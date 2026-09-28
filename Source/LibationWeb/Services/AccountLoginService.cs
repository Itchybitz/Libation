using ApplicationServices;
using AudibleApi;
using AudibleUtilities;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LibationWeb.Services;

/// <summary>One Audible marketplace the portal can register an account with.</summary>
public sealed record LocaleDto(string CountryCode, string Name);

/// <summary>A sign-in waiting for the person to paste back the address their browser landed on.</summary>
public sealed record LoginFlowDto(string FlowId, string AccountId, string Locale, string LoginUrl, DateTimeOffset StartedAt);

/// <summary>How the last sign-in ended: <c>succeeded</c>, <c>failed</c> or <c>cancelled</c>.</summary>
public sealed record LoginResultDto(string AccountId, string Locale, string Status, string? Error, DateTimeOffset FinishedAt);

public sealed record LoginStateDto(LoginFlowDto? Pending, LoginResultDto? Last);

/// <summary>What a Start did: a flow to finish, or nothing because the account already has valid tokens.</summary>
public sealed record LoginStartDto(bool AlreadySignedIn, string AccountId, string Locale, LoginFlowDto? Flow);

/// <summary>Where one account's tokens are written, and whether it still has usable ones.</summary>
public sealed record AccountTarget(bool AlreadySignedIn, string AccountsSettingsFile, string IdentityTokensJsonPath);

/// <summary>
/// AccountsSettings.json held open for the length of one operation, the way LibationCli's login-external holds
/// its persister open for the whole flow: AudibleApi writes the new tokens into that file as the flow completes,
/// and the persister is what saves them. This is a seam, not indirection for its own sake - a unit test must
/// never open the real Libation folder.
/// </summary>
public interface IAccountsSession : IDisposable
{
	/// <summary>Add the row if it is missing, leave an existing one alone, and report what the login needs.</summary>
	AccountTarget Upsert(string accountId, string localeName);

	/// <summary>Remove the row. False when there is no such account for that marketplace.</summary>
	bool Delete(string accountId, string localeName);
}

public delegate IAccountsSession AccountsSessionFactory();

/// <summary>
/// The blocking half of the sign-in. <paramref name="login"/> is handed to AudibleApi, which calls back into it
/// with Amazon's URL and then parks until the response URL arrives.
/// </summary>
public delegate Task LoginFlowRunner(
	ILoginExternal login,
	AudibleApi.Locale locale,
	string accountsSettingsFile,
	string identityTokensJsonPath,
	DeviceRegistrationProfile registrationProfile);

/// <summary>
/// Libation's headless "external browser" sign-in, driven from the portal instead of a terminal. The person
/// opens Amazon's URL in their own browser, signs in there, and pastes the address the browser ends up on back
/// into the page: the password is only ever typed on Amazon's own site. This is exactly the flow LibationCli's
/// login-external verb runs, so an account signed in here is indistinguishable from one signed in there.
///
/// One flow at a time: it is a single file (AccountsSettings.json) plus one device registration, and two
/// sign-ins racing on that would lose tokens.
/// </summary>
public sealed class AccountLoginService
{
	/// <summary>AudibleApi builds the sign-in URL locally, so this only has to cover a pathologically slow start.</summary>
	private const int LoginUrlSeconds = 30;
	/// <summary>How long the person has to sign in and paste before the flow gives up on its own.</summary>
	private const int ResponseMinutes = 20;
	/// <summary>Finishing registers a device with Amazon over the network, which is the slow part.</summary>
	private const int CompleteSeconds = 90;
	/// <summary>A cancelled runner only has to unwind; it is not waiting on Amazon.</summary>
	private const int CancelSeconds = 15;

	private readonly EventHub hub;
	private readonly Func<bool> busy;
	private readonly LoginFlowRunner runner;
	private readonly AccountsSessionFactory sessions;
	private readonly Func<DeviceRegistrationProfile> defaultDeviceProfile;

	private readonly object gate = new();
	private PendingFlow? pending;
	private bool starting;
	private LoginResultDto? last;

	public AccountLoginService(
		EventHub hub,
		Func<bool>? busy = null,
		LoginFlowRunner? runner = null,
		AccountsSessionFactory? sessions = null,
		Func<DeviceRegistrationProfile>? defaultDeviceProfile = null)
	{
		this.hub = hub;
		this.busy = busy ?? (() => LibraryCommands.Scanning);
		this.runner = runner ?? DefaultRunner;
		this.sessions = sessions ?? (() => new LibationAccountsSession());
		// Lazy on purpose: reading it needs Settings.json, which a unit test has no business loading.
		this.defaultDeviceProfile = defaultDeviceProfile ?? (() => Configuration.Instance.GetDeviceRegistrationProfile());
	}

	public LoginStateDto State
	{
		get { lock (gate) return new LoginStateDto(pending?.ToDto(), last); }
	}

	/// <summary>The marketplaces worth offering. Locales that sign in with a username are legacy and are left out, as in the CLI.</summary>
	public static IReadOnlyList<LocaleDto> KnownLocales()
		=> Localization.Locales
			.Where(l => !l.WithUsername)
			.Select(l => new LocaleDto(l.CountryCode, l.Name))
			.ToList();

	/// <summary>
	/// Open a sign-in: create or find the account row, then get Amazon's URL for the person to open.
	/// </summary>
	/// <exception cref="ArgumentException">The account id, locale or device registration is not usable.</exception>
	/// <exception cref="InvalidOperationException">Another sign-in is already waiting.</exception>
	public async Task<LoginStartDto> StartAsync(string? accountId, string? localeInput, string? deviceRegistration)
	{
		// Everything that can be refused without touching AccountsSettings.json is refused first.
		var id = ValidateAccountId(accountId);
		var locale = ResolveLocaleOrThrow(localeInput);
		var profile = ResolveDeviceRegistration(deviceRegistration);

		lock (gate)
		{
			if (pending is not null || starting)
				throw new InvalidOperationException("A sign-in is already waiting for its response URL. Finish or cancel it first.");
			starting = true;
		}

		IAccountsSession? session = null;
		PendingFlow? flow = null;
		try
		{
			session = sessions();
			// Persist by canonical locale name ("germany"), not the user input ("de").
			var target = session.Upsert(id, locale.Name);

			if (target.AlreadySignedIn)
			{
				session.Dispose();
				session = null;
				return new LoginStartDto(true, id, locale.Name, null);
			}

			flow = new PendingFlow(id, locale.Name, session, TimeSpan.FromMinutes(ResponseMinutes));
			session = null; // the flow owns it now, and disposes it when it ends

			// GetResponseUrl blocks its thread for as long as the person takes, so it gets a thread of its own.
			var started = flow;
			flow.Task = Task.Factory
				.StartNew(
					() => runner(started.Login, locale, target.AccountsSettingsFile, target.IdentityTokensJsonPath, profile),
					CancellationToken.None,
					TaskCreationOptions.LongRunning,
					TaskScheduler.Default)
				.Unwrap();

			flow.LoginUrl = await AwaitLoginUrlAsync(flow.LoginUrlSource.Task, flow.Task, TimeSpan.FromSeconds(LoginUrlSeconds));

			lock (gate)
				pending = flow;

			// Record the outcome even when nobody ever calls Complete (the 20 minute give-up, or an error).
			_ = flow.Task.ContinueWith(t => FinishFromTask(started, t), TaskScheduler.Default);

			Serilog.Log.Logger.Information("Web portal: Audible sign-in started for {Account} ({Locale})", id, locale.Name);
			return new LoginStartDto(false, id, locale.Name, flow.ToDto());
		}
		catch
		{
			if (flow is not null)
			{
				flow.Cancel();
				flow.Dispose();
				lock (gate)
					if (ReferenceEquals(pending, flow))
						pending = null;
			}

			session?.Dispose();
			throw;
		}
		finally
		{
			lock (gate)
				starting = false;
		}
	}

	/// <summary>
	/// Hand the pasted address to the waiting sign-in and wait for Amazon to hand back tokens.
	/// </summary>
	/// <exception cref="KeyNotFoundException">No flow with that id is pending.</exception>
	/// <exception cref="ArgumentException">The pasted text is not an absolute https URL.</exception>
	public async Task<LoginResultDto> CompleteAsync(string? flowId, string? responseUrl)
	{
		PendingFlow flow;
		lock (gate)
			flow = MatchPending(flowId) ?? throw new KeyNotFoundException("That sign-in is no longer waiting. Start it again.");

		// Checked before the runner ever sees it, so a mistyped paste leaves the flow open to try again.
		var url = (responseUrl ?? "").Trim();
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
			throw new ArgumentException("Paste the whole address from your browser's address bar, starting with https://.");

		flow.Respond(url);
		try
		{
			await flow.Task.WaitAsync(TimeSpan.FromSeconds(CompleteSeconds));
			Serilog.Log.Logger.Information("Web portal: Audible sign-in succeeded for {Account} ({Locale})", flow.AccountId, flow.LocaleName);
			return Finish(flow, "succeeded", null);
		}
		catch (OperationCanceledException)
		{
			return Finish(flow, "cancelled", null);
		}
		catch (Exception ex)
		{
			// A timeout leaves the runner parked on Amazon; stop it so the next sign-in can have the file.
			flow.Cancel();
			Serilog.Log.Logger.Error(ex, "Web portal: Audible sign-in failed for {Account} ({Locale})", flow.AccountId, flow.LocaleName);
			return Finish(flow, "failed", ErrorExplainer.Explain(ex));
		}
	}

	/// <summary>Abandon the pending sign-in. False when nothing with that id is pending.</summary>
	public async Task<bool> CancelAsync(string? flowId)
	{
		PendingFlow? flow;
		lock (gate)
			flow = MatchPending(flowId);

		if (flow is null)
			return false;

		flow.Cancel();
		// Let the runner unwind before the session is disposed, so its file handle is closed for the next attempt.
		try { await flow.Task.WaitAsync(TimeSpan.FromSeconds(CancelSeconds)); }
		catch { /* however it ended, the flow was cancelled */ }

		Finish(flow, "cancelled", null);
		return true;
	}

	/// <summary>
	/// Forget an account. Its books stay in the library and on disk - that is what Libation itself does, and
	/// the files are the whole point of having downloaded them.
	/// </summary>
	/// <returns>False when there is no such account for that marketplace.</returns>
	/// <exception cref="ArgumentException">The account id or locale is not usable.</exception>
	/// <exception cref="InvalidOperationException">A scan or download is using the account right now.</exception>
	public bool Remove(string? accountId, string? localeInput)
	{
		var id = ValidateAccountId(accountId);
		var locale = ResolveLocaleOrThrow(localeInput);

		if (busy())
			throw new InvalidOperationException("A scan or download is running. Wait for it to finish, then remove the account.");

		bool removed;
		using (var session = sessions())
			removed = session.Delete(id, locale.Name);

		if (removed)
		{
			Serilog.Log.Logger.Information("Web portal: removed Audible account {Account} ({Locale})", id, locale.Name);
			hub.Publish("accounts", State);
		}

		return removed;
	}

	#region validation

	// ResolveLocale and IsEmptyLocale are copied from LibationCli's LoginExternalOptions, where they are
	// internal to that exe and so cannot be referenced from here.

	/// <summary>
	/// Resolve by locale name or country code. Prefers modern (non-pre-amazon) locales for country codes.
	/// Works with older AudibleApi builds where <see cref="Localization.Get"/> only matched names.
	/// </summary>
	internal static AudibleApi.Locale ResolveLocale(string localeInput)
	{
		var fromGet = Localization.Get(localeInput);
		if (!IsEmptyLocale(fromGet))
			return fromGet;

		return Localization.Locales
			.Where(l => l.CountryCode.Equals(localeInput, StringComparison.OrdinalIgnoreCase)
				|| l.Name.Equals(localeInput, StringComparison.OrdinalIgnoreCase))
			.OrderBy(l => l.WithUsername)
			.FirstOrDefault()
			?? AudibleApi.Locale.Empty;
	}

	internal static bool IsEmptyLocale(AudibleApi.Locale locale) => string.IsNullOrEmpty(locale.CountryCode);

	internal static AudibleApi.Locale ResolveLocaleOrThrow(string? localeInput)
	{
		var input = localeInput?.Trim();
		if (string.IsNullOrEmpty(input))
			throw new ArgumentException($"A locale is required. Use a country code or locale name, for example: {LocaleExamples()}");

		var locale = ResolveLocale(input);
		if (IsEmptyLocale(locale))
			throw new ArgumentException($"Unknown locale '{input}'. Use a country code or locale name, for example: {LocaleExamples()}");

		return locale;
	}

	private static string LocaleExamples()
		=> string.Join(", ", KnownLocales().Select(l => $"{l.CountryCode} ({l.Name})"));

	/// <summary>Copied from LibationCli's LoginExternalOptions.TryResolveRegistrationProfile.</summary>
	internal static DeviceRegistrationKind ParseDeviceRegistration(string deviceRegistration)
	{
		if (Enum.TryParse<DeviceRegistrationKind>((deviceRegistration ?? "").Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind))
			return kind;

		throw new ArgumentException($"Unknown device registration '{deviceRegistration}'. Use CurrentAndroid, RetailAndroid, or Mkb79IPhone.");
	}

	internal DeviceRegistrationProfile ResolveDeviceRegistration(string? deviceRegistration)
		=> string.IsNullOrWhiteSpace(deviceRegistration)
			? defaultDeviceProfile()
			: DeviceRegistrationProfile.FromKind(ParseDeviceRegistration(deviceRegistration));

	/// <summary>
	/// The account id is the Audible login. It is only shape-checked: a handful of old accounts are not email
	/// addresses, and Amazon is the one that decides whether it exists.
	/// </summary>
	internal static string ValidateAccountId(string? accountId)
	{
		var id = (accountId ?? "").Trim();
		if (id.Length == 0)
			throw new ArgumentException("An account id is required: the email address you sign in to Audible with.");
		if (id.Length > 200)
			throw new ArgumentException("That account id is too long to be an Audible login.");
		if (id.Length < 3 || id.Any(char.IsWhiteSpace))
			throw new ArgumentException("That does not look like an Audible login. Use the email address you sign in to Audible with.");

		return id;
	}

	#endregion

	private PendingFlow? MatchPending(string? flowId)
		=> pending is { } p && !string.IsNullOrEmpty(flowId) && p.FlowId == flowId ? p : null;

	private static async Task<string> AwaitLoginUrlAsync(Task<string> loginUrl, Task runner, TimeSpan timeout)
	{
		var finished = await Task.WhenAny(loginUrl, runner, Task.Delay(timeout));
		if (finished == loginUrl)
			return await loginUrl;

		if (finished == runner)
		{
			await runner; // rethrows whatever stopped it before the URL was ready
			throw new TimeoutException("The sign-in ended before Audible produced a sign-in URL.");
		}

		throw new TimeoutException($"Audible did not produce a sign-in URL within {timeout.TotalSeconds:0} seconds.");
	}

	/// <summary>
	/// How the runner actually ended decides the outcome. CancelRequested is only the tie-breaker, because a
	/// flow that failed is cancelled a moment later to unpark the runner, and that must not rewrite the reason
	/// the person needs to read.
	/// </summary>
	private void FinishFromTask(PendingFlow flow, Task task)
	{
		if (task.IsCanceled || FindCancellation(task.Exception))
			Finish(flow, "cancelled", null);
		else if (task.Exception is { } ex)
			Finish(flow, "failed", ErrorExplainer.Explain(ex.InnerException ?? ex));
		else if (flow.CancelRequested)
			Finish(flow, "cancelled", null);
		else
			Finish(flow, "succeeded", null);
	}

	private static bool FindCancellation(AggregateException? ex)
		=> ex is not null && ex.Flatten().InnerExceptions.Any(e => e is OperationCanceledException);

	/// <summary>
	/// Record how a flow ended, exactly once. Complete, Cancel and the flow's own continuation all race to get
	/// here; the first one wins and the others read back what it recorded.
	/// </summary>
	private LoginResultDto Finish(PendingFlow flow, string status, string? error)
	{
		LoginResultDto result;
		lock (gate)
		{
			if (!ReferenceEquals(pending, flow))
				return last ?? new LoginResultDto(flow.AccountId, flow.LocaleName, status, error, DateTimeOffset.Now);

			result = new LoginResultDto(flow.AccountId, flow.LocaleName, status, error, DateTimeOffset.Now);
			pending = null;
			last = result;
		}

		flow.Dispose();
		// The page shows "Signed in" straight from AccountsSettings.json, so it has to re-read it.
		hub.Publish("accounts", State);
		return result;
	}

	private sealed class PendingFlow : IDisposable
	{
		private readonly IAccountsSession session;
		private readonly CancellationTokenSource cts = new();
		private int disposed;

		public PendingFlow(string accountId, string localeName, IAccountsSession session, TimeSpan responseTimeout)
		{
			AccountId = accountId;
			LocaleName = localeName;
			this.session = session;
			Login = new WebLoginExternal(LoginUrlSource, ResponseUrlSource, responseTimeout, cts.Token);
		}

		public string FlowId { get; } = Guid.NewGuid().ToString("N");
		public string AccountId { get; }
		public string LocaleName { get; }
		public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
		public string LoginUrl { get; set; } = "";
		public bool CancelRequested { get; private set; }

		public TaskCompletionSource<string> LoginUrlSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<string> ResponseUrlSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ILoginExternal Login { get; }
		public Task Task { get; set; } = Task.CompletedTask;

		public void Respond(string responseUrl) => ResponseUrlSource.TrySetResult(responseUrl);

		public void Cancel()
		{
			CancelRequested = true;
			ResponseUrlSource.TrySetCanceled();
			try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
		}

		public LoginFlowDto ToDto() => new(FlowId, AccountId, LocaleName, LoginUrl, StartedAt);

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0)
				return;

			session.Dispose();
			cts.Dispose();
		}
	}

	/// <summary>
	/// The portal's half of the external-browser sign-in. AudibleApi calls this on the runner's thread and
	/// blocks there, so the URL it hands over is published to the waiting HTTP request and this thread parks
	/// until the pasted address arrives. Same synchronous contract as LibationCli's CliLoginExternal.
	/// </summary>
	private sealed class WebLoginExternal : ILoginExternal
	{
		private readonly TaskCompletionSource<string> loginUrlSource;
		private readonly TaskCompletionSource<string> responseUrlSource;
		private readonly TimeSpan timeout;
		private readonly CancellationToken cancellationToken;

		public WebLoginExternal(
			TaskCompletionSource<string> loginUrlSource,
			TaskCompletionSource<string> responseUrlSource,
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			this.loginUrlSource = loginUrlSource;
			this.responseUrlSource = responseUrlSource;
			this.timeout = timeout;
			this.cancellationToken = cancellationToken;
		}

		public string GetResponseUrl(string loginUrl, CookieCollection signInCookies)
		{
			loginUrlSource.TrySetResult(loginUrl);

			try
			{
				return responseUrlSource.Task.WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult();
			}
			catch (TimeoutException)
			{
				// The person walked away. Same outcome as a cancel, and the runner unwinds the same way.
				throw new OperationCanceledException("The sign-in was not finished in time.");
			}
		}
	}

	/// <summary>Libation's real AccountsSettings.json, opened for the length of one flow.</summary>
	private sealed class LibationAccountsSession : IAccountsSession
	{
		private readonly AccountsSettingsPersister persister = AudibleApiStorage.GetAccountsSettingsPersister();

		public AccountTarget Upsert(string accountId, string localeName)
		{
			var account = persister.AccountsSettings.Upsert(accountId, localeName);
			return new AccountTarget(
				account.IdentityTokens?.IsValid == true,
				AudibleApiStorage.AccountsSettingsFile,
				account.GetIdentityTokensJsonPath());
		}

		public bool Delete(string accountId, string localeName) => persister.AccountsSettings.Delete(accountId, localeName);

		public void Dispose() => persister.Dispose();
	}

	/// <summary>The real sign-in: the same EzApiCreator call LibationCli's login-external makes.</summary>
	private static readonly LoginFlowRunner DefaultRunner = async (login, locale, accountsSettingsFile, identityTokensJsonPath, registrationProfile) =>
		_ = await EzApiCreator.GetApiAsync(login, locale, accountsSettingsFile, identityTokensJsonPath, registrationProfile);
}

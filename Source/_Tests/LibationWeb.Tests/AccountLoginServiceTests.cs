using AudibleApi;
using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace LibationWeb.Tests;

/// <summary>
/// The sign-in flow with Amazon replaced by a fake runner. Nothing here opens AccountsSettings.json, reads
/// Settings.json or reaches the network: the service takes both the accounts session and the flow runner as
/// seams precisely so these can be substituted.
/// </summary>
[TestClass]
public class AccountLoginServiceTests
{
	private const string FakeLoginUrl = "https://www.amazon.com/ap/signin?openid.mode=checkid_setup&fake=1";
	private const string PastedUrl = "https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=ABC123";
	/// <summary>Stands in for Settings.json's device registration, which a unit test has no business loading.</summary>
	private static readonly DeviceRegistrationProfile DefaultProfile = DeviceRegistrationProfile.FromKind(DeviceRegistrationKind.CurrentAndroid);

	private FakeAccounts accounts = new();
	private FakeRunner runner = new();

	[TestInitialize]
	public void Setup()
	{
		accounts = new FakeAccounts();
		runner = new FakeRunner();
	}

	private AccountLoginService Create(Func<bool>? busy = null)
		=> new(
			new EventHub(),
			busy: busy ?? (() => false),
			runner: runner.RunAsync,
			sessions: () => accounts.Open(),
			defaultDeviceProfile: () => DefaultProfile);

	#region the flow

	[TestMethod]
	public void Nothing_is_pending_before_a_sign_in_is_started()
	{
		var state = Create().State;

		Assert.IsNull(state.Pending);
		Assert.IsNull(state.Last);
	}

	[TestMethod]
	public async Task Starting_publishes_the_url_to_open_and_leaves_the_flow_waiting()
	{
		var service = Create();

		var started = await service.StartAsync("  me@example.com  ", "us", null);

		Assert.IsFalse(started.AlreadySignedIn);
		Assert.IsNotNull(started.Flow);
		Assert.AreEqual("me@example.com", started.Flow.AccountId, "the account id is trimmed");
		Assert.AreEqual("us", started.Flow.Locale);
		Assert.AreEqual(FakeLoginUrl, started.Flow.LoginUrl);
		Assert.AreNotEqual("", started.Flow.FlowId);

		var state = service.State;
		Assert.IsNotNull(state.Pending);
		Assert.AreEqual(started.Flow.FlowId, state.Pending.FlowId);
		Assert.AreEqual(FakeLoginUrl, state.Pending.LoginUrl);
		Assert.IsNull(state.Last, "nothing has finished yet");

		// The account row is created up front, exactly as login-external does it.
		CollectionAssert.AreEqual(new[] { "me@example.com|us" }, accounts.Upserts.ToArray());
		Assert.IsNull(runner.ResponseUrlReceived, "the runner is still parked waiting for the paste");

		await service.CancelAsync(started.Flow.FlowId);
	}

	[TestMethod]
	public async Task The_locale_is_canonicalized_before_the_account_row_is_written()
	{
		var service = Create();

		var started = await service.StartAsync("me@example.com", "de", null);

		Assert.IsNotNull(started.Flow);
		Assert.AreEqual("germany", started.Flow.Locale);
		CollectionAssert.AreEqual(new[] { "me@example.com|germany" }, accounts.Upserts.ToArray());
		Assert.AreEqual("germany", runner.Locale?.Name);

		await service.CancelAsync(started.Flow.FlowId);
	}

	[TestMethod]
	public async Task Completing_hands_the_pasted_url_to_the_runner_and_records_success()
	{
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		var result = await service.CompleteAsync(started.Flow.FlowId, "  " + PastedUrl + "  ");

		Assert.AreEqual("succeeded", result.Status);
		Assert.IsNull(result.Error);
		Assert.AreEqual(PastedUrl, runner.ResponseUrlReceived, "trimmed, and handed straight to AudibleApi");

		var state = service.State;
		Assert.IsNull(state.Pending);
		Assert.IsNotNull(state.Last);
		Assert.AreEqual("succeeded", state.Last.Status);
		Assert.AreEqual("me@example.com", state.Last.AccountId);
		Assert.AreEqual("us", state.Last.Locale);
		Assert.AreEqual(1, accounts.Disposed, "the accounts file is released when the flow ends");
	}

	[TestMethod]
	public async Task Cancelling_ends_the_flow_as_cancelled()
	{
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		Assert.IsTrue(await service.CancelAsync(started.Flow.FlowId));

		var state = service.State;
		Assert.IsNull(state.Pending);
		Assert.IsNotNull(state.Last);
		Assert.AreEqual("cancelled", state.Last.Status);
		Assert.IsNull(runner.ResponseUrlReceived, "the runner never got a URL");
		Assert.AreEqual(1, accounts.Disposed);

		Assert.IsFalse(await service.CancelAsync(started.Flow.FlowId), "there is nothing left to cancel");
	}

	[TestMethod]
	public async Task A_second_sign_in_while_one_is_pending_is_refused()
	{
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(
			() => service.StartAsync("other@example.com", "uk", null));

		Assert.AreEqual(1, accounts.Opened, "the second attempt never opened the accounts file");
		Assert.AreEqual(started.Flow.FlowId, service.State.Pending?.FlowId);

		// ...and once the first one is out of the way, another may start.
		await service.CancelAsync(started.Flow.FlowId);
		var second = await service.StartAsync("other@example.com", "uk", null);
		Assert.IsNotNull(second.Flow);
		await service.CancelAsync(second.Flow.FlowId);
	}

	[TestMethod]
	public async Task An_account_that_still_has_valid_tokens_starts_no_flow()
	{
		accounts.AlreadySignedIn = true;
		var service = Create();

		var started = await service.StartAsync("me@example.com", "us", null);

		Assert.IsTrue(started.AlreadySignedIn);
		Assert.IsNull(started.Flow);
		Assert.AreEqual("me@example.com", started.AccountId);
		Assert.AreEqual("us", started.Locale);
		Assert.IsNull(service.State.Pending);
		Assert.AreEqual(1, accounts.Disposed, "the accounts file is not held open for a flow that never starts");
	}

	[DataTestMethod]
	[DataRow("http://www.amazon.com/ap/maplanding?x=1", DisplayName = "plain http")]
	[DataRow("/ap/maplanding?x=1", DisplayName = "relative")]
	[DataRow("www.amazon.com/ap/maplanding", DisplayName = "no scheme")]
	[DataRow("not a url at all", DisplayName = "garbage")]
	[DataRow("", DisplayName = "empty")]
	public async Task A_response_url_that_is_not_absolute_https_never_reaches_the_runner(string responseUrl)
	{
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		await Assert.ThrowsExactlyAsync<ArgumentException>(
			() => service.CompleteAsync(started.Flow.FlowId, responseUrl));

		Assert.IsNull(runner.ResponseUrlReceived);
		Assert.IsNotNull(service.State.Pending, "the flow stays open so the paste can be corrected");

		// the corrected paste still works
		var result = await service.CompleteAsync(started.Flow.FlowId, PastedUrl);
		Assert.AreEqual("succeeded", result.Status);
	}

	[TestMethod]
	public async Task Completing_an_unknown_flow_is_refused()
	{
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
			() => service.CompleteAsync("not-the-flow-id", PastedUrl));
		await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
			() => service.CompleteAsync(null, PastedUrl));

		await service.CancelAsync(started.Flow.FlowId);
	}

	[TestMethod]
	public async Task A_sign_in_that_throws_is_recorded_as_failed_with_its_message()
	{
		runner.ThrowAfterResponse = new InvalidOperationException("Amazon said no");
		var service = Create();
		var started = await service.StartAsync("me@example.com", "us", null);
		Assert.IsNotNull(started.Flow);

		var result = await service.CompleteAsync(started.Flow.FlowId, PastedUrl);

		Assert.AreEqual("failed", result.Status);
		Assert.AreEqual("Amazon said no", result.Error);
		Assert.IsNull(service.State.Pending);
		Assert.AreEqual("failed", service.State.Last?.Status);
		Assert.AreEqual(1, accounts.Disposed);
	}

	[TestMethod]
	public async Task The_runner_is_handed_the_paths_and_device_profile_the_account_needs()
	{
		var service = Create();

		var started = await service.StartAsync("me@example.com", "us", "mkb79iphone");

		Assert.IsNotNull(started.Flow);
		Assert.AreEqual(FakeAccounts.SettingsFile, runner.AccountsSettingsFile);
		Assert.AreEqual("$.tokens[me@example.com|us]", runner.IdentityTokensJsonPath);
		Assert.IsNotNull(runner.Profile);
		Assert.AreNotSame(DefaultProfile, runner.Profile, "the requested device registration, not the configured default");

		await service.CancelAsync(started.Flow.FlowId);
	}

	#endregion

	#region locales

	[TestMethod]
	public void A_locale_resolves_by_country_code_or_by_name()
	{
		Assert.AreEqual("us", AccountLoginService.ResolveLocale("us").Name);
		Assert.AreEqual("us", AccountLoginService.ResolveLocale("US").Name);

		// "de" is the country code, "germany" the name Libation stores.
		var byName = AccountLoginService.ResolveLocale("germany");
		var byCode = AccountLoginService.ResolveLocale("de");
		Assert.AreEqual("germany", byName.Name);
		Assert.AreEqual(byName.Name, byCode.Name);
		Assert.AreEqual(byName.CountryCode, byCode.CountryCode);
	}

	[TestMethod]
	public void An_unknown_locale_is_refused_and_the_message_lists_the_real_ones()
	{
		Assert.IsTrue(AccountLoginService.IsEmptyLocale(AccountLoginService.ResolveLocale("atlantis")));

		var ex = Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ResolveLocaleOrThrow("atlantis"));
		StringAssert.Contains(ex.Message, "atlantis");
		StringAssert.Contains(ex.Message, "us (us)");

		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ResolveLocaleOrThrow(null));
		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ResolveLocaleOrThrow("   "));
	}

	[TestMethod]
	public void The_offered_locales_leave_out_the_legacy_username_ones()
	{
		var locales = AccountLoginService.KnownLocales();

		Assert.IsTrue(locales.Count > 0);
		Assert.IsTrue(locales.Any(l => l.CountryCode == "us"));
		Assert.IsTrue(locales.All(l => !string.IsNullOrEmpty(l.CountryCode) && !string.IsNullOrEmpty(l.Name)));
		Assert.AreEqual(
			Localization.Locales.Count(l => !l.WithUsername),
			locales.Count);
	}

	#endregion

	#region device registration and account ids

	[DataTestMethod]
	[DataRow("CurrentAndroid", DeviceRegistrationKind.CurrentAndroid)]
	[DataRow("currentandroid", DeviceRegistrationKind.CurrentAndroid)]
	[DataRow("  RETAILANDROID  ", DeviceRegistrationKind.RetailAndroid)]
	[DataRow("mkb79iPhone", DeviceRegistrationKind.Mkb79IPhone)]
	public void Device_registration_is_matched_without_regard_to_case(string input, DeviceRegistrationKind expected)
		=> Assert.AreEqual(expected, AccountLoginService.ParseDeviceRegistration(input));

	[DataTestMethod]
	[DataRow("iPad")]
	[DataRow("99")]
	[DataRow("")]
	public void An_unknown_device_registration_is_refused(string input)
		=> Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ParseDeviceRegistration(input));

	[TestMethod]
	public void No_device_registration_means_the_configured_default()
	{
		var service = Create();

		Assert.AreSame(DefaultProfile, service.ResolveDeviceRegistration(null));
		Assert.AreSame(DefaultProfile, service.ResolveDeviceRegistration("  "));
	}

	[TestMethod]
	public void An_account_id_is_trimmed_and_obvious_rubbish_is_refused()
	{
		Assert.AreEqual("me@example.com", AccountLoginService.ValidateAccountId("  me@example.com  "));
		Assert.AreEqual("old-style-login", AccountLoginService.ValidateAccountId("old-style-login"));

		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ValidateAccountId(null), "null");
		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ValidateAccountId("   "), "whitespace only");
		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ValidateAccountId("ab"), "too short");
		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ValidateAccountId("me @example.com"), "has a space");
		Assert.ThrowsExactly<ArgumentException>(() => AccountLoginService.ValidateAccountId(new string('a', 201)), "too long");
	}

	[TestMethod]
	public async Task A_bad_account_id_or_locale_never_opens_the_accounts_file()
	{
		var service = Create();

		await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.StartAsync("", "us", null));
		await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.StartAsync("me@example.com", "atlantis", null));
		await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.StartAsync("me@example.com", "us", "iPad"));

		Assert.AreEqual(0, accounts.Opened);
		Assert.IsNull(service.State.Pending);
	}

	#endregion

	#region removing

	[TestMethod]
	public void Removing_an_account_deletes_its_row_and_leaves_the_books_alone()
	{
		var service = Create();

		Assert.IsTrue(service.Remove("  me@example.com ", "de"));

		CollectionAssert.AreEqual(new[] { "me@example.com|germany" }, accounts.Deletes.ToArray());
		Assert.AreEqual(1, accounts.Disposed);
	}

	[TestMethod]
	public void Removing_an_account_that_is_not_there_reports_nothing_removed()
	{
		accounts.DeleteResult = false;

		Assert.IsFalse(Create().Remove("me@example.com", "us"));
	}

	[TestMethod]
	public void An_account_cannot_be_removed_while_a_scan_or_download_is_using_it()
	{
		var service = Create(busy: () => true);

		Assert.ThrowsExactly<InvalidOperationException>(() => service.Remove("me@example.com", "us"));
		Assert.AreEqual(0, accounts.Opened, "the accounts file is not even opened");
	}

	#endregion

	#region fakes

	/// <summary>AccountsSettings.json, without a file. Records what the service asked it to do.</summary>
	private sealed class FakeAccounts : IAccountsSession
	{
		public const string SettingsFile = @"C:\fake\AccountsSettings.json";

		public bool AlreadySignedIn { get; set; }
		public bool DeleteResult { get; set; } = true;
		public int Opened { get; private set; }
		public int Disposed { get; private set; }
		public List<string> Upserts { get; } = [];
		public List<string> Deletes { get; } = [];

		public IAccountsSession Open()
		{
			Opened++;
			return this;
		}

		public AccountTarget Upsert(string accountId, string localeName)
		{
			Upserts.Add($"{accountId}|{localeName}");
			return new AccountTarget(AlreadySignedIn, SettingsFile, $"$.tokens[{accountId}|{localeName}]");
		}

		public bool Delete(string accountId, string localeName)
		{
			Deletes.Add($"{accountId}|{localeName}");
			return DeleteResult;
		}

		public void Dispose() => Disposed++;
	}

	/// <summary>
	/// AudibleApi's part: it calls GetResponseUrl on the thread the service gave it and blocks there until the
	/// pasted URL arrives, which is the whole reason the service needs a thread of its own.
	/// </summary>
	private sealed class FakeRunner
	{
		public Locale? Locale { get; private set; }
		public string? AccountsSettingsFile { get; private set; }
		public string? IdentityTokensJsonPath { get; private set; }
		public DeviceRegistrationProfile? Profile { get; private set; }
		public string? ResponseUrlReceived { get; private set; }
		public Exception? ThrowAfterResponse { get; set; }

		public Task RunAsync(
			ILoginExternal login,
			Locale locale,
			string accountsSettingsFile,
			string identityTokensJsonPath,
			DeviceRegistrationProfile registrationProfile)
		{
			Locale = locale;
			AccountsSettingsFile = accountsSettingsFile;
			IdentityTokensJsonPath = identityTokensJsonPath;
			Profile = registrationProfile;

			var url = login.GetResponseUrl(FakeLoginUrl, new CookieCollection());
			if (ThrowAfterResponse is not null)
				throw ThrowAfterResponse;

			ResponseUrlReceived = url;
			return Task.CompletedTask;
		}
	}

	#endregion
}

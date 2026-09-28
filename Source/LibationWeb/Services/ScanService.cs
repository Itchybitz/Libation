using ApplicationServices;
using AudibleUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibationWeb.Services;

public sealed record AccountDto(string AccountId, string Name, string Locale, bool LibraryScan, bool Authenticated, string[] AlsoScans);

public sealed record ScanState(
	bool Running,
	DateTimeOffset? StartedAt,
	DateTimeOffset? FinishedAt,
	IReadOnlyList<string> Accounts,
	int TotalProcessed,
	int NewBooks,
	string? Error);

/// <summary>
/// Runs Libation's library scan the way the CLI does - never an interactive login - one at a time, and
/// keeps the outcome of the last run for the status card. Progress goes out over the event hub.
/// </summary>
public sealed class ScanService
{
	private readonly EventHub hub;
	private readonly LibraryService library;
	private readonly object gate = new();
	private ScanState state = new(false, null, null, [], 0, 0, null);

	public ScanService(EventHub hub, LibraryService library)
	{
		this.hub = hub;
		this.library = library;
	}

	public ScanState State
	{
		get { lock (gate) return state; }
	}

	/// <summary>
	/// The account list, or the reason it cannot be read (typically tokens encrypted on another machine
	/// without the master key). The status card shows the reason instead of failing the whole page.
	/// </summary>
	public (IReadOnlyList<AccountDto> Accounts, string? Error) TryListAccounts()
	{
		try
		{
			return (ListAccounts(), null);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Web portal: could not read AccountsSettings.json");
			return ([], ErrorExplainer.Explain(ex));
		}
	}

	public IReadOnlyList<AccountDto> ListAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.GetAll()
			.Select(a => new AccountDto(
				a.AccountId,
				a.AccountName ?? "",
				a.Locale?.Name ?? "",
				a.LibraryScan,
				a.IdentityTokens?.IsValid == true,
				a.AdditionalLocales.Select(l => l.Name).ToArray()))
			.ToList();
	}

	/// <summary>Start a scan of every account, or of the named ones (ids or nicknames, case-insensitive).</summary>
	/// <exception cref="InvalidOperationException">A scan is already running, or no account matched.</exception>
	public ScanState Start(IReadOnlyList<string>? accountFilter)
	{
		Account[] accounts;
		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
		{
			var all = persister.AccountsSettings.GetAll().ToArray();
			accounts = accountFilter is { Count: > 0 }
				? all.Where(a => accountFilter.Any(f => f.Equals(a.AccountId, StringComparison.OrdinalIgnoreCase) || f.Equals(a.AccountName, StringComparison.OrdinalIgnoreCase))).ToArray()
				: all;
		}

		if (accounts.Length == 0)
			throw new InvalidOperationException(accountFilter is { Count: > 0 }
				? "None of the named accounts exist."
				: "No Audible accounts are configured. Add one with 'LibationCli login-external' or in the desktop app.");

		lock (gate)
		{
			if (state.Running || LibraryCommands.Scanning)
				throw new InvalidOperationException("A scan is already running.");

			state = new ScanState(true, DateTimeOffset.Now, null, accounts.Select(a => a.AccountName ?? a.AccountId).ToList(), 0, 0, null);
		}

		hub.Publish("scan", state);
		_ = Task.Run(() => RunAsync(accounts));
		return State;
	}

	private async Task RunAsync(Account[] accounts)
	{
		int total = 0, added = 0;
		string? error = null;
		try
		{
			Serilog.Log.Logger.Information("Web portal: scanning {Count} account(s)", accounts.Length);
			(total, added) = await LibraryCommands.ImportAccountAsync(accounts, allowInteractiveLogin: false);
			Serilog.Log.Logger.Information("Web portal: scan complete. Processed {Total}, new {New}", total, added);
		}
		catch (Exception ex)
		{
			error = ErrorExplainer.Explain(ex);
			Serilog.Log.Logger.Error(ex, "Web portal: scan failed");
		}
		finally
		{
			library.Invalidate();
			lock (gate)
				state = state with { Running = false, FinishedAt = DateTimeOffset.Now, TotalProcessed = total, NewBooks = added, Error = error };
			hub.Publish("scan", State);
			hub.Publish("library", new { reason = "scan" });
		}
	}
}

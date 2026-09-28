using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibationWeb.Services;

/// <summary>What the page shows about the schedule: the settings plus what the scheduler has done with them.</summary>
public sealed record ScheduleDto(
	string Mode,
	int EveryHours,
	bool DownloadAfterScan,
	DateTimeOffset? NextScanAt,
	DateTimeOffset? LastAutomaticScanAt,
	string? LastAutomaticScanError);

/// <summary>
/// Everything the scheduler needs from the rest of the portal, as delegates. This is a seam, not
/// indirection for its own sake: a unit test must never start a real Audible scan.
/// </summary>
public sealed class ScanSchedulerHooks
{
	/// <summary>The scan service's own state: whether one is running, when the last one ended, how it ended.</summary>
	public required Func<ScanState> ScanState { get; init; }
	/// <summary>Libation is scanning outside the portal (the desktop app, the CLI, a job started before a restart).</summary>
	public required Func<bool> LibraryBusy { get; init; }
	/// <summary>An Audible sign-in is waiting for the person to paste their response URL.</summary>
	public required Func<bool> SignInPending { get; init; }
	/// <summary>The queue is working on something.</summary>
	public required Func<bool> DownloadsWorking { get; init; }
	/// <summary>Start a scan of every account. Throws <see cref="InvalidOperationException"/> when it cannot.</summary>
	public required Action StartScan { get; init; }
	/// <summary>Queue everything not downloaded yet, exactly as <c>POST /api/queue/new</c> does. Returns how many were added.</summary>
	public required Func<int> EnqueueNewDownloads { get; init; }
}

/// <summary>
/// Scans Audible on a timer when the portal is set to do so. It wakes once a minute rather than sleeping
/// until the due time, so that a setting changed from the page, a manual scan or a container restart is
/// noticed within a minute without any of them having to interrupt a sleeping task.
/// </summary>
public sealed class ScanScheduler : IHostedService
{
	/// <summary>How long after the portal starts (or automatic scanning is turned on) the first scan may run.</summary>
	public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

	private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

	private readonly PortalSettingsStore store;
	private readonly EventHub hub;
	private readonly ScanSchedulerHooks hooks;
	private readonly TimeProvider time;
	private readonly CancellationTokenSource shutdown = new();
	private readonly object gate = new();

	private PortalSettingsDto settings;
	private DateTimeOffset enabledAt;
	private DateTimeOffset? lastAutomaticScanAt;
	private string? lastAutomaticScanError;
	/// <summary>True between starting an automatic scan and noticing that it has ended.</summary>
	private bool automaticScanRunning;
	private Task? loop;

	public ScanScheduler(PortalSettingsStore store, EventHub hub, ScanSchedulerHooks hooks, TimeProvider? time = null)
	{
		this.store = store;
		this.hub = hub;
		this.hooks = hooks;
		this.time = time ?? TimeProvider.System;
		settings = store.Load();
		enabledAt = this.time.GetUtcNow();
	}

	public PortalSettingsDto Settings
	{
		get { lock (gate) return settings; }
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		var current = Settings;
		if (current.ScanMode == PortalSettingsStore.Automatic)
			Serilog.Log.Logger.Information(
				"Web portal: automatic scans every {Hours}h, the first no sooner than {Grace} minutes from now. Download afterwards: {Download}",
				current.ScanEveryHours, Grace.TotalMinutes, current.DownloadAfterScan);

		loop ??= Task.Run(() => LoopAsync(shutdown.Token), CancellationToken.None);
		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await shutdown.CancelAsync();
		if (loop is not null)
		{
			try { await loop.WaitAsync(cancellationToken); }
			catch { /* shutting down */ }
		}
	}

	/// <summary>Re-read the settings after a save and report the new schedule.</summary>
	public ScheduleDto Reload()
	{
		var reloaded = store.Load();
		var now = time.GetUtcNow();
		lock (gate)
		{
			// Turning automatic scanning on restarts the grace period: nobody expects a scan the instant
			// they touch the select, and the interval is measured from a scan, not from this moment.
			if (reloaded.ScanMode == PortalSettingsStore.Automatic && settings.ScanMode != PortalSettingsStore.Automatic)
				enabledAt = now;
			settings = reloaded;
		}
		return Schedule();
	}

	public ScheduleDto Schedule()
	{
		var lastScanFinishedAt = hooks.ScanState().FinishedAt;
		var now = time.GetUtcNow();
		lock (gate)
			return new ScheduleDto(
				settings.ScanMode,
				settings.ScanEveryHours,
				settings.DownloadAfterScan,
				ComputeNextScan(settings, enabledAt, lastScanFinishedAt, now),
				lastAutomaticScanAt,
				lastAutomaticScanError);
	}

	/// <summary>
	/// When the next automatic scan is due, or null when the portal only scans on request. The grace
	/// period covers a container that is recreated often - it would otherwise scan on every start - and
	/// counting the interval from the end of the last scan means a scan started by hand pushes the next
	/// automatic one out instead of the two running back to back.
	/// </summary>
	/// <returns>Null, or a moment no earlier than <paramref name="now"/> - an overdue scan reads as "now".</returns>
	public static DateTimeOffset? ComputeNextScan(
		PortalSettingsDto settings,
		DateTimeOffset enabledAt,
		DateTimeOffset? lastScanFinishedAt,
		DateTimeOffset now)
	{
		if (settings.ScanMode != PortalSettingsStore.Automatic)
			return null;

		var earliest = enabledAt + Grace;
		if (lastScanFinishedAt is { } finished)
		{
			var afterInterval = finished + TimeSpan.FromHours(settings.ScanEveryHours);
			if (afterInterval > earliest)
				earliest = afterInterval;
		}

		return earliest > now ? earliest : now;
	}

	private async Task LoopAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TickInterval, time);
		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				try { Tick(); }
				catch (Exception ex) { Serilog.Log.Logger.Error(ex, "Web portal: the scan scheduler stumbled; it will try again in a minute"); }
			}
		}
		catch (OperationCanceledException)
		{
			// normal shutdown
		}
	}

	/// <summary>One minute's worth of decisions. Internal so a test can run it against a clock it controls.</summary>
	internal void Tick()
	{
		var scan = hooks.ScanState();
		var now = time.GetUtcNow();

		if (automaticScanRunning && !scan.Running && !hooks.LibraryBusy())
			FinishAutomaticScan(scan);

		PortalSettingsDto current;
		DateTimeOffset? due;
		lock (gate)
		{
			current = settings;
			due = ComputeNextScan(current, enabledAt, scan.FinishedAt, now);
		}

		if (due is null || due > now)
			return;

		if (Blocked(scan) is string reason)
		{
			// Only postponed: a due scan is looked at again on the next tick.
			Serilog.Log.Logger.Debug("Web portal: an automatic scan is due but {Reason}", reason);
			return;
		}

		try
		{
			Serilog.Log.Logger.Information("Web portal: starting the automatic scan (every {Hours}h)", current.ScanEveryHours);
			hooks.StartScan();
			lock (gate)
			{
				automaticScanRunning = true;
				lastAutomaticScanAt = now;
				lastAutomaticScanError = null;
			}
		}
		catch (InvalidOperationException ex)
		{
			// No accounts configured, or something took the scan between the check and here. Next minute, then.
			Serilog.Log.Logger.Warning("Web portal: the automatic scan could not start: {Reason}", ex.Message);
			return;
		}

		hub.Publish("schedule", Schedule());
	}

	private string? Blocked(ScanState scan)
	{
		if (scan.Running || hooks.LibraryBusy())
			return "a scan is already running";
		if (hooks.SignInPending())
			return "an Audible sign-in is waiting to be finished";
		if (hooks.DownloadsWorking())
			return "the queue is still downloading";
		return null;
	}

	private void FinishAutomaticScan(ScanState scan)
	{
		lock (gate)
		{
			automaticScanRunning = false;
			lastAutomaticScanError = scan.Error;
		}

		if (scan.Error is not null)
			Serilog.Log.Logger.Warning("Web portal: the automatic scan failed: {Error}", scan.Error);
		else
			Serilog.Log.Logger.Information("Web portal: the automatic scan finished. Processed {Total}, new {New}", scan.TotalProcessed, scan.NewBooks);

		if (scan.Error is null && Settings.DownloadAfterScan)
		{
			try
			{
				var added = hooks.EnqueueNewDownloads();
				Serilog.Log.Logger.Information("Web portal: queued {Count} title(s) after the automatic scan", added);
			}
			catch (Exception ex)
			{
				// The scan is the valuable half; a queue that refuses is not worth losing it over.
				Serilog.Log.Logger.Error(ex, "Web portal: could not queue the new titles after the automatic scan");
			}
		}

		hub.Publish("schedule", Schedule());
	}
}

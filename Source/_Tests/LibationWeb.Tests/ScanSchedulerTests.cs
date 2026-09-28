using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace LibationWeb.Tests;

/// <summary>
/// The automatic scan, decided against a clock the test moves by hand. Nothing here starts a scan, reads
/// AccountsSettings.json or queues a download: the scheduler takes all of that as delegates so that a
/// unit test can never reach Audible.
/// </summary>
[TestClass]
public class ScanSchedulerTests
{
	private static readonly DateTimeOffset Start = new(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan Grace = ScanScheduler.Grace;
	private static readonly PortalSettingsDto Manual = new("manual", 6, false);

	private static PortalSettingsDto Automatic(int everyHours = 6, bool downloadAfterScan = false)
		=> new("automatic", everyHours, downloadAfterScan);

	/// <summary>A scan service that is doing nothing, having last finished when it says.</summary>
	private static ScanState Idle(DateTimeOffset? finishedAt, string? error = null)
		=> new(false, null, finishedAt, [], 12, 3, error);

	private string dir = "";
	private string file = "";
	private TestTime time = new(Start);
	private ScanState scan = Idle(null);
	private int scansStarted;
	private int enqueueCalls;
	private bool libraryBusy;
	private bool signInPending;
	private bool downloadsWorking;

	[TestInitialize]
	public void Setup()
	{
		dir = Path.Combine(Path.GetTempPath(), "LibationWeb.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		file = Path.Combine(dir, PortalSettingsStore.FileName);
		time = new TestTime(Start);
		scan = Idle(null);
		scansStarted = enqueueCalls = 0;
		libraryBusy = signInPending = downloadsWorking = false;
	}

	[TestCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
	}

	private PortalSettingsStore Store() => new(() => file);

	private ScanScheduler Create(PortalSettingsDto settings)
	{
		var store = Store();
		store.Save(settings);
		return new ScanScheduler(store, new EventHub(), new ScanSchedulerHooks
		{
			ScanState = () => scan,
			LibraryBusy = () => libraryBusy,
			SignInPending = () => signInPending,
			DownloadsWorking = () => downloadsWorking,
			StartScan = () =>
			{
				scansStarted++;
				scan = new ScanState(true, time.GetUtcNow(), null, [], 0, 0, null);
			},
			EnqueueNewDownloads = () => { enqueueCalls++; return 2; },
		}, time);
	}

	/// <summary>The scan the scheduler just started has ended.</summary>
	private void ScanEnds(string? error = null) => scan = Idle(time.GetUtcNow(), error);

	#region when the next scan is due

	[TestMethod]
	public void Manual_is_never_due()
	{
		Assert.IsNull(ScanScheduler.ComputeNextScan(Manual, Start, null, Start));
		Assert.IsNull(ScanScheduler.ComputeNextScan(Manual, Start, Start.AddDays(-7), Start));
	}

	[TestMethod]
	public void Right_after_the_portal_starts_the_grace_period_wins()
	{
		// The last scan was long enough ago that the interval alone would say "now"; a container that is
		// recreated every few minutes would then scan on every start.
		Assert.AreEqual(Start + Grace, ScanScheduler.ComputeNextScan(Automatic(6), Start, Start.AddHours(-10), Start));
		Assert.AreEqual(Start + Grace, ScanScheduler.ComputeNextScan(Automatic(6), Start, null, Start));
	}

	[TestMethod]
	public void Once_the_grace_period_is_past_the_interval_after_the_last_scan_wins()
	{
		var finished = Start.AddMinutes(10);

		var next = ScanScheduler.ComputeNextScan(Automatic(6), Start, finished, Start.AddMinutes(11));

		Assert.AreEqual(finished.AddHours(6), next);
	}

	[TestMethod]
	public void A_scan_started_by_hand_pushes_the_next_automatic_one_out()
	{
		var automatic = Automatic(6);
		var now = Start.AddMinutes(11);

		var beforeTheManualScan = ScanScheduler.ComputeNextScan(automatic, Start, Start.AddMinutes(10), now);
		var afterTheManualScan = ScanScheduler.ComputeNextScan(automatic, Start, Start.AddMinutes(70), now);

		Assert.AreEqual(TimeSpan.FromHours(1), afterTheManualScan - beforeTheManualScan, "the interval runs from the end of the last scan, whoever started it");
	}

	[TestMethod]
	public void A_scan_that_is_overdue_reads_as_now()
	{
		var now = Start.AddHours(9);

		Assert.AreEqual(now, ScanScheduler.ComputeNextScan(Automatic(1), Start, Start.AddHours(-10), now));
	}

	#endregion

	#region the tick

	[TestMethod]
	public void A_due_scan_is_started_and_recorded()
	{
		var scheduler = Create(Automatic());
		time.Advance(TimeSpan.FromMinutes(4));

		scheduler.Tick();
		Assert.AreEqual(0, scansStarted, "the grace period is not over yet");

		time.Advance(TimeSpan.FromMinutes(2));
		scheduler.Tick();

		Assert.AreEqual(1, scansStarted);
		Assert.AreEqual(time.GetUtcNow(), scheduler.Schedule().LastAutomaticScanAt);
	}

	[TestMethod]
	public void Manual_mode_never_starts_anything()
	{
		var scheduler = Create(Manual);
		time.Advance(TimeSpan.FromDays(2));

		scheduler.Tick();

		Assert.AreEqual(0, scansStarted);
		Assert.IsNull(scheduler.Schedule().NextScanAt);
	}

	[TestMethod]
	public void Nothing_starts_while_a_scan_is_already_running()
	{
		var scheduler = Create(Automatic());
		time.Advance(Grace + TimeSpan.FromMinutes(1));

		scan = new ScanState(true, Start, null, [], 0, 0, null);
		scheduler.Tick();
		Assert.AreEqual(0, scansStarted, "the portal's own scan service is busy");

		scan = Idle(null);
		libraryBusy = true;
		scheduler.Tick();
		Assert.AreEqual(0, scansStarted, "Libation is scanning somewhere else");

		libraryBusy = false;
		scheduler.Tick();
		Assert.AreEqual(1, scansStarted, "a due scan is only postponed, never dropped");
	}

	[TestMethod]
	public void Nothing_starts_while_an_audible_sign_in_is_waiting()
	{
		var scheduler = Create(Automatic());
		time.Advance(Grace + TimeSpan.FromMinutes(1));
		signInPending = true;

		scheduler.Tick();
		Assert.AreEqual(0, scansStarted);

		signInPending = false;
		scheduler.Tick();
		Assert.AreEqual(1, scansStarted);
	}

	[TestMethod]
	public void Nothing_starts_while_the_queue_is_downloading()
	{
		var scheduler = Create(Automatic());
		time.Advance(Grace + TimeSpan.FromMinutes(1));
		downloadsWorking = true;

		scheduler.Tick();

		Assert.AreEqual(0, scansStarted);
	}

	[TestMethod]
	public void How_the_automatic_scan_ended_is_kept()
	{
		var scheduler = Create(Automatic(downloadAfterScan: true));
		time.Advance(Grace + TimeSpan.FromMinutes(1));
		scheduler.Tick();

		time.Advance(TimeSpan.FromMinutes(1));
		ScanEnds("Audible refused the request.");
		scheduler.Tick();

		Assert.AreEqual("Audible refused the request.", scheduler.Schedule().LastAutomaticScanError);
		Assert.AreEqual(0, enqueueCalls, "a failed scan has found nothing worth queuing");
	}

	[TestMethod]
	public void New_titles_are_queued_after_a_successful_automatic_scan_when_that_is_asked_for()
	{
		var scheduler = Create(Automatic(downloadAfterScan: true));
		time.Advance(Grace + TimeSpan.FromMinutes(1));
		scheduler.Tick();

		time.Advance(TimeSpan.FromMinutes(1));
		ScanEnds();
		scheduler.Tick();

		Assert.AreEqual(1, enqueueCalls);
		Assert.IsNull(scheduler.Schedule().LastAutomaticScanError);

		// And not again on the ticks that follow.
		time.Advance(TimeSpan.FromMinutes(1));
		scheduler.Tick();
		Assert.AreEqual(1, enqueueCalls);
	}

	[TestMethod]
	public void Nothing_is_queued_when_downloading_afterwards_is_off()
	{
		var scheduler = Create(Automatic());
		time.Advance(Grace + TimeSpan.FromMinutes(1));
		scheduler.Tick();

		time.Advance(TimeSpan.FromMinutes(1));
		ScanEnds();
		scheduler.Tick();

		Assert.AreEqual(1, scansStarted);
		Assert.AreEqual(0, enqueueCalls);
	}

	[TestMethod]
	public void A_scan_the_scheduler_did_not_start_queues_nothing()
	{
		var scheduler = Create(Automatic(downloadAfterScan: true));

		// Someone pressed "Scan now" a minute after the portal came up, well inside the grace period.
		time.Advance(TimeSpan.FromMinutes(1));
		ScanEnds();
		scheduler.Tick();

		Assert.AreEqual(0, scansStarted);
		Assert.AreEqual(0, enqueueCalls);
	}

	#endregion

	#region reloading

	[TestMethod]
	public void Switching_to_automatic_starts_the_grace_period_again()
	{
		var scheduler = Create(Manual);
		time.Advance(TimeSpan.FromHours(3));
		Store().Save(Automatic(1, downloadAfterScan: true));

		var schedule = scheduler.Reload();

		Assert.AreEqual("automatic", schedule.Mode);
		Assert.AreEqual(1, schedule.EveryHours);
		Assert.IsTrue(schedule.DownloadAfterScan);
		Assert.AreEqual(time.GetUtcNow() + Grace, schedule.NextScanAt, "the grace period runs from the moment it was turned on");

		time.Advance(TimeSpan.FromMinutes(4));
		scheduler.Tick();
		Assert.AreEqual(0, scansStarted);
	}

	[TestMethod]
	public void Changing_the_interval_does_not_postpone_the_scan_that_is_already_due()
	{
		var scheduler = Create(Automatic(1));
		time.Advance(Grace);
		Store().Save(Automatic(24));

		var schedule = scheduler.Reload();

		Assert.AreEqual(24, schedule.EveryHours);
		Assert.AreEqual(time.GetUtcNow(), schedule.NextScanAt, "it was already due; only switching it on restarts the grace period");
	}

	#endregion
}

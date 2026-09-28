using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationWeb.Tests;

[TestClass]
public class DownloadQueueTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	private static QueueJob Job(string asin, QueueJobKind kind = QueueJobKind.Download, bool force = false)
		=> new() { Asin = asin, Kind = kind, Force = force, Title = "Title " + asin, Authors = "Author" };

	private static async Task<DownloadQueue> Start(ProcessJob processor, EventHub? hub = null, int workers = 1)
	{
		var queue = new DownloadQueue(processor, hub ?? new EventHub(), workers);
		await queue.StartAsync(CancellationToken.None);
		return queue;
	}

	private static async Task WaitUntil(Func<bool> condition, string what)
	{
		var deadline = DateTime.UtcNow + Timeout;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				Assert.Fail("Timed out waiting for: " + what);
			await Task.Delay(10);
		}
	}

	[TestMethod]
	public async Task Jobs_run_in_order_and_finish_completed()
	{
		var seen = new List<string>();
		await using var queue = await Start((job, _, _) => { lock (seen) seen.Add(job.Asin); return Task.CompletedTask; });

		var a = queue.Enqueue(Job("A")).Job!;
		var b = queue.Enqueue(Job("B")).Job!;

		await WaitUntil(() => b.Status == QueueJobStatus.Completed, "B to complete");

		CollectionAssert.AreEqual(new[] { "A", "B" }, seen);
		Assert.AreEqual(QueueJobStatus.Completed, a.Status);
		Assert.AreEqual(100, a.Progress);
		Assert.IsNotNull(a.StartedAt);
		Assert.IsNotNull(a.FinishedAt);
	}

	[TestMethod]
	public async Task The_same_title_is_not_queued_twice_for_the_same_kind()
	{
		var gate = new TaskCompletionSource();
		await using var queue = await Start((_, _, _) => gate.Task);

		var first = queue.Enqueue(Job("A"));
		var duplicate = queue.Enqueue(Job("A"));
		var otherKind = queue.Enqueue(Job("A", QueueJobKind.Pdf));

		Assert.IsNotNull(first.Job);
		Assert.IsNull(duplicate.Job);
		Assert.IsNotNull(duplicate.SkippedReason);
		Assert.IsNotNull(otherKind.Job, "a PDF job for the same title is different work");

		gate.SetResult();
		await WaitUntil(() => otherKind.Job!.Status == QueueJobStatus.Completed, "queue to drain");

		// Once finished, the title may be queued again.
		Assert.IsNotNull(queue.Enqueue(Job("A")).Job);
	}

	[TestMethod]
	public async Task Skip_and_fail_from_the_processor_are_recorded_with_their_reason()
	{
		await using var queue = await Start((job, reporter, _) =>
		{
			if (job.Asin == "SKIP") reporter.Skip("Already downloaded.");
			else reporter.Fail("Audible said no.");
			return Task.CompletedTask;
		});

		var skip = queue.Enqueue(Job("SKIP")).Job!;
		var fail = queue.Enqueue(Job("FAIL")).Job!;
		await WaitUntil(() => !fail.IsActive, "both to finish");

		Assert.AreEqual(QueueJobStatus.Skipped, skip.Status);
		Assert.AreEqual("Already downloaded.", skip.Message);
		Assert.AreEqual(QueueJobStatus.Failed, fail.Status);
		Assert.AreEqual("Audible said no.", fail.Message);
	}

	[TestMethod]
	public async Task An_exception_in_the_processor_fails_only_that_job()
	{
		await using var queue = await Start((job, _, _) => job.Asin == "BOOM" ? throw new InvalidOperationException("kaboom") : Task.CompletedTask);

		var boom = queue.Enqueue(Job("BOOM")).Job!;
		var next = queue.Enqueue(Job("NEXT")).Job!;
		await WaitUntil(() => next.Status == QueueJobStatus.Completed, "the queue to carry on");

		Assert.AreEqual(QueueJobStatus.Failed, boom.Status);
		StringAssert.Contains(boom.Message, "kaboom");
	}

	[TestMethod]
	public async Task Cancelling_a_waiting_job_removes_it_before_it_starts()
	{
		var gate = new TaskCompletionSource();
		var started = new List<string>();
		await using var queue = await Start((job, _, _) => { lock (started) started.Add(job.Asin); return gate.Task; });

		var first = queue.Enqueue(Job("A")).Job!;
		var second = queue.Enqueue(Job("B")).Job!;
		await WaitUntil(() => first.Status == QueueJobStatus.Working, "A to start");

		Assert.IsTrue(await queue.CancelAsync(second.Id));
		Assert.AreEqual(QueueJobStatus.Cancelled, second.Status);

		gate.SetResult();
		await WaitUntil(() => first.Status == QueueJobStatus.Completed, "A to finish");
		await Task.Delay(50);

		CollectionAssert.DoesNotContain(started, "B");
	}

	[TestMethod]
	public async Task Cancelling_a_working_job_interrupts_it_through_the_registered_hook_and_the_token()
	{
		var hookCalled = false;
		var registered = new TaskCompletionSource();
		await using var queue = await Start(async (_, reporter, ct) =>
		{
			reporter.RegisterCancel(() => { hookCalled = true; return Task.CompletedTask; });
			registered.SetResult();
			await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
		});

		var job = queue.Enqueue(Job("A")).Job!;
		await registered.Task.WaitAsync(Timeout);

		Assert.IsTrue(await queue.CancelAsync(job.Id));
		await WaitUntil(() => job.Status == QueueJobStatus.Cancelled, "A to be cancelled");

		Assert.IsTrue(hookCalled);
		Assert.IsFalse(await queue.CancelAsync(job.Id), "a finished job cannot be cancelled again");
	}

	[TestMethod]
	public async Task A_cancel_that_lands_before_the_step_registers_its_hook_still_interrupts_it()
	{
		var hookCalled = new TaskCompletionSource();
		var started = new TaskCompletionSource();
		var proceed = new TaskCompletionSource();
		await using var queue = await Start(async (_, reporter, ct) =>
		{
			started.SetResult();
			await proceed.Task;                     // the moment between "Working" and RegisterCancel
			reporter.RegisterCancel(() => { hookCalled.TrySetResult(); return Task.CompletedTask; });
			ct.ThrowIfCancellationRequested();      // what LibationProcessor does right after registering
			Assert.Fail("the step must not start after a cancel");
		});

		var job = queue.Enqueue(Job("A")).Job!;
		await started.Task.WaitAsync(Timeout);

		Assert.IsTrue(await queue.CancelAsync(job.Id));
		proceed.SetResult();

		await hookCalled.Task.WaitAsync(Timeout);
		await WaitUntil(() => job.Status == QueueJobStatus.Cancelled, "A to be cancelled");
	}

	[TestMethod]
	public async Task Clear_finished_keeps_active_jobs()
	{
		var gate = new TaskCompletionSource();
		await using var queue = await Start((job, _, _) => job.Asin == "HOLD" ? gate.Task : Task.CompletedTask);

		var done = queue.Enqueue(Job("DONE")).Job!;
		await WaitUntil(() => done.Status == QueueJobStatus.Completed, "DONE to finish");
		var hold = queue.Enqueue(Job("HOLD")).Job!;
		await WaitUntil(() => hold.Status == QueueJobStatus.Working, "HOLD to start");
		var waiting = queue.Enqueue(Job("WAIT")).Job!;

		Assert.AreEqual(1, queue.ClearFinished());
		CollectionAssert.AreEquivalent(new[] { hold.Id, waiting.Id }, queue.Items.Select(j => j.Id).ToArray());

		gate.SetResult();
	}

	[TestMethod]
	public async Task Progress_and_state_changes_reach_the_event_hub()
	{
		var hub = new EventHub();
		var (_, reader) = hub.Subscribe();
		await using var queue = await Start((_, reporter, _) =>
		{
			reporter.Step("Downloading");
			reporter.Progress(50, 500, 1000);
			reporter.Progress(100, 1000, 1000);
			return Task.CompletedTask;
		}, hub);

		var job = queue.Enqueue(Job("A")).Job!;
		await WaitUntil(() => job.Status == QueueJobStatus.Completed, "A to finish");

		var frames = new List<string>();
		while (reader.TryRead(out var frame))
			frames.Add(frame);

		Assert.IsTrue(frames.Any(f => f.StartsWith("event: queue\n")), "queue snapshots are published");
		Assert.IsTrue(frames.Any(f => f.StartsWith("event: job\n") && f.Contains("\"progress\":100")), "the final progress reaches the page");
		Assert.IsTrue(frames.Last().Contains("\"status\":\"Completed\""), "the last frame carries the final state");
	}

	[TestMethod]
	public async Task Snapshot_counts_every_state()
	{
		var gate = new TaskCompletionSource();
		await using var queue = await Start((job, reporter, _) =>
		{
			switch (job.Asin)
			{
				case "HOLD": return gate.Task;
				case "SKIP": reporter.Skip("no"); break;
				case "FAIL": reporter.Fail("no"); break;
			}
			return Task.CompletedTask;
		});

		queue.Enqueue(Job("SKIP"));
		queue.Enqueue(Job("FAIL"));
		queue.Enqueue(Job("OK"));
		var hold = queue.Enqueue(Job("HOLD")).Job!;
		await WaitUntil(() => hold.Status == QueueJobStatus.Working, "HOLD to start");
		queue.Enqueue(Job("WAIT"));

		var snapshot = queue.Snapshot();
		Assert.AreEqual(1, snapshot.Skipped);
		Assert.AreEqual(1, snapshot.Failed);
		Assert.AreEqual(1, snapshot.Completed);
		Assert.AreEqual(1, snapshot.Working);
		Assert.AreEqual(1, snapshot.Queued);
		Assert.AreEqual(5, snapshot.Items.Count);

		gate.SetResult();
	}

	[TestMethod]
	public async Task Two_workers_run_two_jobs_at_once()
	{
		var running = 0;
		var peak = 0;
		var gate = new TaskCompletionSource();
		await using var queue = await Start(async (_, _, _) =>
		{
			var now = Interlocked.Increment(ref running);
			lock (this) peak = Math.Max(peak, now);
			await gate.Task;
			Interlocked.Decrement(ref running);
		}, workers: 2);

		queue.Enqueue(Job("A"));
		queue.Enqueue(Job("B"));
		queue.Enqueue(Job("C"));
		await WaitUntil(() => peak == 2, "two jobs to run together");
		Assert.AreEqual(1, queue.Snapshot().Queued, "the third waits");

		gate.SetResult();
		await WaitUntil(() => queue.Snapshot().Completed == 3, "all three to finish");
	}
}

using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LibationWeb.Services;

public enum QueueJobKind { Download, Pdf, ConvertMp3 }

public enum QueueJobStatus { Queued, Working, Completed, Failed, Cancelled, Skipped }

/// <summary>What a processor may tell the queue while it works on one job.</summary>
public interface IJobReporter
{
	void Progress(double percent, long bytesReceived, long totalBytes);
	void Eta(TimeSpan? remaining);
	/// <summary>The phase the job is in: "Downloading", "Downloading PDF", ...</summary>
	void Step(string step);
	void Log(string line);
	/// <summary>Nothing to do for this title. The job ends as Skipped with this explanation.</summary>
	void Skip(string reason);
	/// <summary>The job ends as Failed with this explanation.</summary>
	void Fail(string reason);
	/// <summary>How to interrupt the work in flight. Called at most once per job when the user cancels.</summary>
	void RegisterCancel(Func<Task> cancel);
}

/// <summary>The unit of work: process this job, reporting through the reporter, honouring the token.</summary>
public delegate Task ProcessJob(QueueJob job, IJobReporter reporter, CancellationToken cancellationToken);

public sealed class QueueJob
{
	private const int MaxLogLines = 80;
	private readonly List<string> log = [];

	public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
	public required string Asin { get; init; }
	public required QueueJobKind Kind { get; init; }
	/// <summary>Re-download even though Libation has the audio, and ignore a recent refusal.</summary>
	public bool Force { get; init; }
	public string Title { get; init; } = "";
	public string Authors { get; init; } = "";
	public string? PictureId { get; init; }

	public QueueJobStatus Status { get; internal set; } = QueueJobStatus.Queued;
	public string? Step { get; internal set; }
	public double Progress { get; internal set; }
	public long BytesReceived { get; internal set; }
	public long TotalBytes { get; internal set; }
	public double? EtaSeconds { get; internal set; }
	public string? Message { get; internal set; }
	public DateTimeOffset QueuedAt { get; } = DateTimeOffset.Now;
	public DateTimeOffset? StartedAt { get; internal set; }
	public DateTimeOffset? FinishedAt { get; internal set; }

	internal CancellationTokenSource Cancellation { get; } = new();
	internal Func<Task>? CancelWork { get; set; }
	internal DateTimeOffset LastProgressPublish { get; set; } = DateTimeOffset.MinValue;

	public bool IsActive => Status is QueueJobStatus.Queued or QueueJobStatus.Working;

	internal void AppendLog(string line)
	{
		lock (log)
		{
			log.Add($"{DateTime.Now:HH:mm:ss} {line}");
			if (log.Count > MaxLogLines)
				log.RemoveAt(0);
		}
	}

	public QueueJobDto ToDto()
	{
		string[] lines;
		lock (log)
			lines = log.ToArray();

		return new QueueJobDto(Id, Asin, Kind, Force, Title, Authors, PictureId, Status, Step, Math.Round(Progress, 1),
			BytesReceived, TotalBytes, EtaSeconds, Message, QueuedAt, StartedAt, FinishedAt, lines);
	}
}

public sealed record QueueJobDto(
	string Id,
	string Asin,
	QueueJobKind Kind,
	bool Force,
	string Title,
	string Authors,
	string? PictureId,
	QueueJobStatus Status,
	string? Step,
	double Progress,
	long BytesReceived,
	long TotalBytes,
	double? EtaSeconds,
	string? Message,
	DateTimeOffset QueuedAt,
	DateTimeOffset? StartedAt,
	DateTimeOffset? FinishedAt,
	string[] Log);

public sealed record QueueSnapshot(IReadOnlyList<QueueJobDto> Items, int Queued, int Working, int Completed, int Failed, int Skipped, int Cancelled);

public sealed record EnqueueResult(QueueJob? Job, string? SkippedReason);

/// <summary>
/// The portal's download queue: ordered, deduplicated, worked by a fixed number of background workers,
/// every change published to the event hub. The queue knows nothing about Audible - the work is the
/// <see cref="ProcessJob"/> it is given, which is <see cref="LibationProcessor"/> in the app and a stub in tests.
/// </summary>
public sealed class DownloadQueue : IHostedService, IAsyncDisposable
{
	private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(400);

	private readonly ProcessJob processor;
	private readonly EventHub hub;
	private readonly int workerCount;
	private readonly Channel<QueueJob> channel = Channel.CreateUnbounded<QueueJob>(new UnboundedChannelOptions { SingleReader = false });
	private readonly List<QueueJob> jobs = [];
	private readonly object gate = new();
	private readonly CancellationTokenSource shutdown = new();
	private Task[] workers = [];

	public DownloadQueue(ProcessJob processor, EventHub hub, int workerCount = 1)
	{
		this.processor = processor;
		this.hub = hub;
		this.workerCount = Math.Clamp(workerCount, 1, 4);
	}

	/// <summary>Raised on a worker thread when a job leaves the Working state.</summary>
	public event Action<QueueJob>? JobFinished;

	public int WorkerCount => workerCount;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		if (workers.Length == 0)
			workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() => WorkerLoopAsync(shutdown.Token))).ToArray();
		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		shutdown.Cancel();
		channel.Writer.TryComplete();
		foreach (var job in Items.Where(j => j.Status == QueueJobStatus.Working))
		{
			try { await (job.CancelWork?.Invoke() ?? Task.CompletedTask); }
			catch { /* shutting down */ }
		}
		try { await Task.WhenAll(workers).WaitAsync(cancellationToken); }
		catch { /* shutting down */ }
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None);
		shutdown.Dispose();
	}

	public IReadOnlyList<QueueJob> Items
	{
		get { lock (gate) return jobs.ToList(); }
	}

	public QueueJob? Find(string id)
	{
		lock (gate) return jobs.FirstOrDefault(j => j.Id == id);
	}

	/// <summary>
	/// Add a job. A title already queued or working for the same kind is not queued twice; the existing
	/// job is reported as the reason.
	/// </summary>
	public EnqueueResult Enqueue(QueueJob job)
	{
		lock (gate)
		{
			var duplicate = jobs.FirstOrDefault(j => j.IsActive && j.Kind == job.Kind && j.Asin.Equals(job.Asin, StringComparison.OrdinalIgnoreCase));
			if (duplicate is not null)
				return new EnqueueResult(null, duplicate.Status == QueueJobStatus.Working ? "Already downloading." : "Already queued.");

			jobs.Add(job);
		}

		job.AppendLog("Queued");
		channel.Writer.TryWrite(job);
		PublishQueue();
		return new EnqueueResult(job, null);
	}

	/// <returns>True when the job was queued or working and is now cancelled (or being cancelled).</returns>
	public async Task<bool> CancelAsync(string id)
	{
		QueueJob? job;
		Func<Task>? cancelWork = null;
		lock (gate)
		{
			job = jobs.FirstOrDefault(j => j.Id == id);
			if (job is null || !job.IsActive)
				return false;

			if (job.Status == QueueJobStatus.Queued)
			{
				job.Status = QueueJobStatus.Cancelled;
				job.FinishedAt = DateTimeOffset.Now;
				job.Message = "Cancelled before it started.";
			}
			else
			{
				cancelWork = job.CancelWork;
			}
		}

		job.AppendLog("Cancel requested");
		job.Cancellation.Cancel();
		if (cancelWork is not null)
		{
			try { await cancelWork(); }
			catch (Exception ex) { job.AppendLog("Cancel error: " + ex.Message); }
		}

		PublishQueue();
		return true;
	}

	/// <returns>How many finished jobs were removed from the list.</returns>
	public int ClearFinished()
	{
		int removed;
		lock (gate)
			removed = jobs.RemoveAll(j => !j.IsActive);
		if (removed > 0)
			PublishQueue();
		return removed;
	}

	public QueueSnapshot Snapshot()
	{
		List<QueueJobDto> dtos;
		lock (gate)
			dtos = jobs.Select(j => j.ToDto()).ToList();

		return new QueueSnapshot(
			dtos,
			Queued: dtos.Count(d => d.Status == QueueJobStatus.Queued),
			Working: dtos.Count(d => d.Status == QueueJobStatus.Working),
			Completed: dtos.Count(d => d.Status == QueueJobStatus.Completed),
			Failed: dtos.Count(d => d.Status == QueueJobStatus.Failed),
			Skipped: dtos.Count(d => d.Status == QueueJobStatus.Skipped),
			Cancelled: dtos.Count(d => d.Status == QueueJobStatus.Cancelled));
	}

	private void PublishQueue() => hub.Publish("queue", Snapshot());

	private void PublishJob(QueueJob job) => hub.Publish("job", job.ToDto());

	private async Task WorkerLoopAsync(CancellationToken shutdownToken)
	{
		try
		{
			await foreach (var job in channel.Reader.ReadAllAsync(shutdownToken))
			{
				lock (gate)
				{
					// Cancelled while it waited its turn.
					if (job.Status != QueueJobStatus.Queued)
						continue;
					job.Status = QueueJobStatus.Working;
					job.StartedAt = DateTimeOffset.Now;
				}
				job.AppendLog("Started");
				PublishQueue();

				await RunOneAsync(job, shutdownToken);
			}
		}
		catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
		{
			// normal shutdown
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Web portal: queue worker stopped unexpectedly");
		}
	}

	private async Task RunOneAsync(QueueJob job, CancellationToken shutdownToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, job.Cancellation.Token);
		var reporter = new Reporter(this, job);
		try
		{
			await processor(job, reporter, linked.Token);

			lock (gate)
			{
				if (job.Status == QueueJobStatus.Working)
					job.Status = job.Cancellation.IsCancellationRequested ? QueueJobStatus.Cancelled : QueueJobStatus.Completed;
			}
		}
		catch (OperationCanceledException)
		{
			lock (gate)
			{
				if (job.Status == QueueJobStatus.Working)
				{
					job.Status = QueueJobStatus.Cancelled;
					job.Message ??= "Cancelled.";
				}
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Web portal: job {JobId} for {Asin} failed", job.Id, job.Asin);
			lock (gate)
			{
				if (job.Status == QueueJobStatus.Working)
				{
					job.Status = QueueJobStatus.Failed;
					job.Message = ErrorExplainer.Explain(ex);
				}
			}
		}
		finally
		{
			lock (gate)
			{
				job.FinishedAt = DateTimeOffset.Now;
				job.CancelWork = null;
				if (job.Status == QueueJobStatus.Completed)
					job.Progress = 100;
			}
			job.AppendLog(job.Status + (job.Message is null ? "" : ": " + job.Message));
			PublishQueue();
			try { JobFinished?.Invoke(job); }
			catch (Exception ex) { Serilog.Log.Logger.Warning(ex, "Web portal: JobFinished handler threw"); }
		}
	}

	private sealed class Reporter : IJobReporter
	{
		private readonly DownloadQueue queue;
		private readonly QueueJob job;

		public Reporter(DownloadQueue queue, QueueJob job)
		{
			this.queue = queue;
			this.job = job;
		}

		public void Progress(double percent, long bytesReceived, long totalBytes)
		{
			var now = DateTimeOffset.Now;
			bool publish;
			lock (queue.gate)
			{
				job.Progress = Math.Clamp(percent, 0, 100);
				if (bytesReceived > 0) job.BytesReceived = bytesReceived;
				if (totalBytes > 0) job.TotalBytes = totalBytes;
				publish = now - job.LastProgressPublish >= ProgressPublishInterval || percent >= 100;
				if (publish)
					job.LastProgressPublish = now;
			}
			if (publish)
				queue.PublishJob(job);
		}

		public void Eta(TimeSpan? remaining)
		{
			lock (queue.gate)
				job.EtaSeconds = remaining?.TotalSeconds;
		}

		public void Step(string step)
		{
			lock (queue.gate)
			{
				job.Step = step;
				job.Progress = 0;
				job.EtaSeconds = null;
			}
			job.AppendLog(step);
			queue.PublishJob(job);
		}

		public void Log(string line) => job.AppendLog(line);

		public void Skip(string reason) => End(QueueJobStatus.Skipped, reason);

		public void Fail(string reason) => End(QueueJobStatus.Failed, reason);

		private void End(QueueJobStatus status, string reason)
		{
			lock (queue.gate)
			{
				if (job.Status != QueueJobStatus.Working)
					return;
				job.Status = status;
				job.Message = reason;
			}
		}

		public void RegisterCancel(Func<Task> cancel)
		{
			bool alreadyCancelled;
			lock (queue.gate)
			{
				job.CancelWork = cancel;
				alreadyCancelled = job.Cancellation.IsCancellationRequested;
			}

			// The user may have pressed Cancel in the moment between the job starting and the step
			// being ready to be interrupted. Interrupt it now rather than letting it run to the end.
			if (alreadyCancelled)
			{
				_ = Task.Run(async () =>
				{
					try { await cancel(); }
					catch (Exception ex) { job.AppendLog("Cancel error: " + ex.Message); }
				});
			}
		}
	}
}

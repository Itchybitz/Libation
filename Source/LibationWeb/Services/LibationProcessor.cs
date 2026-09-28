using ApplicationServices;
using AudibleApi;
using DataLayer;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationWeb.Services;

/// <summary>
/// The real work behind a queue job, built from the same FileLiberator steps LibationCli chains for
/// <c>liberate</c>: download and decrypt, then the PDF from the license that download already holds, then
/// the Audiobookshelf upload when that is switched on. Nothing here decides queue state beyond Skip/Fail.
/// </summary>
public static class LibationProcessor
{
	public static async Task ProcessAsync(QueueJob job, IJobReporter reporter, CancellationToken cancellationToken)
	{
		var config = Configuration.Instance;

		// A fresh, untracked copy: the queue may hold a job for minutes and the row can change meanwhile.
		var libraryBook = DbContexts.GetLibraryBook_Flat_NoTracking(job.Asin, caseSensative: false);
		if (libraryBook is null)
		{
			reporter.Fail("This title is not in Libation's library. Scan first.");
			return;
		}

		if (AudibleFileStorage.BooksDirectory is null)
		{
			reporter.Fail("The Books folder is not set or does not exist. Check the 'Books' setting (or LIBATION_BOOKS_DIR).");
			return;
		}

		switch (job.Kind)
		{
			case QueueJobKind.Download:
				await DownloadAsync(job, libraryBook, config, reporter, cancellationToken);
				break;
			case QueueJobKind.Pdf:
				await PdfOnlyAsync(libraryBook, config, reporter, cancellationToken);
				break;
			case QueueJobKind.ConvertMp3:
				await ConvertAsync(libraryBook, config, reporter, cancellationToken);
				break;
		}
	}

	private static async Task DownloadAsync(QueueJob job, LibraryBook libraryBook, Configuration config, IJobReporter reporter, CancellationToken cancellationToken)
	{
		if (job.Force)
		{
			// Same as the CLI's targeted run: the statuses are set on the untracked copy so Validate passes,
			// and a wait Libation was observing after a refusal is the user's to override.
			libraryBook.Book.UserDefinedItem.BookStatus = LiberatedStatus.NotLiberated;
			libraryBook.Book.UserDefinedItem.SetPdfStatus(LiberatedStatus.NotLiberated);
			DownloadAttemptFailureStore.Clear(libraryBook);
		}
		else
		{
			if (libraryBook.Book.AudioExists)
			{
				if (libraryBook.NeedsPdfDownload)
				{
					await PdfOnlyAsync(libraryBook, config, reporter, cancellationToken);
					return;
				}
				reporter.Skip("Already downloaded. Queue it again with 'Re-download' to fetch it afresh.");
				return;
			}

			if (!libraryBook.Downloadable)
			{
				reporter.Skip("Audible did not list this title in the last scan, so it cannot be licensed. Scan again, or force it.");
				return;
			}

			if (DownloadAttemptFailureStore.Find(libraryBook, DateTimeOffset.Now) is { } deferred)
			{
				reporter.Skip($"Audible refused this title recently ({deferred.Reason ?? deferred.KindLabel}). "
					+ $"Libation will try again {DeferredDownloadUserMessage.DescribeWhen(deferred.RetryAfter, DateTimeOffset.Now)}; open the title and choose 'Try now' to ignore the wait.");
				return;
			}
		}

		if (DailyDownloadLimit.AppliesTo(libraryBook.IsAudiblePlus, config))
		{
			var now = DateTimeOffset.Now;
			var allowance = DailyDownloadLimit.Evaluate(config, DownloadHistoryStore.GetCurrentWindow(now), now);
			if (allowance.Blocks(libraryBook.IsAudiblePlus))
			{
				reporter.Skip(DailyDownloadLimitUserMessage.BuildWaitingStatus(allowance));
				return;
			}
		}

		var download = DownloadDecryptBook.Create(config);
		Wire(download, libraryBook, config, reporter);
		reporter.RegisterCancel(download.CancelAsync);
		// A cancel that arrived before the step existed has nothing to interrupt yet; stop here instead.
		cancellationToken.ThrowIfCancellationRequested();
		reporter.Step("Downloading");

		StatusHandler status;
		try
		{
			status = await download.ProcessSingleAsync(libraryBook, validate: true);
		}
		catch (ContentLicenseDeniedException ex)
		{
			reporter.Fail("Audible refused to license this title: " + ex.Message + NextAttempt(libraryBook));
			return;
		}
		catch (ApiErrorException ex) when (WidevineRecommendation.ShouldRecommendWidevine(ex, config))
		{
			reporter.Fail(WidevineRecommendation.BuildLogSummary(libraryBook.Book.TitleWithSubtitle));
			return;
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			reporter.Fail(ErrorExplainer.Explain(ex) + NextAttempt(libraryBook));
			return;
		}

		// The step reports a cancellation as a status rather than throwing; the queue wants the throw.
		cancellationToken.ThrowIfCancellationRequested();

		if (!status.IsSuccess)
		{
			reporter.Fail(string.Join(" ", status.Errors) + NextAttempt(libraryBook));
			return;
		}

		// The PDF comes from the license the download used - one request to Audible, not two.
		if (download.ObtainedLicense is { } license)
		{
			var pdf = DownloadPdf.Create(config);
			if (pdf.Validate(libraryBook))
			{
				Wire(pdf, libraryBook, config, reporter);
				pdf.LicenseInfo = license;
				reporter.Step("Downloading PDF");
				try
				{
					var pdfStatus = await pdf.ProcessSingleAsync(libraryBook, validate: false);
					if (!pdfStatus.IsSuccess)
						reporter.Log("PDF: " + string.Join(" ", pdfStatus.Errors));
				}
				catch (Exception ex)
				{
					reporter.Log("PDF failed: " + ErrorExplainer.Explain(ex));
				}
				finally
				{
					pdf.LicenseInfo = null;
				}
			}
		}

		if (config.AudiobookshelfEnabled)
			await UploadAsync(libraryBook, config, reporter);
	}

	private static async Task PdfOnlyAsync(LibraryBook libraryBook, Configuration config, IJobReporter reporter, CancellationToken cancellationToken)
	{
		var pdf = DownloadPdf.Create(config);
		if (!pdf.Validate(libraryBook))
		{
			reporter.Skip(libraryBook.Book.HasPdf ? "The PDF is already downloaded." : "This title has no PDF.");
			return;
		}

		Wire(pdf, libraryBook, config, reporter);
		reporter.Step("Downloading PDF");
		try
		{
			var status = await pdf.ProcessSingleAsync(libraryBook, validate: true);
			cancellationToken.ThrowIfCancellationRequested();
			if (!status.IsSuccess)
				reporter.Fail(string.Join(" ", status.Errors) + NextAttempt(libraryBook));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			reporter.Fail(ErrorExplainer.Explain(ex) + NextAttempt(libraryBook));
		}
	}

	private static async Task ConvertAsync(LibraryBook libraryBook, Configuration config, IJobReporter reporter, CancellationToken cancellationToken)
	{
		var convert = ConvertToMp3.Create(config);
		if (!convert.Validate(libraryBook))
		{
			reporter.Skip("Nothing to convert: no M4B was found, or the MP3 already exists.");
			return;
		}

		Wire(convert, libraryBook, config, reporter);
		reporter.RegisterCancel(convert.CancelAsync);
		cancellationToken.ThrowIfCancellationRequested();
		reporter.Step("Converting to MP3");
		try
		{
			var status = await convert.ProcessSingleAsync(libraryBook, validate: true);
			cancellationToken.ThrowIfCancellationRequested();
			if (!status.IsSuccess)
				reporter.Fail(string.Join(" ", status.Errors));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			reporter.Fail(ErrorExplainer.Explain(ex));
		}
	}

	private static async Task UploadAsync(LibraryBook libraryBook, Configuration config, IJobReporter reporter)
	{
		reporter.Step("Uploading to Audiobookshelf");
		var upload = UploadToAudiobookshelf.Create(config);
		upload.OutcomeDetermined += (_, e) => reporter.Log("Audiobookshelf: " + e.Message);
		try
		{
			// An upload problem must never mark the book as a bad download; the step itself reports through OutcomeDetermined.
			await upload.TryProcessAsync(libraryBook);
		}
		catch (Exception ex)
		{
			reporter.Log("Audiobookshelf upload failed: " + ErrorExplainer.Explain(ex));
		}
	}

	private static void Wire(Processable processable, LibraryBook libraryBook, Configuration config, IJobReporter reporter)
	{
		processable.StatusUpdate += (_, message) => reporter.Log(message);
		processable.FileCreated += (_, e) => reporter.Log("File: " + Path.GetFileName(e.path));
		processable.StreamingTimeRemaining += (_, remaining) => reporter.Eta(remaining);
		processable.StreamingProgressChanged += (_, progress) =>
		{
			if (progress.ProgressPercentage is double percent)
				reporter.Progress(percent, Bytes(progress.BytesReceived), Bytes(progress.TotalBytesToReceive));
		};

		if (processable is AudioDecodable decodable)
		{
			// Same source as the CLI: the cover Libation already has, at native size, embedded in the file.
			decodable.RequestCoverArt += (_, _) =>
			{
				var pictureId = config.FileDownloadQuality == Configuration.DownloadQuality.High
					? libraryBook.Book.PictureLarge ?? libraryBook.Book.PictureId
					: libraryBook.Book.PictureId;

				return pictureId is null ? null : PictureStorage.GetPictureSynchronously(new PictureDefinition(pictureId, PictureSize.Native));
			};
		}
	}

	// DownloadProgress's byte counters have been nullable in some Dinah.Core versions and not in others;
	// overloading on both keeps this compiling either way.
	private static long Bytes(long value) => value;
	private static long Bytes(long? value) => value ?? 0;

	/// <summary>Says when Libation will try a refused title again, so the queue explains its own silence later.</summary>
	private static string NextAttempt(LibraryBook libraryBook)
	{
		var now = DateTimeOffset.Now;
		return DownloadAttemptFailureStore.Find(libraryBook, now) is { } deferred
			? $" Libation will not try this title again {DeferredDownloadUserMessage.DescribeWhen(deferred.RetryAfter, now)}; open the title and choose 'Try now' to ignore the wait."
			: "";
	}

	/// <summary>
	/// Every title a bulk "download everything new" should attempt: the same selection the CLI makes for
	/// a bare <c>liberate</c>, including the wait on titles Audible refused recently.
	/// </summary>
	public static IEnumerable<LibraryBook> SelectNewDownloads(IReadOnlyList<LibraryBook> library, Configuration config, bool includeDeferred)
	{
		var template = DownloadDecryptBook.Create(config);
		var deferrals = includeDeferred ? DownloadDeferrals.None : DownloadDeferrals.Load(DateTimeOffset.Now);

		return template
			.GetValidLibraryBooks(library.Where(lb => lb.Downloadable))
			.Where(lb => !deferrals.IsDeferred(lb))
			.ToList();
	}
}

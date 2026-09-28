using ApplicationServices;
using DataLayer;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationWeb.Services;

/// <summary>One row of the portal's library list. Entity data only, so it can be built without a database.</summary>
public sealed record BookDto(
	string Asin,
	string Title,
	string? Subtitle,
	string TitleWithSubtitle,
	string Authors,
	string Narrators,
	string Series,
	int LengthMinutes,
	DateTime? DatePublished,
	DateTime DateAdded,
	/// <summary>Libation's own status name: NotLiberated, Liberated, Error or PartialDownload.</summary>
	string Status,
	string? PdfStatus,
	bool HasPdf,
	bool NeedsDownload,
	bool NeedsPdf,
	bool Absent,
	bool IsPlus,
	bool IsEpisode,
	string? PictureId,
	string Tags,
	float Rating,
	string Locale,
	string Account,
	DateTime? LastDownloaded);

public sealed record BookDetailDto(BookDto Book, string? Description, string? AudioPath, string[] Categories, string? Publisher, string? Language, bool IsAbridged);

public sealed record LibraryCounts(int Total, int NotDownloaded, int Downloaded, int Errors, int Absent, int PdfMissing);

public sealed record LibraryPage(int Total, int Page, int PageSize, IReadOnlyList<BookDto> Items);

/// <summary>
/// The library as the portal sees it: a short-lived cached read of Libation's database, queried and
/// paged in memory (a personal library is a few thousand rows at most), invalidated whenever Libation
/// reports that the library or a book's status changed.
/// </summary>
public sealed class LibraryService
{
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
	private readonly object gate = new();
	private List<LibraryBook>? cache;
	private DateTimeOffset cachedAt;

	public LibraryService()
	{
		LibraryCommands.LibrarySizeChanged += (_, _) => Invalidate();
		LibraryCommands.BookUserDefinedItemCommitted += (_, _) => Invalidate();
	}

	public void Invalidate()
	{
		lock (gate)
			cache = null;
	}

	public IReadOnlyList<LibraryBook> GetLibrary()
	{
		lock (gate)
		{
			if (cache is null || DateTimeOffset.UtcNow - cachedAt > CacheLifetime)
			{
				cache = DbContexts.GetLibrary_Flat_NoTracking();
				cachedAt = DateTimeOffset.UtcNow;
			}
			return cache;
		}
	}

	public LibraryBook? Find(string asin)
		=> GetLibrary().FirstOrDefault(lb => lb.Book.AudibleProductId.Equals(asin, StringComparison.OrdinalIgnoreCase));

	public LibraryCounts Counts()
	{
		var library = GetLibrary();
		return new LibraryCounts(
			Total: library.Count,
			NotDownloaded: library.Count(lb => lb.NeedsBookDownload),
			Downloaded: library.Count(lb => lb.Book.UserDefinedItem.BookStatus == LiberatedStatus.Liberated),
			Errors: library.Count(lb => lb.Book.UserDefinedItem.BookStatus == LiberatedStatus.Error),
			Absent: library.Count(lb => lb.AbsentFromLastScan),
			PdfMissing: library.Count(lb => lb.NeedsPdfDownload));
	}

	/// <param name="query">A Libation (Lucene) search string, or plain words. A query Lucene rejects falls back to a substring match.</param>
	/// <param name="status">all, new, downloaded, error, partial, absent or pdf.</param>
	/// <param name="sort">added (default), title, author, published, length or relevance.</param>
	public LibraryPage Query(string? query, string? status, string? sort, int page, int pageSize)
	{
		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, 500);

		IEnumerable<LibraryBook> items = GetLibrary();
		items = ApplyStatusFilter(items, status);

		Dictionary<string, int>? rank = null;
		if (!string.IsNullOrWhiteSpace(query))
		{
			var text = query.Trim();
			rank = TrySearch(text);

			// Plain words that the index cannot answer - a stop word like "the", or the first letters of
			// a title typed on a phone - fall back to a substring match. A query written in Lucene's own
			// syntax is taken at its word, zero hits included.
			if (rank is { Count: 0 } && !LooksLikeLucene(text))
				rank = null;

			items = rank is not null
				? items.Where(lb => rank.ContainsKey(lb.Book.AudibleProductId))
				: items.Where(lb => MatchesText(lb, text));
		}

		items = ApplySort(items, sort, rank);

		var list = items.ToList();
		var pageItems = list.Skip((page - 1) * pageSize).Take(pageSize).Select(ToDto).ToList();
		return new LibraryPage(list.Count, page, pageSize, pageItems);
	}

	internal static IEnumerable<LibraryBook> ApplyStatusFilter(IEnumerable<LibraryBook> items, string? status)
		=> (status ?? "all").ToLowerInvariant() switch
		{
			"new" => items.Where(lb => lb.NeedsBookDownload),
			"downloaded" => items.Where(lb => lb.Book.UserDefinedItem.BookStatus == LiberatedStatus.Liberated),
			"error" => items.Where(lb => lb.Book.UserDefinedItem.BookStatus == LiberatedStatus.Error),
			"partial" => items.Where(lb => lb.Book.UserDefinedItem.BookStatus == LiberatedStatus.PartialDownload),
			"absent" => items.Where(lb => lb.AbsentFromLastScan),
			"pdf" => items.Where(lb => lb.NeedsPdfDownload),
			_ => items,
		};

	internal static IEnumerable<LibraryBook> ApplySort(IEnumerable<LibraryBook> items, string? sort, Dictionary<string, int>? rank)
		=> (sort ?? "added").ToLowerInvariant() switch
		{
			"title" => items.OrderBy(lb => lb.Book.TitleSortable(), StringComparer.OrdinalIgnoreCase),
			"author" => items.OrderBy(lb => lb.Book.AuthorNames, StringComparer.OrdinalIgnoreCase).ThenBy(lb => lb.Book.TitleSortable(), StringComparer.OrdinalIgnoreCase),
			"published" => items.OrderByDescending(lb => lb.Book.DatePublished ?? DateTime.MinValue),
			"length" => items.OrderByDescending(lb => lb.Book.LengthInMinutes),
			"relevance" when rank is not null => items.OrderBy(lb => rank.GetValueOrDefault(lb.Book.AudibleProductId, int.MaxValue)),
			_ => items.OrderByDescending(lb => lb.DateAdded),
		};

	/// <summary>Field names, wildcards, quotes, ranges, negation or boolean operators: the user meant Lucene.</summary>
	internal static bool LooksLikeLucene(string query)
		=> query.IndexOfAny([':', '*', '?', '"', '(', ')', '[', ']', '{', '}', '~', '^']) >= 0
		|| query.StartsWith('-')
		|| query.Contains(" -", StringComparison.Ordinal)
		|| query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w is "AND" or "OR" or "NOT");

	private static bool MatchesText(LibraryBook lb, string text)
		=> lb.Book.TitleWithSubtitle.Contains(text, StringComparison.OrdinalIgnoreCase)
		|| lb.Book.AuthorNames.Contains(text, StringComparison.OrdinalIgnoreCase)
		|| lb.Book.NarratorNames.Contains(text, StringComparison.OrdinalIgnoreCase)
		|| lb.Book.SeriesNames().Contains(text, StringComparison.OrdinalIgnoreCase)
		|| lb.Book.AudibleProductId.Equals(text, StringComparison.OrdinalIgnoreCase);

	/// <returns>Product ids in relevance order, or null when the index could not answer.</returns>
	private static Dictionary<string, int>? TrySearch(string query)
	{
		try
		{
			var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var i = 0;
			foreach (var doc in SearchEngineCommands.Search(query).Docs)
			{
				var id = doc.Doc.GetField("_ID_")?.StringValue;
				if (id is not null && !rank.ContainsKey(id))
					rank[id] = i++;
			}
			return rank;
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Debug(ex, "Search index could not answer {Query}; falling back to a text match", query);
			return null;
		}
	}

	public static BookDto ToDto(LibraryBook lb)
	{
		var book = lb.Book;
		var udi = book.UserDefinedItem;
		return new BookDto(
			Asin: book.AudibleProductId,
			Title: book.Title,
			Subtitle: string.IsNullOrEmpty(book.Subtitle) ? null : book.Subtitle,
			TitleWithSubtitle: book.TitleWithSubtitle,
			Authors: book.AuthorNames,
			Narrators: book.NarratorNames,
			Series: book.SeriesNames(includeIndex: true),
			LengthMinutes: book.LengthInMinutes,
			DatePublished: book.DatePublished,
			DateAdded: lb.DateAdded,
			Status: udi.BookStatus.ToString(),
			PdfStatus: udi.PdfStatus?.ToString(),
			HasPdf: book.HasPdf,
			NeedsDownload: lb.NeedsBookDownload,
			NeedsPdf: lb.NeedsPdfDownload,
			Absent: lb.AbsentFromLastScan,
			IsPlus: lb.IsAudiblePlus,
			IsEpisode: book.IsEpisodeChild(),
			PictureId: book.PictureId ?? book.PictureLarge,
			Tags: udi.Tags,
			Rating: udi.Rating.FirstScore(),
			Locale: book.Locale,
			Account: lb.Account,
			LastDownloaded: udi.LastDownloaded);
	}

	public BookDetailDto ToDetail(LibraryBook lb)
	{
		string? audioPath = null;
		try
		{
			audioPath = AudibleFileStorage.Audio.GetPath(lb.Book.AudibleProductId)?.Path;
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Debug(ex, "Could not resolve the audio path for {Asin}", lb.Book.AudibleProductId);
		}

		return new BookDetailDto(
			ToDto(lb),
			string.IsNullOrWhiteSpace(lb.Book.Description) ? null : lb.Book.Description,
			PathDisplay.Pretty(audioPath),
			lb.Book.LowestCategoryNames(),
			lb.Book.Publisher,
			lb.Book.Language,
			lb.Book.IsAbridged);
	}

	/// <summary>
	/// Cover bytes from Libation's image cache, downloading on a miss. Returns null when the picture cannot
	/// be had in time; the page draws a placeholder instead.
	/// </summary>
	public static async Task<byte[]?> GetCoverAsync(string pictureId, PictureSize size, CancellationToken cancellationToken)
	{
		var definition = new PictureDefinition(pictureId, size);

		var (isDefault, bytes) = PictureStorage.GetPicture(definition);
		if (!isDefault && bytes.Length > 0)
			return bytes;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		try
		{
			var downloaded = await Task.Run(() => PictureStorage.GetPictureSynchronously(definition, timeout.Token), timeout.Token);
			return downloaded.Length > 0 ? downloaded : null;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Debug(ex, "Cover download failed for {PictureId}", pictureId);
			return null;
		}
	}
}

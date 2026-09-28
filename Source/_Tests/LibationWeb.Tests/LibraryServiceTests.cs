using DataLayer;
using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Text.Json;

namespace LibationWeb.Tests;

[TestClass]
public class LibraryServiceTests
{
	[TestMethod]
	public void A_new_title_maps_to_not_downloaded_and_needs_download()
	{
		var lb = MockLibraryBook.CreateBook(title: "New Book", bookStatus: LiberatedStatus.NotLiberated, dateAdded: new DateTime(2026, 9, 1));

		var dto = LibraryService.ToDto(lb);

		Assert.AreEqual("NotLiberated", dto.Status);
		Assert.IsTrue(dto.NeedsDownload);
		Assert.IsFalse(dto.Absent);
		Assert.AreEqual("Author One", dto.Authors);
		Assert.AreEqual("Narrator One", dto.Narrators);
		Assert.AreEqual(lb.Book.AudibleProductId, dto.Asin);
		Assert.AreEqual(new DateTime(2026, 9, 1), dto.DateAdded);
	}

	[TestMethod]
	public void A_downloaded_title_does_not_need_download()
	{
		var dto = LibraryService.ToDto(MockLibraryBook.CreateBook(bookStatus: LiberatedStatus.Liberated));

		Assert.AreEqual("Liberated", dto.Status);
		Assert.IsFalse(dto.NeedsDownload);
		Assert.IsNotNull(dto.LastDownloaded);
	}

	[TestMethod]
	public void A_title_absent_from_the_last_scan_is_flagged_and_never_needs_download()
	{
		var dto = LibraryService.ToDto(MockLibraryBook.CreateBook(absentFromLastScan: true, bookStatus: LiberatedStatus.NotLiberated));

		Assert.IsTrue(dto.Absent);
		Assert.IsFalse(dto.NeedsDownload, "Audible will not license a title it no longer lists");
	}

	[TestMethod]
	public void Series_includes_the_order_number()
	{
		var lb = MockLibraryBook.CreateBook().AddSeries("Dungeon Crawler Carl", 2);

		Assert.AreEqual("Dungeon Crawler Carl (#2)", LibraryService.ToDto(lb).Series);
	}

	[TestMethod]
	public void Status_filters_select_by_libation_state()
	{
		var books = new LibraryBook[]
		{
			MockLibraryBook.CreateBook(title: "A", bookStatus: LiberatedStatus.NotLiberated),
			MockLibraryBook.CreateBook(title: "B", bookStatus: LiberatedStatus.Liberated),
			MockLibraryBook.CreateBook(title: "C", bookStatus: LiberatedStatus.Error),
			MockLibraryBook.CreateBook(title: "D", bookStatus: LiberatedStatus.Liberated, pdfStatus: LiberatedStatus.NotLiberated),
			MockLibraryBook.CreateBook(title: "E", bookStatus: LiberatedStatus.NotLiberated, absentFromLastScan: true),
		};

		string[] Titles(string status) => LibraryService.ApplyStatusFilter(books, status).Select(b => b.Book.Title).ToArray();

		CollectionAssert.AreEquivalent(new[] { "A" }, Titles("new"));
		CollectionAssert.AreEquivalent(new[] { "B", "D" }, Titles("downloaded"));
		CollectionAssert.AreEquivalent(new[] { "C" }, Titles("error"));
		CollectionAssert.AreEquivalent(new[] { "D" }, Titles("pdf"));
		CollectionAssert.AreEquivalent(new[] { "E" }, Titles("absent"));
		Assert.AreEqual(5, Titles("all").Length);
		Assert.AreEqual(5, Titles(null!).Length);
	}

	[TestMethod]
	public void Sorting_by_title_ignores_a_leading_article_and_default_is_newest_first()
	{
		var older = MockLibraryBook.CreateBook(title: "Zebra", dateAdded: new DateTime(2026, 1, 1));
		var newer = MockLibraryBook.CreateBook(title: "The Apple", dateAdded: new DateTime(2026, 6, 1));
		var books = new LibraryBook[] { older, newer };

		var byTitle = LibraryService.ApplySort(books, "title", null).Select(b => b.Book.Title).ToArray();
		var byAdded = LibraryService.ApplySort(books, "added", null).Select(b => b.Book.Title).ToArray();

		CollectionAssert.AreEqual(new[] { "The Apple", "Zebra" }, byTitle);
		CollectionAssert.AreEqual(new[] { "The Apple", "Zebra" }, byAdded, "newest first");
	}

	[TestMethod]
	public void Relevance_sort_follows_the_search_rank()
	{
		var a = MockLibraryBook.CreateBook(title: "A");
		var b = MockLibraryBook.CreateBook(title: "B");
		var rank = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			[b.Book.AudibleProductId] = 0,
			[a.Book.AudibleProductId] = 1,
		};

		var sorted = LibraryService.ApplySort(new LibraryBook[] { a, b }, "relevance", rank).Select(x => x.Book.Title).ToArray();

		CollectionAssert.AreEqual(new[] { "B", "A" }, sorted);
	}

	[TestMethod]
	public void Dto_serializes_with_camel_case_and_string_enums()
	{
		var dto = LibraryService.ToDto(MockLibraryBook.CreateBook(bookStatus: LiberatedStatus.NotLiberated));
		var json = JsonSerializer.Serialize(dto, Json.Options);

		StringAssert.Contains(json, "\"asin\":");
		StringAssert.Contains(json, "\"status\":\"NotLiberated\"");
		StringAssert.Contains(json, "\"needsDownload\":true");
	}

	[TestMethod]
	public void Event_frames_are_valid_server_sent_events()
	{
		var frame = EventHub.Frame("queue", new { queued = 1, status = QueueJobStatus.Working });

		Assert.AreEqual("event: queue\ndata: {\"queued\":1,\"status\":\"Working\"}\n\n", frame);
	}

	[TestMethod]
	public void Plain_words_are_not_lucene_but_operators_are()
	{
		Assert.IsFalse(LibraryService.LooksLikeLucene("the"));
		Assert.IsFalse(LibraryService.LooksLikeLucene("dungeon crawler"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("author:sanderson"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("-liberated"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("carl -dungeon"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("harry AND potter"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("pott*"));
		Assert.IsTrue(LibraryService.LooksLikeLucene("\"exact phrase\""));
	}

	[TestMethod]
	public void Extended_windows_paths_are_shown_the_way_explorer_shows_them()
	{
		Assert.AreEqual(@"\\192.168.1.50\media\AudioBooks", PathDisplay.Pretty(@"\\?\UNC\192.168.1.50\media\AudioBooks"));
		Assert.AreEqual(@"C:\Users\me\Libation", PathDisplay.Pretty(@"\\?\C:\Users\me\Libation"));
		Assert.AreEqual("/config-internal", PathDisplay.Pretty("/config-internal"));
		Assert.IsNull(PathDisplay.Pretty(null));
	}

	[TestMethod]
	public void Error_explainer_names_the_innermost_cause()
	{
		var ex = new InvalidOperationException("outer", new System.IO.IOException("disk full"));

		Assert.AreEqual("outer (disk full)", ErrorExplainer.Explain(ex));
		Assert.AreEqual("plain", ErrorExplainer.Explain(new Exception("plain")));
	}
}

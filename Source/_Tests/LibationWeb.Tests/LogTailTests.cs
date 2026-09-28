using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace LibationWeb.Tests;

[TestClass]
public class LogTailTests
{
	private string dir = "";

	[TestInitialize]
	public void Setup()
	{
		dir = Path.Combine(Path.GetTempPath(), "LibationWeb.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
	}

	[TestMethod]
	public void Newest_log_file_wins_and_other_files_are_ignored()
	{
		var old = Path.Combine(dir, "Log202607.log");
		var newer = Path.Combine(dir, "Log202609.log");
		File.WriteAllText(old, "old\n");
		File.WriteAllText(newer, "new\n");
		File.WriteAllText(Path.Combine(dir, "Settings.json"), "{}");
		File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
		File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

		Assert.AreEqual(newer, LogTail.NewestLogFile(dir));
	}

	[TestMethod]
	public void Log_directory_comes_from_the_serilog_file_sink_when_settings_name_one()
	{
		var logDir = Path.Combine(dir, "elsewhere");
		Directory.CreateDirectory(logDir);
		var settings = Path.Combine(dir, "Settings.json");
		var configured = Path.Combine(logDir, "Log.log").Replace("\\", "\\\\");
		File.WriteAllText(settings, $$$"""
			{ "Books": "x", "Serilog": { "MinimumLevel": "Information", "WriteTo": [ { "Name": "File", "Args": { "path": "{{{configured}}}", "rollingInterval": "Month" } } ] } }
			""");

		Assert.AreEqual(logDir, LogTail.ResolveLogDirectory(settings, dir));
	}

	[TestMethod]
	public void Log_directory_falls_back_when_settings_are_missing_or_name_no_file_sink()
	{
		Assert.AreEqual(dir, LogTail.ResolveLogDirectory(Path.Combine(dir, "missing.json"), dir));

		var settings = Path.Combine(dir, "Settings.json");
		File.WriteAllText(settings, """{ "Books": "x", "Serilog": { "WriteTo": [ { "Name": "Console" } ] } }""");
		Assert.AreEqual(dir, LogTail.ResolveLogDirectory(settings, dir));

		File.WriteAllText(settings, "not json at all");
		Assert.AreEqual(dir, LogTail.ResolveLogDirectory(settings, dir));
	}

	[TestMethod]
	public void No_log_files_means_null()
	{
		Assert.IsNull(LogTail.NewestLogFile(dir));
		Assert.IsNull(LogTail.NewestLogFile(Path.Combine(dir, "missing")));
	}

	[TestMethod]
	public void Tail_returns_the_last_lines_in_order()
	{
		var path = Path.Combine(dir, "Log202609.log");
		File.WriteAllLines(path, Enumerable.Range(1, 50).Select(i => "line " + i));

		var tail = LogTail.Tail(path, 3);

		CollectionAssert.AreEqual(new[] { "line 48", "line 49", "line 50" }, tail.ToArray());
	}

	[TestMethod]
	public void Tail_of_a_short_file_is_the_whole_file()
	{
		var path = Path.Combine(dir, "Log202609.log");
		File.WriteAllText(path, "a\r\nb\r\n");

		CollectionAssert.AreEqual(new[] { "a", "b" }, LogTail.Tail(path, 10).ToArray());
	}

	[TestMethod]
	public void A_line_cut_by_the_byte_window_is_dropped_not_shown_as_a_fragment()
	{
		var path = Path.Combine(dir, "Log202609.log");
		File.WriteAllLines(path, Enumerable.Range(1, 200).Select(i => $"entry number {i:000} with some padding text"));

		var tail = LogTail.Tail(path, 1000, maxBytes: 200);

		Assert.IsTrue(tail.Count > 0);
		Assert.IsTrue(tail.All(l => l.StartsWith("entry number ")), "no partial first line: " + tail[0]);
		Assert.AreEqual("entry number 200 with some padding text", tail[^1]);
	}

	[TestMethod]
	public void Tail_reads_a_file_another_process_holds_open_for_writing()
	{
		var path = Path.Combine(dir, "Log202609.log");
		using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
		writer.Write("held open\n"u8);
		writer.Flush();

		CollectionAssert.AreEqual(new[] { "held open" }, LogTail.Tail(path, 5).ToArray());
	}
}

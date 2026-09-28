using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LibationWeb.Services;

/// <summary>Reads the end of Libation's rolling log without loading the whole file.</summary>
public static class LogTail
{
	public const int DefaultMaxBytes = 512 * 1024;

	/// <summary>
	/// Where Libation writes its log: the folder of the <c>File</c> sink's <c>path</c> in Settings.json's
	/// Serilog section when there is one (the desktop app stores an absolute path there), otherwise the
	/// Libation files folder itself.
	/// </summary>
	public static string ResolveLogDirectory(string settingsFilePath, string fallbackDirectory)
	{
		try
		{
			if (!File.Exists(settingsFilePath))
				return fallbackDirectory;

			var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsFilePath));
			if (settings.RootElement.TryGetProperty("Serilog", out var serilog)
				&& serilog.TryGetProperty("WriteTo", out var writeTo)
				&& writeTo.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				foreach (var sink in writeTo.EnumerateArray())
				{
					if (sink.TryGetProperty("Name", out var name) && name.GetString() == "File"
						&& sink.TryGetProperty("Args", out var args)
						&& args.TryGetProperty("path", out var pathElement)
						&& pathElement.GetString() is { Length: > 0 } configured
						&& Path.GetDirectoryName(configured) is { Length: > 0 } dir
						&& Directory.Exists(dir))
						return dir;
				}
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Debug(ex, "Could not read the Serilog path from {Settings}", settingsFilePath);
		}

		return fallbackDirectory;
	}

	/// <summary>The log Libation is writing now: the most recently modified <c>Log*.log</c> in the folder.</summary>
	public static string? NewestLogFile(string directory)
	{
		if (!Directory.Exists(directory))
			return null;

		return Directory.EnumerateFiles(directory, "Log*.log")
			.Select(f => new FileInfo(f))
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.ThenByDescending(f => f.Name, StringComparer.Ordinal)
			.FirstOrDefault()?.FullName;
	}

	/// <summary>
	/// The last <paramref name="lineCount"/> lines of the file. Only the final <paramref name="maxBytes"/> are
	/// read; when that cuts a line in half the fragment is dropped rather than shown as a line.
	/// </summary>
	public static IReadOnlyList<string> Tail(string path, int lineCount, int maxBytes = DefaultMaxBytes)
	{
		if (lineCount <= 0)
			return [];

		// Serilog holds the file open for writing; share everything so the tail never blocks the logger.
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

		var start = Math.Max(0, stream.Length - maxBytes);
		stream.Seek(start, SeekOrigin.Begin);

		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		var text = reader.ReadToEnd();

		var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
		if (start > 0 && lines.Count > 0)
			lines.RemoveAt(0);
		if (lines.Count > 0 && lines[^1].Length == 0)
			lines.RemoveAt(lines.Count - 1);

		return lines.Count <= lineCount ? lines : lines.GetRange(lines.Count - lineCount, lineCount);
	}
}

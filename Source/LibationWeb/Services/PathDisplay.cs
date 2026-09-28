using System;

namespace LibationWeb.Services;

/// <summary>
/// Libation keeps Windows paths in their extended form (<c>\\?\C:\...</c>, <c>\\?\UNC\server\share</c>).
/// That is right for the file system and wrong for a person reading a status card.
/// </summary>
public static class PathDisplay
{
	public static string? Pretty(string? path)
	{
		if (string.IsNullOrEmpty(path))
			return path;

		const string uncPrefix = @"\\?\UNC\";
		const string extendedPrefix = @"\\?\";

		if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
			return @"\\" + path[uncPrefix.Length..];
		if (path.StartsWith(extendedPrefix, StringComparison.Ordinal))
			return path[extendedPrefix.Length..];
		return path;
	}
}

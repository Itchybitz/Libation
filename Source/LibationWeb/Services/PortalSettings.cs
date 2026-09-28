using LibationFileManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LibationWeb.Services;

/// <summary>
/// The portal's own settings - the handful of things the desktop app has no notion of. Libation's
/// <c>Settings.json</c> is never written by the portal, so these live beside it in their own file.
/// </summary>
public sealed record PortalSettingsDto(string ScanMode, int ScanEveryHours, bool DownloadAfterScan);

/// <summary>
/// Loads and saves <c>LibationWeb.json</c> in the Libation files folder. A missing or unreadable file is
/// not an error: the portal has always worked without one, so the defaults stand in (manual scans, the
/// setting a person who never opens this page keeps).
/// </summary>
public sealed class PortalSettingsStore
{
	public const string FileName = "LibationWeb.json";
	public const string Manual = "manual";
	public const string Automatic = "automatic";

	/// <summary>The intervals the page offers. Anything else is refused rather than rounded.</summary>
	public static readonly IReadOnlyList<int> AllowedHours = [1, 2, 3, 6, 12, 24];

	public static PortalSettingsDto Defaults { get; } = new(Manual, 6, false);

	private static readonly JsonSerializerOptions WriteOptions = new(Json.Options) { WriteIndented = true };

	private readonly Func<string> filePath;

	/// <param name="filePath">
	/// Where the file lives. Resolved lazily on purpose: the default needs Configuration, which a unit
	/// test has no business loading, and the tests hand in a temp file instead.
	/// </param>
	public PortalSettingsStore(Func<string>? filePath = null)
		=> this.filePath = filePath ?? (() => Path.Combine(Configuration.Instance.LibationFiles.Location, FileName));

	public string FilePath => filePath();

	/// <summary>The saved settings, or the defaults when there is no usable file.</summary>
	public PortalSettingsDto Load()
	{
		var path = filePath();
		try
		{
			if (!File.Exists(path))
				return Defaults;

			var saved = JsonSerializer.Deserialize<PortalSettingsDto>(File.ReadAllText(path), Json.Options);
			return saved is null ? Defaults : Validate(saved);
		}
		catch (Exception ex)
		{
			// Hand-edited to nonsense, half-written, unreadable: none of that is worth refusing to start over.
			Serilog.Log.Logger.Warning(ex, "Web portal: {File} could not be read; using the default portal settings", path);
			return Defaults;
		}
	}

	/// <summary>Validate, then write the file whole.</summary>
	/// <exception cref="ArgumentException">A field the page would never send.</exception>
	public PortalSettingsDto Save(PortalSettingsDto settings)
	{
		var valid = Validate(settings);
		var path = filePath();
		var temp = path + ".tmp";

		// Written aside and moved into place: a container killed mid-write leaves the old file, not half of a new one.
		File.WriteAllText(temp, JsonSerializer.Serialize(valid, WriteOptions));
		File.Move(temp, path, overwrite: true);

		Serilog.Log.Logger.Information("Web portal: portal settings saved. {@Settings}", valid);
		return valid;
	}

	/// <exception cref="ArgumentException">A field the page would never send.</exception>
	public static PortalSettingsDto Validate(PortalSettingsDto? settings)
	{
		var mode = (settings?.ScanMode ?? "").Trim().ToLowerInvariant();
		if (mode is not (Manual or Automatic))
			throw new ArgumentException($"scanMode must be '{Manual}' or '{Automatic}'.");

		var hours = settings!.ScanEveryHours;
		if (!AllowedHours.Contains(hours))
			throw new ArgumentException($"scanEveryHours must be one of {string.Join(", ", AllowedHours)}.");

		return new PortalSettingsDto(mode, hours, settings.DownloadAfterScan);
	}
}

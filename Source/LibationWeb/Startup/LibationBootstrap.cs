using AppScaffolding;
using FileManager;
using LibationFileManager;
using System;
using System.IO;

namespace LibationWeb.Startup;

/// <summary>
/// Thrown for a configuration problem the operator has to fix before the portal can start. The message
/// is written to the console as-is, so it must say what to do, not just what went wrong.
/// </summary>
public sealed class StartupException : Exception
{
	public StartupException(string message) : base(message) { }
}

/// <summary>
/// Brings Libation up the way LibationCli does, headless: locate the Libation files folder, run the
/// migrations, configure logging, then apply the environment overrides Docker and servers rely on.
/// Settings are loaded ephemerally so nothing this process does is ever written back to Settings.json.
/// </summary>
public static class LibationBootstrap
{
	public const string BooksDirEnvVar = "LIBATION_BOOKS_DIR";
	public const string InProgressDirEnvVar = "LIBATION_INPROGRESS_DIR";

	public static Configuration Initialize(string[] args)
	{
		ApplyLibationFilesArgument(args);

		//***********************************************//
		//                                               //
		//   do not use Configuration before this line   //
		//                                               //
		//***********************************************//
		var config = LibationScaffolding.RunPreConfigMigrations();

		if (!Directory.Exists(config.LibationFiles.Location))
			throw new StartupException(
				$"Cannot find the Libation files folder at '{config.LibationFiles.Location}'. "
				+ $"Point the {LibationFiles.LIBATION_FILES_DIR} environment variable (or --libationFiles) at the folder that holds Settings.json and AccountsSettings.json.");

		if (!File.Exists(config.LibationFiles.SettingsFilePath))
			throw new StartupException(
				$"Cannot find '{config.LibationFiles.SettingsFilePath}'. "
				+ "Run the Libation desktop app once against this folder, or copy its Settings.json here.");

		LibationScaffolding.RunPostConfigMigrations(config, ephemeralSettings: true);
		LibationScaffolding.RunPostMigrationScaffolding(Variety.Chardonnay, config);

		ApplyEnvironmentOverrides(config);

		// Advisory only, exactly like the CLI: the portal is meant to run beside a desktop install.
		using var guiInstanceCheck = SingleInstance.TryAcquire(config.LibationFiles.Location);
		if (!guiInstanceCheck.IsFirstInstance)
			Serilog.Log.Logger.Warning(
				"Another Libation instance is running against this LibationFiles folder. "
				+ "The web portal will continue, but concurrent writes may conflict.");

		return config;
	}

	/// <summary>
	/// <c>--libationFiles &lt;dir&gt;</c>, the CLI's spelling, so the two can be pointed at the same folder
	/// with the same words. It is applied through the environment variable Libation already honours.
	/// </summary>
	private static void ApplyLibationFilesArgument(string[] args)
	{
		for (var i = 0; i < args.Length - 1; i++)
		{
			if (!args[i].Equals("--libationFiles", StringComparison.OrdinalIgnoreCase))
				continue;

			var dir = Path.GetFullPath(args[i + 1]);
			if (!Directory.Exists(dir))
				throw new StartupException($"--libationFiles: directory not found: {dir}");

			Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, dir);
			return;
		}
	}

	/// <summary>
	/// The two folders a container has to relocate. Applied to the ephemeral settings, so the host's
	/// Settings.json is untouched; the Docker entrypoint sets the same two keys with jq for the CLI image.
	/// </summary>
	internal static void ApplyEnvironmentOverrides(Configuration config)
	{
		var books = Environment.GetEnvironmentVariable(BooksDirEnvVar);
		if (!string.IsNullOrWhiteSpace(books))
			config.Books = (LongPath)books.Trim();

		var inProgress = Environment.GetEnvironmentVariable(InProgressDirEnvVar);
		if (!string.IsNullOrWhiteSpace(inProgress))
			config.InProgress = inProgress.Trim();
	}
}

using LibationWeb.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace LibationWeb.Tests;

/// <summary>
/// The portal's own settings file. The store takes its path as a delegate precisely so this never opens
/// the real Libation folder.
/// </summary>
[TestClass]
public class PortalSettingsStoreTests
{
	private string dir = "";
	private string file = "";

	[TestInitialize]
	public void Setup()
	{
		dir = Path.Combine(Path.GetTempPath(), "LibationWeb.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		file = Path.Combine(dir, PortalSettingsStore.FileName);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
	}

	private PortalSettingsStore Store() => new(() => file);

	[TestMethod]
	public void The_defaults_stand_in_when_there_is_no_file()
	{
		var settings = Store().Load();

		Assert.AreEqual("manual", settings.ScanMode);
		Assert.AreEqual(6, settings.ScanEveryHours);
		Assert.IsFalse(settings.DownloadAfterScan);
		Assert.IsFalse(File.Exists(file), "reading the settings must not create the file");
	}

	[TestMethod]
	public void Saved_settings_come_back_as_they_were_saved()
	{
		var saved = Store().Save(new PortalSettingsDto("automatic", 12, true));

		Assert.AreEqual(new PortalSettingsDto("automatic", 12, true), saved);
		Assert.IsTrue(File.Exists(file));
		Assert.IsFalse(File.Exists(file + ".tmp"), "the temp file is moved into place, not left behind");
		Assert.AreEqual(saved, Store().Load(), "a new store reads the same file");
	}

	[TestMethod]
	public void The_scan_mode_is_stored_the_way_it_is_compared()
	{
		var saved = Store().Save(new PortalSettingsDto("  Automatic ", 24, false));

		Assert.AreEqual("automatic", saved.ScanMode);
		Assert.AreEqual("automatic", Store().Load().ScanMode);
	}

	[TestMethod]
	public void A_corrupt_file_falls_back_to_the_defaults()
	{
		File.WriteAllText(file, "{ this is not json");

		Assert.AreEqual(PortalSettingsStore.Defaults, Store().Load());
	}

	[TestMethod]
	public void A_file_holding_a_value_the_page_never_sends_falls_back_to_the_defaults()
	{
		File.WriteAllText(file, """{"scanMode":"automatic","scanEveryHours":5,"downloadAfterScan":true}""");

		Assert.AreEqual(PortalSettingsStore.Defaults, Store().Load());
	}

	[TestMethod]
	public void A_scan_mode_that_is_not_manual_or_automatic_is_refused()
	{
		foreach (var mode in new[] { "", " ", "weekly", "Automatic scan", "null" })
			Assert.ThrowsExactly<ArgumentException>(() => Store().Save(new PortalSettingsDto(mode, 6, false)), mode);

		Assert.IsFalse(File.Exists(file), "a refused save writes nothing");
	}

	[TestMethod]
	public void An_interval_the_page_does_not_offer_is_refused()
	{
		foreach (var hours in new[] { 0, -1, 5, 7, 48 })
			Assert.ThrowsExactly<ArgumentException>(() => Store().Save(new PortalSettingsDto("automatic", hours, false)), hours.ToString());

		Assert.IsFalse(File.Exists(file), "a refused save writes nothing");
	}

	[TestMethod]
	public void Either_answer_to_download_after_scan_is_accepted()
	{
		// The only field with nothing to validate: anything that is not a JSON boolean is refused by the
		// request binding long before it reaches here.
		Assert.IsTrue(Store().Save(new PortalSettingsDto("automatic", 1, true)).DownloadAfterScan);
		Assert.IsFalse(Store().Save(new PortalSettingsDto("automatic", 1, false)).DownloadAfterScan);
	}
}

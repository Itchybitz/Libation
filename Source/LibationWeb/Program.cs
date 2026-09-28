using ApplicationServices;
using LibationFileManager;
using LibationWeb.Api;
using LibationWeb.Auth;
using LibationWeb.Services;
using LibationWeb.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;

namespace LibationWeb;

public static class Program
{
	public const string UrlsEnvVar = "LIBATION_WEB_URLS";
	public const string ConcurrencyEnvVar = "LIBATION_WEB_CONCURRENCY";
	public const string TrustProxyEnvVar = "LIBATION_WEB_TRUST_PROXY";
	public const string DefaultUrl = "http://0.0.0.0:8791";

	public static int Main(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;

		if (args.Any(a => a is "--help" or "-h" or "/?"))
		{
			PrintHelp();
			return 0;
		}

		var auth = new PortalAuth(PortalAuthOptions.FromEnvironment());
		if (auth.StartupProblem() is string authProblem)
		{
			Console.Error.WriteLine("LibationWeb cannot start: " + authProblem);
			return 2;
		}

		Configuration config;
		try
		{
			config = LibationBootstrap.Initialize(args);
		}
		catch (StartupException ex)
		{
			Console.Error.WriteLine("LibationWeb cannot start: " + ex.Message);
			return 2;
		}

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			Args = args,
			ContentRootPath = AppContext.BaseDirectory,
			WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
		});

		// --urls and ASPNETCORE_URLS win; otherwise the portal's own variable, otherwise every interface on 8791.
		if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
			builder.WebHost.UseUrls(Environment.GetEnvironmentVariable(UrlsEnvVar) is { Length: > 0 } urls ? urls : DefaultUrl);

		builder.Logging.ClearProviders();
		builder.Logging.AddSimpleConsole(o =>
		{
			o.SingleLine = true;
			o.TimestampFormat = "HH:mm:ss ";
		});
		builder.Logging.SetMinimumLevel(LogLevel.Information);
		// Request-level chatter would be copied into Libation's own log file by the console capture.
		builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

		builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
		builder.Services.AddSingleton(auth);
		builder.Services.AddSingleton<EventHub>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddSingleton<ScanService>();
		builder.Services.AddSingleton(sp => new DownloadQueue(LibationProcessor.ProcessAsync, sp.GetRequiredService<EventHub>(), WorkerCount()));
		builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadQueue>());
		builder.Services.AddSingleton(sp => new AccountLoginService(
			sp.GetRequiredService<EventHub>(),
			// Forgetting an account mid-scan or mid-download would pull its tokens out from under the job.
			busy: () => LibraryCommands.Scanning || sp.GetRequiredService<DownloadQueue>().Snapshot().Working > 0));
		builder.Services.AddSingleton(_ => new PortalSettingsStore());
		builder.Services.AddSingleton(sp => new ScanScheduler(
			sp.GetRequiredService<PortalSettingsStore>(),
			sp.GetRequiredService<EventHub>(),
			new ScanSchedulerHooks
			{
				ScanState = () => sp.GetRequiredService<ScanService>().State,
				LibraryBusy = () => LibraryCommands.Scanning,
				// Both of these write where a scan writes, or hold tokens a scan needs; a due scan waits a minute instead.
				SignInPending = () => sp.GetRequiredService<AccountLoginService>().State.Pending is not null,
				DownloadsWorking = () => sp.GetRequiredService<DownloadQueue>().Snapshot().Working > 0,
				StartScan = () => sp.GetRequiredService<ScanService>().Start(null),
				EnqueueNewDownloads = () => EnqueueNewDownloads(sp),
			}));
		builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanScheduler>());

		var trustProxy = ConfigureForwardedHeaders(builder.Services);

		var app = builder.Build();

		if (trustProxy)
			app.UseForwardedHeaders();

		app.Use(async (ctx, next) =>
		{
			ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
			ctx.Response.Headers["Referrer-Policy"] = "same-origin";
			if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.Value!.EndsWith("/cover", StringComparison.OrdinalIgnoreCase))
				ctx.Response.Headers.CacheControl = "no-store";
			await next();
		});

		app.Use(async (ctx, next) =>
		{
			if (ctx.Request.Path.StartsWithSegments("/api") && !ApiEndpoints.IsPublic(ctx.Request.Path))
			{
				var result = auth.Authenticate(ctx.Request);
				if (!result.Ok)
				{
					ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
					await ctx.Response.WriteAsJsonAsync(new { error = new { code = "unauthenticated", message = "Sign in first." } });
					return;
				}

				// A cookie session only mutates through JSON the page itself sends; a cross-site form post never is.
				if (result.Via == AuthVia.Cookie
					&& !HttpMethods.IsGet(ctx.Request.Method)
					&& ctx.Request.ContentLength is > 0
					&& !(ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
				{
					ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
					await ctx.Response.WriteAsJsonAsync(new { error = new { code = "unsupported_media_type", message = "Send JSON with Content-Type: application/json." } });
					return;
				}
			}

			await next();
		});

		app.UseDefaultFiles();
		app.UseStaticFiles();
		app.MapApi();
		app.MapFallbackToFile("index.html");

		// A finished download changes a row in the library; tell the page so the Library view catches up.
		var queue = app.Services.GetRequiredService<DownloadQueue>();
		var library = app.Services.GetRequiredService<LibraryService>();
		var hub = app.Services.GetRequiredService<EventHub>();
		queue.JobFinished += job =>
		{
			library.Invalidate();
			hub.Publish("library", new { reason = "job", asin = job.Asin, status = job.Status });
		};

		var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
		lifetime.ApplicationStarted.Register(() => PrintBanner(app, config, auth));

		app.Run();
		return 0;
	}

	/// <summary>
	/// What <c>POST /api/queue/new</c> queues, queued by the scheduler after an automatic scan. It lives
	/// here rather than in the scheduler so that the scheduler itself never touches Libation.
	/// </summary>
	private static int EnqueueNewDownloads(IServiceProvider services)
	{
		var queue = services.GetRequiredService<DownloadQueue>();
		var library = services.GetRequiredService<LibraryService>();

		var added = 0;
		foreach (var lb in LibationProcessor.SelectNewDownloads(library.GetLibrary(), Configuration.Instance, includeDeferred: false))
		{
			if (queue.Enqueue(ApiEndpoints.NewJob(lb, QueueJobKind.Download, force: false)).Job is not null)
				added++;
		}
		return added;
	}

	private static int WorkerCount()
	{
		var raw = Environment.GetEnvironmentVariable(ConcurrencyEnvVar);
		return int.TryParse(raw, out var n) ? Math.Clamp(n, 1, 4) : 1;
	}

	/// <summary>
	/// Behind a reverse proxy (DSM, Tailscale Serve, nginx) the portal only sees the proxy's address and plain
	/// HTTP. Naming the proxy here makes the sign-in throttle and the Secure cookie flag see the real client.
	/// </summary>
	private static bool ConfigureForwardedHeaders(IServiceCollection services)
	{
		var raw = Environment.GetEnvironmentVariable(TrustProxyEnvVar)?.Trim();
		if (string.IsNullOrEmpty(raw))
			return false;

		services.Configure<ForwardedHeadersOptions>(o =>
		{
			o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
			o.KnownNetworks.Clear();
			o.KnownProxies.Clear();
			if (raw.Equals("any", StringComparison.OrdinalIgnoreCase))
				return;

			foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				// "172.18.0.1/32" is how Nightstand's README spells a single proxy; accept it as the address.
				var address = entry.Split('/')[0];
				if (IPAddress.TryParse(address, out var ip))
					o.KnownProxies.Add(ip);
				else
					Console.Error.WriteLine($"{TrustProxyEnvVar}: ignoring '{entry}' (not an IP address)");
			}
		});
		return true;
	}

	private static void PrintBanner(WebApplication app, Configuration config, PortalAuth auth)
	{
		var booksDir = config.Books?.Path ?? "(not set)";
		var booksExist = AudibleFileStorage.BooksDirectory is not null;
		var lines = new[]
		{
			$"LibationWeb {ApiEndpoints.Version}",
			$"  Libation files: {config.LibationFiles.Location.Path}",
			$"  Books:          {booksDir}{(booksExist ? "" : "  <-- NOT FOUND: downloads will fail until this exists")}",
			$"  Database:       {SqliteStorage.DatabasePath}",
			$"  Sign-in:        {(auth.AllowAnonymous ? "ANONYMOUS (no password - keep this off the internet)" : $"password, sessions last {auth.SessionLifetime.TotalDays:0} days")}",
			$"  API token:      {(auth.ApiTokenConfigured ? "set" : "not set")}",
			$"  Listening:      {string.Join(", ", app.Urls)}",
		};

		foreach (var line in lines)
			Console.WriteLine(line);

		Serilog.Log.Logger.Information("Web portal started. {@Portal}", new
		{
			Urls = app.Urls,
			BooksDirectoryExists = booksExist,
			auth.AllowAnonymous,
			ApiToken = auth.ApiTokenConfigured,
		});
	}

	private static void PrintHelp()
	{
		Console.WriteLine($"""
			LibationWeb {ApiEndpoints.Version} - Libation's web portal (remote library, scan and download queue)

			Usage: LibationWeb [--libationFiles <dir>] [--urls <url;url>]

			Environment:
			  {LibationFileManager.LibationFiles.LIBATION_FILES_DIR}     Libation files folder (Settings.json, AccountsSettings.json, database). Default: the desktop app's.
			  {PortalAuthOptions.PasswordEnvVar}    Sign-in password (8+ characters). Required unless anonymous access is allowed.
			  {PortalAuthOptions.ApiTokenEnvVar}       Optional bearer token for another server (e.g. Nightstand) to call the API.
			  {PortalAuthOptions.AllowAnonymousEnvVar}  1 = no sign-in. Only on a private network (Tailscale, LAN without port forwarding).
			  {PortalAuthOptions.SessionDaysEnvVar} How long a sign-in lasts (default 30).
			  {UrlsEnvVar}        Listen address(es). Default {DefaultUrl}.
			  {ConcurrencyEnvVar} Books downloaded at once, 1-4 (default 1).
			  {TrustProxyEnvVar} Reverse proxy address(es) whose X-Forwarded-* headers to believe, or 'any'.
			  {LibationBootstrap.BooksDirEnvVar}      Override the Books folder for this process only.
			  {LibationBootstrap.InProgressDirEnvVar} Override the in-progress folder for this process only.
			  LIBATION_MASTER_KEY_FILE / LIBATION_MASTER_KEY  Decrypt tokens exported from another machine (see the Docker docs).
			""");
	}
}

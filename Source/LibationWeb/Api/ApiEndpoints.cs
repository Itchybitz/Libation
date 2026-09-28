using ApplicationServices;
using AppScaffolding;
using DataLayer;
using LibationFileManager;
using LibationWeb.Auth;
using LibationWeb.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LibationWeb.Api;

public sealed record LoginRequest(string? Password);
public sealed record ScanRequest(string[]? Accounts);
public sealed record EnqueueRequest(string[]? Asins, string? Kind, bool? Force);
public sealed record EnqueueAllRequest(bool? Force);
public sealed record SetStatusRequest(string? Status);
public sealed record EnqueueSkip(string Asin, string Reason);
public sealed record EnqueueResponse(IReadOnlyList<QueueJobDto> Added, IReadOnlyList<EnqueueSkip> Skipped);
public sealed record AccountLoginRequest(string? AccountId, string? Locale, string? DeviceRegistration);
public sealed record AccountLoginCompleteRequest(string? FlowId, string? ResponseUrl);
public sealed record AccountLoginCancelRequest(string? FlowId);
public sealed record PortalSettingsRequest(string? ScanMode, int? ScanEveryHours, bool? DownloadAfterScan);

/// <summary>
/// The portal's HTTP API. Paths are under <c>/api</c>; errors are <c>{ "error": { "code", "message" } }</c>,
/// the shape Nightstand's own API uses, so a proxy there can pass them through untouched.
/// </summary>
public static class ApiEndpoints
{
	private static readonly string[] PublicPaths = ["/api/health", "/api/auth/status", "/api/auth/login", "/api/auth/logout"];
	private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;

	public static bool IsPublic(PathString path)
		=> PublicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

	public static string Version => LibationScaffolding.BuildVersion?.ToVersionString() ?? "[unknown]";

	private static IResult Error(int status, string code, string message)
		=> Results.Json(new { error = new { code, message } }, statusCode: status);

	private static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

	public static void MapApi(this WebApplication app)
	{
		var api = app.MapGroup("/api");

		api.MapGet("/health", (PortalAuth auth) => Results.Ok(new
		{
			ok = true,
			app = "LibationWeb",
			version = Version,
			authRequired = !auth.AllowAnonymous,
		}));

		MapAuth(api);
		MapLibrary(api);
		MapAccounts(api);
		MapScan(api);
		MapQueue(api);
		MapSettings(api);
		MapEvents(api);

		api.MapGet("/logs", (int? lines) =>
		{
			var count = Math.Clamp(lines ?? 200, 1, 2000);
			var files = Configuration.Instance.LibationFiles;
			var directory = LogTail.ResolveLogDirectory(files.SettingsFilePath, files.Location);
			var file = LogTail.NewestLogFile(directory);
			return Results.Ok(new
			{
				file = PathDisplay.Pretty(file),
				lines = file is null ? [] : LogTail.Tail(file, count),
			});
		});

		// Anything else under /api is a typo, not the single-page app.
		api.Map("/{**rest}", () => Error(404, "not_found", "No such API route."));
	}

	private static void MapAuth(RouteGroupBuilder api)
	{
		api.MapGet("/auth/status", (HttpContext ctx, PortalAuth auth) =>
		{
			var result = auth.Authenticate(ctx.Request);
			return Results.Ok(new
			{
				authenticated = result.Ok,
				via = result.Via.ToString().ToLowerInvariant(),
				allowAnonymous = auth.AllowAnonymous,
				passwordConfigured = auth.PasswordConfigured,
			});
		});

		api.MapPost("/auth/login", (HttpContext ctx, PortalAuth auth, LoginRequest? body) =>
		{
			if (auth.AllowAnonymous && !auth.PasswordConfigured)
				return Results.NoContent();

			var key = ClientKey(ctx);
			if (auth.ThrottleRemaining(key) is TimeSpan wait)
			{
				var seconds = (int)Math.Ceiling(wait.TotalSeconds);
				ctx.Response.Headers.RetryAfter = seconds.ToString();
				return Error(429, "throttled", $"Too many attempts. Try again in {seconds}s.");
			}

			if (!auth.VerifyPassword(body?.Password))
			{
				auth.RecordFailure(key);
				return Error(401, "invalid_credentials", "That password was not recognized.");
			}

			auth.RecordSuccess(key);
			var token = auth.IssueSession();
			ctx.Response.Cookies.Append(auth.CookieName, token, new CookieOptions
			{
				HttpOnly = true,
				SameSite = SameSiteMode.Lax,
				Secure = ctx.Request.IsHttps,
				Path = "/",
				Expires = DateTimeOffset.UtcNow + auth.SessionLifetime,
				IsEssential = true,
			});
			Serilog.Log.Logger.Information("Web portal: sign-in from {Client}", key);
			return Results.NoContent();
		});

		api.MapPost("/auth/logout", (HttpContext ctx, PortalAuth auth) =>
		{
			if (ctx.Request.Cookies.TryGetValue(auth.CookieName, out var token))
				auth.RevokeSession(token);
			ctx.Response.Cookies.Delete(auth.CookieName, new CookieOptions { Path = "/" });
			return Results.NoContent();
		});
	}

	private static void MapLibrary(RouteGroupBuilder api)
	{
		api.MapGet("/status", (LibraryService library, ScanService scan, DownloadQueue queue, ScanScheduler scheduler, PortalAuth auth) =>
		{
			var config = Configuration.Instance;
			var snapshot = queue.Snapshot();
			var (accounts, accountsError) = scan.TryListAccounts();
			LibraryCounts? counts = null;
			string? libraryError = null;
			try
			{
				counts = library.Counts();
			}
			catch (Exception ex)
			{
				Serilog.Log.Logger.Error(ex, "Web portal: could not read the library");
				libraryError = ErrorExplainer.Explain(ex);
			}

			return Results.Ok(new
			{
				version = Version,
				libationFiles = PathDisplay.Pretty(config.LibationFiles.Location.Path),
				booksDirectory = PathDisplay.Pretty(config.Books?.Path),
				booksDirectoryExists = AudibleFileStorage.BooksDirectory is not null,
				inProgress = PathDisplay.Pretty(config.InProgress),
				database = PathDisplay.Pretty(SqliteStorage.DatabasePath),
				scan = scan.State,
				schedule = scheduler.Schedule(),
				queue = new { snapshot.Queued, snapshot.Working, snapshot.Completed, snapshot.Failed, snapshot.Skipped, snapshot.Cancelled },
				counts,
				libraryError,
				accounts,
				accountsError,
				settings = new
				{
					workers = queue.WorkerCount,
					maxConcurrentDownloads = config.MaxConcurrentDownloads,
					downloadEpisodes = config.DownloadEpisodes,
					audiobookshelfEnabled = config.AudiobookshelfEnabled,
					dailyLimit = config.DailyDownloadLimit.ToString(),
					dailyLimitQuantity = config.DailyDownloadLimitQuantity,
				},
				auth = new { allowAnonymous = auth.AllowAnonymous, apiToken = auth.ApiTokenConfigured, sessions = auth.ActiveSessionCount },
				serverTime = DateTimeOffset.Now,
				uptimeSeconds = (int)(DateTimeOffset.Now - StartedAt).TotalSeconds,
			});
		});

		api.MapGet("/accounts", (ScanService scan) =>
		{
			var (accounts, error) = scan.TryListAccounts();
			return error is null ? Results.Ok(accounts) : Error(500, "accounts_unreadable", error);
		});

		api.MapGet("/books", (LibraryService library, string? q, string? status, string? sort, int page = 1, int pageSize = 100)
			=> Results.Ok(library.Query(q, status, sort, page, pageSize)));

		api.MapGet("/books/{asin}", (string asin, LibraryService library)
			=> library.Find(asin) is { } lb ? Results.Ok(library.ToDetail(lb)) : Error(404, "not_found", "Not in the library."));

		api.MapGet("/books/{asin}/cover", async (HttpContext ctx, string asin, int? size, LibraryService library, CancellationToken cancellationToken) =>
		{
			var pictureId = library.Find(asin)?.Book is { } book ? book.PictureId ?? book.PictureLarge : null;
			if (pictureId is null)
				return Results.NotFound();

			var pictureSize = size switch
			{
				<= 80 => PictureSize._80x80,
				<= 300 => PictureSize._300x300,
				_ => PictureSize._500x500,
			};

			var bytes = await LibraryService.GetCoverAsync(pictureId, pictureSize, cancellationToken);
			if (bytes is null)
				return Results.NotFound();

			ctx.Response.Headers.CacheControl = "private, max-age=604800";
			return Results.File(bytes, "image/jpeg");
		});

		api.MapPost("/books/{asin}/status", async (string asin, SetStatusRequest? body, LibraryService library, EventHub hub) =>
		{
			if (library.Find(asin) is not { } lb)
				return Error(404, "not_found", "Not in the library.");

			if (!Enum.TryParse<LiberatedStatus>(body?.Status, ignoreCase: true, out var status)
				|| status is not (LiberatedStatus.NotLiberated or LiberatedStatus.Liberated or LiberatedStatus.Error))
				return Error(400, "bad_request", "status must be NotLiberated, Liberated or Error.");

			await lb.UpdateBookStatusAsync(status);
			library.Invalidate();
			hub.Publish("library", new { reason = "status", asin });
			return library.Find(asin) is { } updated ? Results.Ok(LibraryService.ToDto(updated)) : Results.NoContent();
		});
	}

	/// <summary>
	/// Adding an Audible account and signing it in again, the way <c>login-external</c> does it: the portal
	/// hands out Amazon's sign-in URL and takes back the address the browser finished on. No password ever
	/// passes through here.
	/// </summary>
	private static void MapAccounts(RouteGroupBuilder api)
	{
		api.MapGet("/accounts/locales", () => Results.Ok(AccountLoginService.KnownLocales()));

		api.MapGet("/accounts/login", (AccountLoginService login) => Results.Ok(login.State));

		api.MapPost("/accounts/login", async (AccountLoginRequest? body, AccountLoginService login) =>
		{
			try
			{
				var started = await login.StartAsync(body?.AccountId, body?.Locale, body?.DeviceRegistration);
				return started.Flow is { } flow
					? Results.Json(flow, statusCode: StatusCodes.Status202Accepted)
					: Results.Ok(new { alreadySignedIn = true, accountId = started.AccountId, locale = started.Locale });
			}
			catch (ArgumentException ex)
			{
				return Error(400, "bad_request", ex.Message);
			}
			catch (InvalidOperationException ex)
			{
				return Error(409, "login_pending", ex.Message);
			}
			catch (Exception ex)
			{
				Serilog.Log.Logger.Error(ex, "Web portal: could not start an Audible sign-in");
				return Error(502, "login_failed", ErrorExplainer.Explain(ex));
			}
		});

		api.MapPost("/accounts/login/complete", async (AccountLoginCompleteRequest? body, AccountLoginService login) =>
		{
			try
			{
				var result = await login.CompleteAsync(body?.FlowId, body?.ResponseUrl);
				return Results.Ok(new { status = result.Status, error = result.Error });
			}
			catch (KeyNotFoundException ex)
			{
				return Error(404, "no_such_flow", ex.Message);
			}
			catch (ArgumentException ex)
			{
				return Error(400, "bad_request", ex.Message);
			}
		});

		api.MapPost("/accounts/login/cancel", async (AccountLoginCancelRequest? body, AccountLoginService login)
			=> await login.CancelAsync(body?.FlowId)
				? Results.NoContent()
				: Error(404, "no_such_flow", "That sign-in is no longer waiting."));

		api.MapDelete("/accounts/{accountId}", (string accountId, string? locale, AccountLoginService login) =>
		{
			try
			{
				// The books stay: Libation keeps them when an account goes, and so does the portal.
				return login.Remove(accountId, locale)
					? Results.NoContent()
					: Error(404, "not_found", "No such account for that locale.");
			}
			catch (ArgumentException ex)
			{
				return Error(400, "bad_request", ex.Message);
			}
			catch (InvalidOperationException ex)
			{
				return Error(409, "busy", ex.Message);
			}
		});
	}

	private static void MapScan(RouteGroupBuilder api)
	{
		api.MapGet("/scan", (ScanService scan) => Results.Ok(scan.State));

		api.MapPost("/scan", (ScanRequest? body, ScanService scan) =>
		{
			if (scan.State.Running || LibraryCommands.Scanning)
				return Error(409, "scan_running", "A scan is already running.");

			try
			{
				return Results.Json(scan.Start(body?.Accounts), statusCode: StatusCodes.Status202Accepted);
			}
			catch (InvalidOperationException ex)
			{
				return Error(409, "scan_unavailable", ex.Message);
			}
		});
	}

	private static void MapQueue(RouteGroupBuilder api)
	{
		api.MapGet("/queue", (DownloadQueue queue) => Results.Ok(queue.Snapshot()));

		api.MapPost("/queue", (EnqueueRequest? body, DownloadQueue queue, LibraryService library) =>
		{
			var asins = (body?.Asins ?? []).Select(a => a.Trim().TrimStart('[').TrimEnd(']')).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (asins.Count == 0)
				return Error(400, "bad_request", "Send at least one ASIN in 'asins'.");

			var kind = ParseKind(body?.Kind);
			if (kind is null)
				return Error(400, "bad_request", "kind must be download, pdf or convert.");

			var added = new List<QueueJobDto>();
			var skipped = new List<EnqueueSkip>();
			foreach (var asin in asins)
			{
				if (library.Find(asin) is not { } lb)
				{
					skipped.Add(new EnqueueSkip(asin, "Not in the library."));
					continue;
				}

				var result = queue.Enqueue(NewJob(lb, kind.Value, body?.Force ?? false));
				if (result.Job is not null)
					added.Add(result.Job.ToDto());
				else
					skipped.Add(new EnqueueSkip(lb.Book.AudibleProductId, result.SkippedReason ?? "Skipped."));
			}

			return Results.Json(new EnqueueResponse(added, skipped), statusCode: added.Count > 0 ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
		});

		api.MapPost("/queue/new", (EnqueueAllRequest? body, DownloadQueue queue, LibraryService library) =>
		{
			var force = body?.Force ?? false;
			var added = new List<QueueJobDto>();
			var skipped = new List<EnqueueSkip>();
			foreach (var lb in LibationProcessor.SelectNewDownloads(library.GetLibrary(), Configuration.Instance, includeDeferred: force))
			{
				var result = queue.Enqueue(NewJob(lb, QueueJobKind.Download, force));
				if (result.Job is not null)
					added.Add(result.Job.ToDto());
				else
					skipped.Add(new EnqueueSkip(lb.Book.AudibleProductId, result.SkippedReason ?? "Skipped."));
			}

			return Results.Json(new EnqueueResponse(added, skipped), statusCode: added.Count > 0 ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
		});

		api.MapDelete("/queue/{id}", async (string id, DownloadQueue queue) =>
		{
			var job = queue.Find(id);
			if (job is null)
				return Error(404, "not_found", "No such job.");
			if (!job.IsActive)
				return Error(409, "already_finished", "That job has already finished.");

			await queue.CancelAsync(id);
			return Results.NoContent();
		});

		api.MapPost("/queue/clear", (DownloadQueue queue) => Results.Ok(new { removed = queue.ClearFinished() }));
	}

	private static QueueJobKind? ParseKind(string? kind) => (kind ?? "download").Trim().ToLowerInvariant() switch
	{
		"download" or "liberate" => QueueJobKind.Download,
		"pdf" => QueueJobKind.Pdf,
		"convert" or "mp3" or "convertmp3" => QueueJobKind.ConvertMp3,
		_ => null,
	};

	/// <summary>Internal so the scan scheduler queues a title exactly as <c>POST /api/queue/new</c> does.</summary>
	internal static QueueJob NewJob(LibraryBook lb, QueueJobKind kind, bool force) => new()
	{
		Asin = lb.Book.AudibleProductId,
		Kind = kind,
		Force = force,
		Title = lb.Book.TitleWithSubtitle,
		Authors = lb.Book.AuthorNames,
		PictureId = lb.Book.PictureId ?? lb.Book.PictureLarge,
	};

	/// <summary>
	/// The portal's own settings - the scan schedule. Libation's settings are the desktop app's business;
	/// these are written to LibationWeb.json beside them and nowhere else.
	/// </summary>
	private static void MapSettings(RouteGroupBuilder api)
	{
		api.MapGet("/settings/portal", (ScanScheduler scheduler)
			=> Results.Ok(new { settings = scheduler.Settings, schedule = scheduler.Schedule() }));

		api.MapPut("/settings/portal", (PortalSettingsRequest? body, PortalSettingsStore store, ScanScheduler scheduler, EventHub hub) =>
		{
			try
			{
				// A missing field is refused rather than silently defaulted: the page always sends all three.
				var saved = store.Save(new PortalSettingsDto(body?.ScanMode ?? "", body?.ScanEveryHours ?? 0, body?.DownloadAfterScan ?? false));
				var schedule = scheduler.Reload();
				hub.Publish("schedule", schedule);
				return Results.Ok(new { settings = saved, schedule });
			}
			catch (ArgumentException ex)
			{
				return Error(400, "bad_request", ex.Message);
			}
			catch (Exception ex)
			{
				Serilog.Log.Logger.Error(ex, "Web portal: could not save the portal settings");
				return Error(500, "settings_unwritable", ErrorExplainer.Explain(ex));
			}
		});
	}

	private static void MapEvents(RouteGroupBuilder api)
	{
		api.MapGet("/events", async (HttpContext ctx, EventHub hub, ScanService scan, DownloadQueue queue, LibraryService library, CancellationToken cancellationToken) =>
		{
			ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers["X-Accel-Buffering"] = "no";
			ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

			var (id, reader) = hub.Subscribe();
			try
			{
				await WriteAsync(EventHub.Frame("hello", new
				{
					scan = scan.State,
					queue = queue.Snapshot(),
					counts = library.Counts(),
					serverTime = DateTimeOffset.Now,
				}));

				while (!cancellationToken.IsCancellationRequested)
				{
					using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
					try
					{
						if (!await reader.WaitToReadAsync(heartbeat.Token))
							break;
						while (reader.TryRead(out var frame))
							await WriteAsync(frame);
					}
					catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
					{
						await WriteAsync(": ping\n\n");
					}
				}
			}
			catch (OperationCanceledException)
			{
				// client went away
			}
			catch (ChannelClosedException)
			{
				// hub shut down
			}
			finally
			{
				hub.Unsubscribe(id);
			}

			async Task WriteAsync(string text)
			{
				await ctx.Response.WriteAsync(text, cancellationToken);
				await ctx.Response.Body.FlushAsync(cancellationToken);
			}
		});
	}
}

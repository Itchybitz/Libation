using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LibationWeb.Auth;

public enum AuthVia { None, Cookie, Bearer, Anonymous }

public readonly record struct AuthResult(bool Ok, AuthVia Via)
{
	public static AuthResult Denied => new(false, AuthVia.None);
}

public sealed class PortalAuthOptions
{
	public const string PasswordEnvVar = "LIBATION_WEB_PASSWORD";
	public const string ApiTokenEnvVar = "LIBATION_WEB_TOKEN";
	public const string AllowAnonymousEnvVar = "LIBATION_WEB_ALLOW_ANONYMOUS";
	public const string SessionDaysEnvVar = "LIBATION_WEB_SESSION_DAYS";

	/// <summary>The one password the portal's sign-in screen accepts. Null means sign-in is impossible.</summary>
	public string? Password { get; init; }

	/// <summary>Optional shared secret for server-to-server callers (Nightstand), sent as a bearer token.</summary>
	public string? ApiToken { get; init; }

	/// <summary>
	/// Skip authentication entirely. Only for a portal that is unreachable from outside a private network
	/// (Tailscale, a LAN with no port forward). Never combine with a public reverse proxy.
	/// </summary>
	public bool AllowAnonymous { get; init; }

	public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(30);
	public string CookieName { get; init; } = "libation_web_session";

	public static PortalAuthOptions FromEnvironment()
	{
		var days = 30;
		if (int.TryParse(Environment.GetEnvironmentVariable(SessionDaysEnvVar), out var parsed) && parsed is >= 1 and <= 3650)
			days = parsed;

		return new PortalAuthOptions
		{
			Password = NullIfBlank(Environment.GetEnvironmentVariable(PasswordEnvVar)),
			ApiToken = NullIfBlank(Environment.GetEnvironmentVariable(ApiTokenEnvVar)),
			AllowAnonymous = Environment.GetEnvironmentVariable(AllowAnonymousEnvVar)?.Trim() == "1",
			SessionLifetime = TimeSpan.FromDays(days),
		};
	}

	private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// One password, opaque session cookies, an optional bearer token for another server, and a per-address
/// sign-in throttle. Sessions live in memory: a restart signs every device out, which for a portal that
/// controls downloads on the owner's Audible account is the right default.
/// </summary>
public sealed class PortalAuth
{
	public const int FailuresBeforeThrottle = 5;
	private static readonly TimeSpan ThrottleBase = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ThrottleCap = TimeSpan.FromMinutes(10);

	private readonly PortalAuthOptions options;
	private readonly TimeProvider time;
	private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new();
	private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset BlockedUntil)> throttle = new();

	public PortalAuth(PortalAuthOptions options, TimeProvider? time = null)
	{
		this.options = options;
		this.time = time ?? TimeProvider.System;
	}

	public bool AllowAnonymous => options.AllowAnonymous;
	public bool PasswordConfigured => options.Password is not null;
	public bool ApiTokenConfigured => options.ApiToken is not null;
	public string CookieName => options.CookieName;
	public TimeSpan SessionLifetime => options.SessionLifetime;

	/// <summary>Refuse to start a portal nobody could sign in to, or one that is open by accident.</summary>
	public string? StartupProblem()
	{
		if (options.AllowAnonymous)
			return null;
		if (options.Password is null)
			return $"No password is set. Set {PortalAuthOptions.PasswordEnvVar} (or, only on a private network, {PortalAuthOptions.AllowAnonymousEnvVar}=1).";
		if (options.Password.Length < 8)
			return $"{PortalAuthOptions.PasswordEnvVar} must be at least 8 characters.";
		return null;
	}

	public bool VerifyPassword(string? candidate)
	{
		if (options.Password is null || candidate is null)
			return false;

		var expected = Encoding.UTF8.GetBytes(options.Password);
		var actual = Encoding.UTF8.GetBytes(candidate);
		// FixedTimeEquals requires equal lengths; a length mismatch is a wrong password either way.
		return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
	}

	public bool VerifyApiToken(string? candidate)
	{
		if (options.ApiToken is null || string.IsNullOrEmpty(candidate))
			return false;

		var expected = Encoding.UTF8.GetBytes(options.ApiToken);
		var actual = Encoding.UTF8.GetBytes(candidate);
		return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
	}

	public string IssueSession()
	{
		PurgeExpired();
		var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
			.Replace('+', '-').Replace('/', '_').TrimEnd('=');
		sessions[Hash(token)] = time.GetUtcNow() + options.SessionLifetime;
		return token;
	}

	public bool ValidateSession(string? token)
	{
		if (string.IsNullOrEmpty(token))
			return false;
		if (!sessions.TryGetValue(Hash(token), out var expires))
			return false;
		if (expires > time.GetUtcNow())
			return true;

		sessions.TryRemove(Hash(token), out _);
		return false;
	}

	public void RevokeSession(string? token)
	{
		if (!string.IsNullOrEmpty(token))
			sessions.TryRemove(Hash(token), out _);
	}

	public int ActiveSessionCount
	{
		get
		{
			PurgeExpired();
			return sessions.Count;
		}
	}

	public AuthResult Authenticate(HttpRequest request)
	{
		if (TryReadBearer(request) is string bearer)
			return VerifyApiToken(bearer) ? new AuthResult(true, AuthVia.Bearer) : AuthResult.Denied;

		if (request.Cookies.TryGetValue(options.CookieName, out var cookie) && ValidateSession(cookie))
			return new AuthResult(true, AuthVia.Cookie);

		if (options.AllowAnonymous)
			return new AuthResult(true, AuthVia.Anonymous);

		return AuthResult.Denied;
	}

	private static string? TryReadBearer(HttpRequest request)
	{
		var header = request.Headers.Authorization.ToString();
		if (string.IsNullOrEmpty(header))
			return null;
		const string prefix = "Bearer ";
		return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
	}

	#region sign-in throttle

	/// <summary>How long the caller must wait before another attempt, or null when they may try now.</summary>
	public TimeSpan? ThrottleRemaining(string clientKey)
	{
		if (!throttle.TryGetValue(clientKey, out var entry))
			return null;
		var remaining = entry.BlockedUntil - time.GetUtcNow();
		return remaining > TimeSpan.Zero ? remaining : null;
	}

	public void RecordFailure(string clientKey)
	{
		throttle.AddOrUpdate(
			clientKey,
			_ => (1, DateTimeOffset.MinValue),
			(_, e) =>
			{
				var failures = e.Failures + 1;
				if (failures < FailuresBeforeThrottle)
					return (failures, DateTimeOffset.MinValue);

				var exponent = Math.Min(failures - FailuresBeforeThrottle, 8);
				var block = TimeSpan.FromTicks(Math.Min(ThrottleCap.Ticks, ThrottleBase.Ticks * (1L << exponent)));
				return (failures, time.GetUtcNow() + block);
			});
	}

	public void RecordSuccess(string clientKey) => throttle.TryRemove(clientKey, out _);

	#endregion

	private void PurgeExpired()
	{
		var now = time.GetUtcNow();
		foreach (var expired in sessions.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
			sessions.TryRemove(expired, out _);
	}

	private static string Hash(string token)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

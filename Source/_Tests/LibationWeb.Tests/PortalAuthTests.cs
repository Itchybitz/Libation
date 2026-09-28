using LibationWeb.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LibationWeb.Tests;

[TestClass]
public class PortalAuthTests
{
	private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

	private static PortalAuth Create(TestTime? time = null, string? password = "correct horse", string? token = null, bool anonymous = false)
		=> new(new PortalAuthOptions { Password = password, ApiToken = token, AllowAnonymous = anonymous, SessionLifetime = TimeSpan.FromDays(30) }, time ?? new TestTime(T0));

	[TestMethod]
	public void Password_must_match_exactly()
	{
		var auth = Create();
		Assert.IsTrue(auth.VerifyPassword("correct horse"));
		Assert.IsFalse(auth.VerifyPassword("correct horsE"));
		Assert.IsFalse(auth.VerifyPassword("correct hors"));
		Assert.IsFalse(auth.VerifyPassword(""));
		Assert.IsFalse(auth.VerifyPassword(null));
	}

	[TestMethod]
	public void No_password_means_nothing_verifies()
	{
		var auth = Create(password: null, anonymous: true);
		Assert.IsFalse(auth.VerifyPassword(""));
		Assert.IsFalse(auth.VerifyPassword("anything"));
	}

	[TestMethod]
	public void Startup_refuses_a_portal_nobody_can_use_or_that_is_open_by_accident()
	{
		Assert.IsNotNull(Create(password: null).StartupProblem(), "no password and not anonymous");
		Assert.IsNotNull(Create(password: "short").StartupProblem(), "password under 8 characters");
		Assert.IsNull(Create().StartupProblem());
		Assert.IsNull(Create(password: null, anonymous: true).StartupProblem(), "anonymous is a deliberate choice");
	}

	[TestMethod]
	public void Sessions_validate_until_they_expire()
	{
		var time = new TestTime(T0);
		var auth = Create(time);

		var token = auth.IssueSession();
		Assert.IsTrue(auth.ValidateSession(token));
		Assert.IsFalse(auth.ValidateSession(token + "x"));
		Assert.IsFalse(auth.ValidateSession(null));
		Assert.AreEqual(1, auth.ActiveSessionCount);

		time.Advance(TimeSpan.FromDays(29));
		Assert.IsTrue(auth.ValidateSession(token));

		time.Advance(TimeSpan.FromDays(2));
		Assert.IsFalse(auth.ValidateSession(token));
		Assert.AreEqual(0, auth.ActiveSessionCount);
	}

	[TestMethod]
	public void Revoking_a_session_signs_that_device_out()
	{
		var auth = Create();
		var a = auth.IssueSession();
		var b = auth.IssueSession();

		auth.RevokeSession(a);

		Assert.IsFalse(auth.ValidateSession(a));
		Assert.IsTrue(auth.ValidateSession(b));
	}

	[TestMethod]
	public void Tokens_are_unpredictable_and_distinct()
	{
		var auth = Create();
		var a = auth.IssueSession();
		var b = auth.IssueSession();
		Assert.AreNotEqual(a, b);
		Assert.IsTrue(a.Length >= 40, $"token too short: {a.Length}");
	}

	[TestMethod]
	public void Throttle_starts_after_five_failures_and_clears_on_success()
	{
		var time = new TestTime(T0);
		var auth = Create(time);

		for (var i = 0; i < PortalAuth.FailuresBeforeThrottle - 1; i++)
			auth.RecordFailure("1.2.3.4");
		Assert.IsNull(auth.ThrottleRemaining("1.2.3.4"), "four failures are not throttled");

		auth.RecordFailure("1.2.3.4");
		var wait = auth.ThrottleRemaining("1.2.3.4");
		Assert.IsNotNull(wait);
		Assert.AreEqual(30, wait.Value.TotalSeconds, 0.5);
		Assert.IsNull(auth.ThrottleRemaining("5.6.7.8"), "another address is unaffected");

		time.Advance(TimeSpan.FromSeconds(31));
		Assert.IsNull(auth.ThrottleRemaining("1.2.3.4"), "the block lapses");

		auth.RecordFailure("1.2.3.4");
		Assert.AreEqual(60, auth.ThrottleRemaining("1.2.3.4")!.Value.TotalSeconds, 0.5, "and doubles on the next failure");

		auth.RecordSuccess("1.2.3.4");
		Assert.IsNull(auth.ThrottleRemaining("1.2.3.4"));
	}

	[TestMethod]
	public void Throttle_is_capped_at_ten_minutes()
	{
		var auth = Create();
		for (var i = 0; i < 40; i++)
			auth.RecordFailure("x");
		Assert.IsTrue(auth.ThrottleRemaining("x")!.Value <= TimeSpan.FromMinutes(10));
	}

	[TestMethod]
	public void Authenticate_accepts_a_valid_cookie()
	{
		var auth = Create();
		var token = auth.IssueSession();

		var ctx = new DefaultHttpContext();
		ctx.Request.Headers.Cookie = $"{auth.CookieName}={token}";

		var result = auth.Authenticate(ctx.Request);
		Assert.IsTrue(result.Ok);
		Assert.AreEqual(AuthVia.Cookie, result.Via);
	}

	[TestMethod]
	public void Authenticate_rejects_a_stale_or_missing_cookie()
	{
		var auth = Create();
		var ctx = new DefaultHttpContext();
		Assert.IsFalse(auth.Authenticate(ctx.Request).Ok, "no cookie");

		ctx.Request.Headers.Cookie = $"{auth.CookieName}=nope";
		Assert.IsFalse(auth.Authenticate(ctx.Request).Ok, "unknown cookie");
	}

	[TestMethod]
	public void Authenticate_accepts_the_api_token_as_bearer_and_nothing_else()
	{
		var auth = Create(token: "server-to-server-secret");
		var ctx = new DefaultHttpContext();

		ctx.Request.Headers.Authorization = "Bearer server-to-server-secret";
		var ok = auth.Authenticate(ctx.Request);
		Assert.IsTrue(ok.Ok);
		Assert.AreEqual(AuthVia.Bearer, ok.Via);

		ctx.Request.Headers.Authorization = "Bearer wrong";
		Assert.IsFalse(auth.Authenticate(ctx.Request).Ok);

		// A bearer header that fails must not fall through to another method.
		ctx.Request.Headers.Cookie = $"{auth.CookieName}={auth.IssueSession()}";
		Assert.IsFalse(auth.Authenticate(ctx.Request).Ok);
	}

	[TestMethod]
	public void Bearer_is_refused_when_no_api_token_is_configured()
	{
		var auth = Create(token: null);
		var ctx = new DefaultHttpContext();
		ctx.Request.Headers.Authorization = "Bearer anything";
		Assert.IsFalse(auth.Authenticate(ctx.Request).Ok);
	}

	[TestMethod]
	public void Anonymous_mode_lets_everyone_in()
	{
		var auth = Create(password: null, anonymous: true);
		var result = auth.Authenticate(new DefaultHttpContext().Request);
		Assert.IsTrue(result.Ok);
		Assert.AreEqual(AuthVia.Anonymous, result.Via);
	}
}

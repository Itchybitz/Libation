using System;

namespace LibationWeb.Tests;

/// <summary>A clock the test moves by hand, so session expiry and the sign-in throttle can be checked without waiting.</summary>
internal sealed class TestTime : TimeProvider
{
	private DateTimeOffset now;

	public TestTime(DateTimeOffset start) => now = start;

	public override DateTimeOffset GetUtcNow() => now;

	public void Advance(TimeSpan by) => now += by;
}

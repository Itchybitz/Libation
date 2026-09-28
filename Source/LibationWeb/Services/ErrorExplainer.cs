using AudibleUtilities;
using System;
using System.Linq;

namespace LibationWeb.Services;

/// <summary>
/// The same three explanations LibationCli prints, as one sentence for a status field. The raw exception
/// still goes to Libation's log; this is what the person on the phone reads.
/// </summary>
public static class ErrorExplainer
{
	public static string Explain(Exception ex)
	{
		if (NonJsonResponseExceptionExtensions.TryFindInTree(ex, out var htmlEx) && htmlEx is not null)
			return string.Join(" ", htmlEx.GetExplainerLines().Where(l => !string.IsNullOrWhiteSpace(l)));

		if (AccountSettingsDecryptFailure.TryFindInTree(ex, out var decryptEx) && decryptEx is not null)
			return string.Join(" ", AccountSettingsDecryptFailure.GetExplainerLines(ex).Where(l => !string.IsNullOrWhiteSpace(l)));

		if (AuthenticationExceptionHelper.FindAuthenticationRequired(ex)?.AccountInfo is { } accountInfo)
			return $"Audible login is required for {accountInfo.RevealOwnerFacingLabel()}. "
				+ "Sign in again with 'LibationCli login-external --account <email> --locale <locale>' against this Libation files folder (or in the desktop app), then retry.";

		var innermost = ex;
		while (innermost.InnerException is not null)
			innermost = innermost.InnerException;

		return innermost.Message == ex.Message ? ex.Message : $"{ex.Message} ({innermost.Message})";
	}
}

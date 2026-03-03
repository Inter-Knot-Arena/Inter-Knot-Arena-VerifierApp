namespace VerifierApp.Core.Models;

public sealed record VerifierDeviceStartRequest(
    string CodeChallenge,
    string RedirectUri,
    string? State
);

public sealed record VerifierDeviceStartResponse(
    string RequestId,
    string AuthorizeUrl,
    long ExpiresAt
);

public sealed record VerifierDeviceExchangeRequest(
    string RequestId,
    string Code,
    string CodeVerifier
);

public sealed record VerifierTokens(
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    long RefreshExpiresAt
);

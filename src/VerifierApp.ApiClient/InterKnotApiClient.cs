using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VerifierApp.Core.Models;
using VerifierApp.Core.Services;

namespace VerifierApp.ApiClient;

public sealed class InterKnotApiClient : IVerifierApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;

    public InterKnotApiClient(
        HttpClient http,
        Func<CancellationToken, Task<string?>> accessTokenProvider
    )
    {
        _http = http;
        _accessTokenProvider = accessTokenProvider;
    }

    public Task<VerifierDeviceStartResponse> StartDeviceAuthAsync(
        VerifierDeviceStartRequest request,
        CancellationToken ct
    ) => SendAsync<VerifierDeviceStartResponse>(
        "/auth/verifier/device/start",
        new
        {
            codeChallenge = request.CodeChallenge,
            redirectUri = request.RedirectUri,
            state = request.State
        },
        includeBearer: false,
        ct
    );

    public async Task<VerifierTokens> ExchangeDeviceCodeAsync(
        VerifierDeviceExchangeRequest request,
        CancellationToken ct
    )
    {
        var response = await SendAsync<DeviceExchangeResponse>(
            "/auth/verifier/device/exchange",
            new
            {
                requestId = request.RequestId,
                code = request.Code,
                codeVerifier = request.CodeVerifier
            },
            includeBearer: false,
            ct
        );
        return new VerifierTokens(
            response.AccessToken,
            response.RefreshToken,
            response.ExpiresAt,
            response.RefreshExpiresAt
        );
    }

    public async Task<VerifierTokens> RefreshVerifierTokenAsync(string refreshToken, CancellationToken ct)
    {
        var response = await SendAsync<TokenRefreshResponse>(
            "/auth/verifier/token/refresh",
            new { refreshToken },
            includeBearer: false,
            ct
        );
        return new VerifierTokens(
            response.AccessToken,
            response.RefreshToken,
            response.ExpiresAt,
            response.RefreshExpiresAt
        );
    }

    public Task RevokeTokenAsync(string token, CancellationToken ct) =>
        SendAsync<object>(
            "/auth/verifier/token/revoke",
            new { token },
            includeBearer: false,
            ct
        );

    public async Task<RosterImportResult> ImportRosterAsync(RosterScanResult result, CancellationToken ct)
    {
        var response = await SendAsync<RosterImportResponse>(
            "/verifier/roster/import",
            new
            {
                uid = result.Uid,
                region = result.Region,
                fullSync = result.FullSync,
                agents = result.Agents.Select(agent => new
                {
                    agentId = agent.AgentId,
                    owned = agent.Owned,
                    level = agent.Level,
                    mindscape = agent.Mindscape,
                    confidence = agent.ConfidenceByField
                }).ToList()
            },
            includeBearer: true,
            ct
        );
        return new RosterImportResult(response.Status, response.Summary?.Message ?? "OK");
    }

    public async Task<MatchVerifierSession> CreateMatchSessionAsync(string matchId, CancellationToken ct)
    {
        var payload = await SendAsync<MatchSessionResponse>(
            $"/matches/{Uri.EscapeDataString(matchId)}/verifier/session",
            new { },
            includeBearer: true,
            ct
        );
        return new MatchVerifierSession(
            payload.MatchId,
            payload.VerifierSessionToken,
            payload.ExpiresAt,
            payload.Ruleset.PrecheckFrequencySec,
            payload.Ruleset.InrunFrequencySec,
            payload.Ruleset.RequireInrunCheck
        );
    }

    public Task SubmitEvidenceAsync(EvidenceSubmission submission, CancellationToken ct)
    {
        var endpoint = submission.Type.Equals("PRECHECK", StringComparison.OrdinalIgnoreCase)
            ? $"/matches/{Uri.EscapeDataString(submission.MatchId)}/evidence/precheck"
            : $"/matches/{Uri.EscapeDataString(submission.MatchId)}/evidence/inrun";

        return SendAsync<object>(
            endpoint,
            new
            {
                detectedAgents = submission.Detection.DetectedAgents,
                confidence = submission.Detection.Confidence,
                result = submission.Detection.Result,
                frameHash = submission.Detection.FrameHash,
                verifierSessionToken = submission.VerifierSessionToken,
                verifierNonce = submission.VerifierNonce,
                verifierSignature = submission.VerifierSignature
            },
            includeBearer: true,
            ct
        );
    }

    private async Task<T> SendAsync<T>(
        string path,
        object body,
        bool includeBearer,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json"
        );
        if (includeBearer)
        {
            var token = await _accessTokenProvider(ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        using var response = await _http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"API request failed ({(int)response.StatusCode}): {content}"
            );
        }
        return JsonSerializer.Deserialize<T>(content, JsonOptions)
               ?? throw new InvalidOperationException("API response is empty");
    }

    private sealed record DeviceExchangeResponse(
        string AccessToken,
        string RefreshToken,
        long ExpiresAt,
        long RefreshExpiresAt
    );

    private sealed record TokenRefreshResponse(
        string AccessToken,
        string RefreshToken,
        long ExpiresAt,
        long RefreshExpiresAt
    );

    private sealed record RosterImportResponse(
        string Status,
        RosterImportSummary? Summary
    );

    private sealed record RosterImportSummary(string? Message);

    private sealed record MatchSessionResponse(
        string MatchId,
        string VerifierSessionToken,
        long ExpiresAt,
        MatchSessionRuleset Ruleset
    );

    private sealed record MatchSessionRuleset(
        bool RequireInrunCheck,
        int PrecheckFrequencySec,
        int InrunFrequencySec
    );

    public void Dispose()
    {
        _http.Dispose();
    }
}

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
        headers: null,
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
            headers: null,
            includeBearer: false,
            ct
        );
        return new VerifierTokens(
            response.AccessToken,
            response.RefreshToken,
            response.ExpiresAt,
            response.RefreshExpiresAt,
            response.User?.Id
        );
    }

    public async Task<VerifierTokens> RefreshVerifierTokenAsync(
        string refreshToken,
        string? currentUserId,
        CancellationToken ct
    )
    {
        var response = await SendAsync<TokenRefreshResponse>(
            "/auth/verifier/token/refresh",
            new { refreshToken },
            headers: null,
            includeBearer: false,
            ct
        );
        return new VerifierTokens(
            response.AccessToken,
            response.RefreshToken,
            response.ExpiresAt,
            response.RefreshExpiresAt,
            currentUserId
        );
    }

    public Task RevokeTokenAsync(string token, CancellationToken ct) =>
        SendAsync<object>(
            "/auth/verifier/token/revoke",
            new { token },
            headers: null,
            includeBearer: false,
            ct
        );

    public async Task<VerifierAuthUser> GetCurrentUserAsync(CancellationToken ct)
    {
        var payload = await SendGetAsync<VerifierAuthUserPayload>(
            "/auth/me",
            includeBearer: true,
            ct
        );
        return new VerifierAuthUser(
            payload.Id,
            payload.DisplayName,
            payload.Verification?.Status ?? "UNVERIFIED",
            payload.Verification?.Uid,
            payload.Verification?.Region
        );
    }

    public async Task<RosterImportResult> ImportRosterAsync(RosterScanResult result, CancellationToken ct)
    {
        var response = await SendAsync<RosterImportResponse>(
            "/verifier/roster/import",
            new
            {
                uid = result.Uid,
                region = result.Region,
                fullSync = result.FullSync,
                modelVersion = result.ModelVersion,
                dataVersion = result.DataVersion,
                scanMeta = result.ScanMeta,
                timingMs = result.TimingMs,
                locale = result.Locale,
                resolution = result.Resolution,
                lowConfReasons = result.LowConfReasons ?? Array.Empty<string>(),
                confidenceByField = result.ConfidenceByField ?? new Dictionary<string, double>(),
                fieldSources = result.FieldSources ?? new Dictionary<string, string>(),
                capabilities = result.Capabilities ?? new Dictionary<string, bool>(),
                agents = result.Agents.Select(agent => new
                {
                    agentId = agent.AgentId,
                    level = agent.Level,
                    levelCap = agent.LevelCap,
                    mindscape = agent.Mindscape,
                    mindscapeCap = agent.MindscapeCap,
                    stats = agent.Stats ?? new Dictionary<string, double>(),
                    weapon = agent.Weapon is null
                        ? null
                        : new
                        {
                            weaponId = agent.Weapon.WeaponId,
                            displayName = agent.Weapon.DisplayName,
                            level = agent.Weapon.Level,
                            levelCap = agent.Weapon.LevelCap,
                            baseStatKey = agent.Weapon.BaseStatKey,
                            baseStatValue = agent.Weapon.BaseStatValue,
                            advancedStatKey = agent.Weapon.AdvancedStatKey,
                            advancedStatValue = agent.Weapon.AdvancedStatValue
                        },
                    weaponPresent = agent.WeaponPresent,
                    discSlotOccupancy = agent.DiscSlotOccupancy ?? new Dictionary<string, bool>(),
                    discs = (agent.Discs ?? Array.Empty<DiscScanResult>()).Select(disc => new
                    {
                        slot = disc.Slot,
                        setId = disc.SetId,
                        displayName = disc.DisplayName,
                        level = disc.Level,
                        levelCap = disc.LevelCap,
                        mainStatKey = disc.MainStatKey,
                        mainStatValue = disc.MainStatValue,
                        substats = (disc.Substats ?? Array.Empty<DiscSubstatScanResult>()).Select(substat => new
                        {
                            key = substat.Key,
                            value = substat.Value
                        }).ToList()
                    }).ToList(),
                    confidenceByField = agent.ConfidenceByField ?? new Dictionary<string, double>(),
                    fieldSources = agent.FieldSources ?? new Dictionary<string, string>()
                }).ToList()
            },
            headers: null,
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
            headers: null,
            includeBearer: true,
            ct
        );
        return new MatchVerifierSession(
            payload.MatchId,
            payload.VerifierSessionToken,
            payload.ExpiresAt,
            payload.Ruleset.PrecheckFrequencySec,
            payload.Ruleset.InrunFrequencySec,
            payload.Ruleset.RequireInrunCheck,
            payload.ExpectedAgents ?? Array.Empty<string>(),
            payload.BannedAgents ?? Array.Empty<string>()
        );
    }

    public Task SubmitEvidenceAsync(EvidenceSubmission submission, CancellationToken ct)
    {
        var endpoint = submission.Type.Equals("PRECHECK", StringComparison.OrdinalIgnoreCase)
            ? $"/matches/{Uri.EscapeDataString(submission.MatchId)}/evidence/precheck"
            : $"/matches/{Uri.EscapeDataString(submission.MatchId)}/evidence/inrun";
        var idempotencyKey = $"verifier:{submission.MatchId}:{submission.Type}:{submission.VerifierNonce}";

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
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Idempotency-Key"] = idempotencyKey
            },
            includeBearer: true,
            ct
        );
    }

    private async Task<T> SendAsync<T>(
        string path,
        object body,
        IReadOnlyDictionary<string, string>? headers,
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
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
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

    private async Task<T> SendGetAsync<T>(
        string path,
        bool includeBearer,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
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
        long RefreshExpiresAt,
        VerifierAuthUserPayload? User
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
        IReadOnlyList<string>? ExpectedAgents,
        IReadOnlyList<string>? BannedAgents,
        MatchSessionRuleset Ruleset
    );

    private sealed record MatchSessionRuleset(
        bool RequireInrunCheck,
        int PrecheckFrequencySec,
        int InrunFrequencySec
    );

    private sealed record VerifierAuthUserPayload(
        string Id,
        string DisplayName,
        VerificationPayload? Verification
    );

    private sealed record VerificationPayload(
        string Status,
        string? Uid,
        string? Region
    );

    public void Dispose()
    {
        _http.Dispose();
    }
}

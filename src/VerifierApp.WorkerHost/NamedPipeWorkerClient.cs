using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using VerifierApp.Core.Models;
using VerifierApp.Core.Services;

namespace VerifierApp.WorkerHost;

public sealed class NamedPipeWorkerClient : IWorkerClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public NamedPipeWorkerClient(string pipeName = "ika_verifier_worker", int connectTimeoutMs = 15_000)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(connectTimeoutMs);
        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public Task<bool> HealthAsync(CancellationToken ct) =>
        SendAsync<bool>("health", new { }, ct);

    public Task<JsonElement> HealthDetailsAsync(CancellationToken ct) =>
        SendAsync<JsonElement>("health.details", new { }, ct);

    public Task<RosterScanResult> RunRosterScanAsync(RosterScanCommand command, CancellationToken ct) =>
        SendAsync<RosterScanResult>("ocr.scan", command, ct);

    public Task<EquipmentOverviewInspectionResult> InspectEquipmentOverviewAsync(string imagePath, CancellationToken ct) =>
        SendAsync<EquipmentOverviewInspectionResult>("ocr.inspectEquipmentOverview", new { path = imagePath }, ct);

    public Task<DangerSurfaceInspectionResult> InspectDangerSurfaceAsync(string imagePath, CancellationToken ct) =>
        SendAsync<DangerSurfaceInspectionResult>("ocr.inspectDangerSurface", new { path = imagePath }, ct);

    public Task<DetectionResult> RunPrecheckAsync(
        string matchId,
        string? frameHashHint,
        IReadOnlyList<string> expectedAgents,
        IReadOnlyList<string> bannedAgents,
        string locale,
        string resolution,
        CancellationToken ct
    ) =>
        SendAsync<DetectionResult>(
            "cv.precheck",
            new { matchId, frameHashHint, expectedAgents, bannedAgents, locale, resolution },
            ct
        );

    public Task<DetectionResult> RunInrunAsync(
        string matchId,
        string? frameHashHint,
        IReadOnlyList<string> expectedAgents,
        IReadOnlyList<string> bannedAgents,
        string locale,
        string resolution,
        CancellationToken ct
    ) =>
        SendAsync<DetectionResult>(
            "cv.inrun",
            new { matchId, frameHashHint, expectedAgents, bannedAgents, locale, resolution },
            ct
        );

    private async Task<T> SendAsync<T>(string method, object payload, CancellationToken ct)
    {
        var timeoutMs = ResolveRequestTimeoutMs(method);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var requestCt = timeoutCts.Token;

        await _requestGate.WaitAsync(requestCt);
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(
                new WorkerRequestEnvelope(
                    requestId,
                    method,
                    payload
                ),
                JsonOptions
            );
            await _writer.WriteLineAsync(request).WaitAsync(requestCt);

            var raw = await _reader.ReadLineAsync().WaitAsync(requestCt);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException($"Worker returned empty response for {method}");
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Backward-compatible fallback for older non-enveloped worker payloads.
            if (!root.TryGetProperty("id", out _) &&
                !root.TryGetProperty("result", out _) &&
                !root.TryGetProperty("error", out _))
            {
                return JsonSerializer.Deserialize<T>(raw, JsonOptions)
                       ?? throw new InvalidOperationException($"Worker returned invalid payload for {method}");
            }

            var responseId = root.GetProperty("id").GetString();
            if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Worker correlation mismatch. Expected '{requestId}', got '{responseId ?? "<null>"}'."
                );
            }

            if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind != JsonValueKind.Null)
            {
                var code = errorNode.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.String
                    ? codeNode.GetString()
                    : "WORKER_ERROR";
                var message = errorNode.TryGetProperty("message", out var messageNode) && messageNode.ValueKind == JsonValueKind.String
                    ? messageNode.GetString()
                    : "Unknown worker error";
                throw new InvalidOperationException($"[{code}] {message}");
            }

            if (!root.TryGetProperty("result", out var resultNode))
            {
                throw new InvalidOperationException($"Worker response for {method} has no result.");
            }

            return resultNode.Deserialize<T>(JsonOptions)
                   ?? throw new InvalidOperationException($"Worker returned invalid result payload for {method}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Worker request timed out after {timeoutMs} ms for {method}."
            );
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static int ResolveRequestTimeoutMs(string method) =>
        method switch
        {
            "health" => ReadPositiveIntFromEnvironment("IKA_WORKER_HEALTH_TIMEOUT_MS", 30_000),
            "ocr.inspectEquipmentOverview" => ReadPositiveIntFromEnvironment("IKA_WORKER_EQUIPMENT_TIMEOUT_MS", 15_000),
            "ocr.inspectDangerSurface" => ReadPositiveIntFromEnvironment("IKA_WORKER_DANGER_TIMEOUT_MS", 15_000),
            "ocr.scan" => ReadPositiveIntFromEnvironment("IKA_WORKER_SCAN_TIMEOUT_MS", 60_000),
            _ => ReadPositiveIntFromEnvironment("IKA_WORKER_REQUEST_TIMEOUT_MS", 30_000),
        };

    private static int ReadPositiveIntFromEnvironment(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(raw) &&
               int.TryParse(raw, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    public ValueTask DisposeAsync()
    {
        _requestGate.Dispose();
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record WorkerRequestEnvelope(
        string Id,
        string Method,
        object Payload
    );
}

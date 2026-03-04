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

    public Task<RosterScanResult> RunRosterScanAsync(RosterScanCommand command, CancellationToken ct) =>
        SendAsync<RosterScanResult>("ocr.scan", command, ct);

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
        await _requestGate.WaitAsync(ct);
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
            await _writer.WriteLineAsync(request);

            var raw = await _reader.ReadLineAsync().WaitAsync(ct);
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
        finally
        {
            _requestGate.Release();
        }
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

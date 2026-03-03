using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VerifierApp.Core.Models;
using VerifierApp.Core.Services;

namespace VerifierApp.WorkerHost;

public sealed class NamedPipeWorkerClient : IWorkerClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public NamedPipeWorkerClient(string pipeName = "ika_verifier_worker")
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(15_000);
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

    public Task<DetectionResult> RunPrecheckAsync(string matchId, CancellationToken ct) =>
        SendAsync<DetectionResult>("cv.precheck", new { matchId }, ct);

    public Task<DetectionResult> RunInrunAsync(string matchId, CancellationToken ct) =>
        SendAsync<DetectionResult>("cv.inrun", new { matchId }, ct);

    private async Task<T> SendAsync<T>(string method, object payload, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new { method, payload }, JsonOptions);
        await _writer.WriteLineAsync(request);

        using var registration = ct.Register(() =>
        {
            try
            {
                _pipe.Close();
            }
            catch
            {
                // ignored
            }
        });
        var raw = await _reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"Worker returned empty response for {method}");
        }
        return JsonSerializer.Deserialize<T>(raw, JsonOptions)
               ?? throw new InvalidOperationException($"Worker returned invalid payload for {method}");
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }
}

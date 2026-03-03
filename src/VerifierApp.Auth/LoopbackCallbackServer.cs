using System.Net;
using System.Text;

namespace VerifierApp.Auth;

public sealed record LoopbackCallbackPayload(string RequestId, string Code, string? State);

public sealed class LoopbackCallbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;

    public LoopbackCallbackServer(int port)
    {
        _prefix = $"http://127.0.0.1:{port}/callback/";
        _listener.Prefixes.Add(_prefix);
    }

    public string RedirectUri => _prefix;

    public void Start() => _listener.Start();

    public async Task<LoopbackCallbackPayload> WaitForCallbackAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // ignored
            }
        });

        var context = await _listener.GetContextAsync();
        var requestId = context.Request.QueryString["requestId"] ?? string.Empty;
        var code = context.Request.QueryString["code"] ?? string.Empty;
        var state = context.Request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(code))
        {
            context.Response.StatusCode = 400;
            await WriteBodyAsync(context.Response, "Verifier callback missing code/requestId.");
            throw new InvalidOperationException("Verifier callback is incomplete");
        }

        context.Response.StatusCode = 200;
        await WriteBodyAsync(context.Response, "Verifier login completed. You can close this window.");
        return new LoopbackCallbackPayload(requestId, code, state);
    }

    private static async Task WriteBodyAsync(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    public ValueTask DisposeAsync()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}

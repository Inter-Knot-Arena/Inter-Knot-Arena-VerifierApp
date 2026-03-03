using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VerifierApp.ApiClient;
using VerifierApp.Auth;
using VerifierApp.Core.Models;
using VerifierApp.Core.Services;
using VerifierApp.WorkerHost;

namespace VerifierApp.UI;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DpapiTokenStore _tokenStore = new();
    private readonly WorkerProcessLauncher _workerLauncher = new();
    private NamedPipeWorkerClient? _workerClient;
    private CancellationTokenSource? _monitorCts;
    private BundledAssetPaths? _bundledAssets;
    private bool _isAuthenticated;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
        SetAuthenticatedState(false, "Sign in required");
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RestoreExistingSessionAsync();
    }

    private async Task RestoreExistingSessionAsync()
    {
        var tokens = await _tokenStore.ReadAsync(CancellationToken.None);
        if (tokens is null)
        {
            AppendStatus("No stored verifier token found.");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (tokens.ExpiresAt > now + 5_000)
        {
            SetAuthenticatedState(true, "Session restored from local store");
            AppendStatus("Existing verifier session restored.");
            return;
        }

        if (tokens.RefreshExpiresAt <= now)
        {
            await _tokenStore.ClearAsync(CancellationToken.None);
            SetAuthenticatedState(false, "Session expired. Please sign in again.");
            AppendStatus("Stored session expired and was cleared.");
            return;
        }

        try
        {
            using var api = BuildApiClient();
            var refreshed = await api.RefreshVerifierTokenAsync(tokens.RefreshToken, CancellationToken.None);
            await _tokenStore.SaveAsync(refreshed, CancellationToken.None);
            SetAuthenticatedState(true, "Session refreshed");
            AppendStatus("Verifier token refreshed successfully.");
        }
        catch (Exception ex)
        {
            await _tokenStore.ClearAsync(CancellationToken.None);
            SetAuthenticatedState(false, "Sign in required");
            AppendStatus($"Failed to refresh stored token: {ex.Message}");
        }
    }

    private async void EmailLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Starting email sign-in...", async ct =>
        {
            var email = EmailTextBox.Text.Trim();
            var password = PasswordInput.Password;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Email and password are required.");
            }

            var tokens = await LoginWithEmailFlowAsync(email, password, ct);
            await _tokenStore.SaveAsync(tokens, ct);
            SetAuthenticatedState(true, "Signed in with email");
            PasswordInput.Clear();
            AppendStatus("Email login completed. Verifier features unlocked.");
        });
    }

    private async void GoogleLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Starting Google OAuth login...", async ct =>
        {
            var tokens = await LoginWithGoogleFlowAsync(ct);
            await _tokenStore.SaveAsync(tokens, ct);
            SetAuthenticatedState(true, "Signed in with Google");
            AppendStatus("Google login completed. Verifier features unlocked.");
        });
    }

    private async void RosterScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isAuthenticated)
        {
            AppendStatus("Sign in first to run roster scan.");
            return;
        }

        await RunUiActionAsync("Running full roster scan...", async ct =>
        {
            await EnsureWorkerAsync();
            var worker = _workerClient ?? throw new InvalidOperationException("Worker is not ready");
            using var api = BuildApiClient();
            var orchestrator = new ScanOrchestrator(api, worker, new NativeBridge());
            var region = GetRegion();
            var fullSync = FullSyncCheckBox.IsChecked == true;
            var result = await orchestrator.ExecuteRosterScanAsync(region, fullSync, ct);
            AppendStatus($"Roster import status: {result.Status}. {result.Message}");
        });
    }

    private async void MatchMonitorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isAuthenticated)
        {
            AppendStatus("Sign in first to run match monitor.");
            return;
        }

        if (_monitorCts is not null)
        {
            _monitorCts.Cancel();
            _monitorCts.Dispose();
            _monitorCts = null;
            MatchMonitorButton.Content = "Start Match Monitor";
            AppendStatus("Match monitor stopped.");
            return;
        }

        await RunUiActionAsync("Starting match monitor...", async ct =>
        {
            await EnsureWorkerAsync();
            var worker = _workerClient ?? throw new InvalidOperationException("Worker is not ready");
            var matchId = MatchIdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(matchId))
            {
                throw new InvalidOperationException("Match ID is required.");
            }

            _monitorCts = new CancellationTokenSource();
            var monitorToken = _monitorCts.Token;
            MatchMonitorButton.Content = "Stop Match Monitor";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var api = BuildApiClient();
                    var monitor = new MatchMonitorService(api, worker);
                    await monitor.RunMatchAsync(matchId, monitorToken);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MatchMonitorButton.Content = "Start Match Monitor";
                        AppendStatus("Match monitor completed.");
                    });
                }
                catch (OperationCanceledException)
                {
                    // expected on stop
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => AppendStatus($"Monitor error: {ex.Message}"));
                }
            }, monitorToken);
        });
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Logging out...", async ct =>
        {
            var tokens = await _tokenStore.ReadAsync(ct);
            if (tokens is not null)
            {
                try
                {
                    using var api = BuildApiClient();
                    await api.RevokeTokenAsync(tokens.AccessToken, ct);
                    await api.RevokeTokenAsync(tokens.RefreshToken, ct);
                }
                catch (Exception ex)
                {
                    AppendStatus($"Token revoke warning: {ex.Message}");
                }
            }

            if (_monitorCts is not null)
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
                _monitorCts = null;
                MatchMonitorButton.Content = "Start Match Monitor";
            }

            await _tokenStore.ClearAsync(ct);
            SetAuthenticatedState(false, "Signed out");
            AppendStatus("Session cleared.");
        });
    }

    private async void PasswordInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && EmailLoginButton.IsEnabled)
        {
            EmailLoginButton_OnClick(sender, e);
        }
        await Task.CompletedTask;
    }

    private async Task<VerifierTokens> LoginWithGoogleFlowAsync(CancellationToken ct)
    {
        var codeVerifier = PkceService.CreateCodeVerifier();
        var codeChallenge = PkceService.CreateCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");
        var port = FindFreePort();

        await using var callbackServer = new LoopbackCallbackServer(port);
        callbackServer.Start();

        using var api = BuildApiClient();
        var start = await api.StartDeviceAuthAsync(
            new VerifierDeviceStartRequest(
                CodeChallenge: codeChallenge,
                RedirectUri: callbackServer.RedirectUri,
                State: state
            ),
            ct
        );

        Process.Start(new ProcessStartInfo
        {
            FileName = start.AuthorizeUrl,
            UseShellExecute = true
        });

        var callback = await callbackServer.WaitForCallbackAsync(ct);
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state mismatch");
        }

        return await api.ExchangeDeviceCodeAsync(
            new VerifierDeviceExchangeRequest(
                callback.RequestId,
                callback.Code,
                codeVerifier
            ),
            ct
        );
    }

    private async Task<VerifierTokens> LoginWithEmailFlowAsync(
        string email,
        string password,
        CancellationToken ct
    )
    {
        var baseUri = ParseApiBaseUri();
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false
        };

        using var sessionHttp = new HttpClient(handler)
        {
            BaseAddress = baseUri
        };

        using (var login = await PostJsonAsync(sessionHttp, "/auth/login", new { email, password }, ct))
        {
            await EnsureSuccessAsync(login, "Email login failed", ct);
        }

        var codeVerifier = PkceService.CreateCodeVerifier();
        var codeChallenge = PkceService.CreateCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");
        var redirectUri = $"http://127.0.0.1:{FindFreePort()}/callback";

        VerifierDeviceStartResponse start;
        using (var startResponse = await PostJsonAsync(
                   sessionHttp,
                   "/auth/verifier/device/start",
                   new
                   {
                       codeChallenge,
                       redirectUri,
                       state
                   },
                   ct
               ))
        {
            await EnsureSuccessAsync(startResponse, "Device auth start failed", ct);
            start = await DeserializeJsonAsync<VerifierDeviceStartResponse>(startResponse, ct);
        }

        using var bridgeRequest = new HttpRequestMessage(HttpMethod.Get, start.AuthorizeUrl);
        using var bridge = await sessionHttp.SendAsync(bridgeRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)bridge.StatusCode < 300 || (int)bridge.StatusCode > 399)
        {
            var details = await ReadErrorAsync(bridge, ct);
            throw new InvalidOperationException($"Device bridge failed: {details}");
        }

        var location = bridge.Headers.Location
                       ?? throw new InvalidOperationException("Device bridge did not return redirect location.");
        var callbackUri = location.IsAbsoluteUri ? location : new Uri(baseUri, location);

        var callbackState = ReadQueryValue(callbackUri, "state");
        var callbackCode = ReadQueryValue(callbackUri, "code")
                           ?? throw new InvalidOperationException("Device bridge callback code is missing.");
        var callbackRequestId = ReadQueryValue(callbackUri, "requestId")
                                ?? throw new InvalidOperationException("Device bridge requestId is missing.");

        if (!string.Equals(callbackState, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Email login state mismatch.");
        }

        using var api = BuildApiClient();
        return await api.ExchangeDeviceCodeAsync(
            new VerifierDeviceExchangeRequest(callbackRequestId, callbackCode, codeVerifier),
            ct
        );
    }

    private static string? ReadQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 0)
            {
                continue;
            }

            var currentKey = Uri.UnescapeDataString(pair[0]);
            if (!string.Equals(currentKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }

        return null;
    }

    private async Task EnsureWorkerAsync()
    {
        if (_workerClient is not null)
        {
            return;
        }

        _bundledAssets ??= BundledAssetManager.EnsureExtracted(Assembly.GetExecutingAssembly());
        NativeLibraryBootstrap.Initialize(_bundledAssets.NativeDllPath);

        _workerLauncher.Start(_bundledAssets.WorkerExePath);
        await Task.Delay(1200);
        _workerClient = new NamedPipeWorkerClient();
        var healthy = await _workerClient.HealthAsync(CancellationToken.None);
        if (!healthy)
        {
            throw new InvalidOperationException("Worker health check failed.");
        }
    }

    private string GetRegion()
    {
        if (RegionComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
        {
            return value;
        }
        return "OTHER";
    }

    private Uri ParseApiBaseUri()
    {
        var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("API URL is invalid.");
        }
        return baseUri;
    }

    private InterKnotApiClient BuildApiClient()
    {
        var http = new HttpClient
        {
            BaseAddress = ParseApiBaseUri()
        };
        return new InterKnotApiClient(http, async ct =>
        {
            var tokens = await _tokenStore.ReadAsync(ct);
            return tokens?.AccessToken;
        });
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient http,
        string url,
        object payload,
        CancellationToken ct
    )
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.PostAsync(url, content, ct);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken ct
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await ReadErrorAsync(response, ct);
        throw new InvalidOperationException($"{context}: {details}");
    }

    private static async Task<T> DeserializeJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException("API response is empty.");
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return $"HTTP {(int)response.StatusCode}";
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString() ?? $"HTTP {(int)response.StatusCode}";
            }
        }
        catch
        {
            // ignored
        }

        return payload;
    }

    private async Task RunUiActionAsync(string startMessage, Func<CancellationToken, Task> action)
    {
        AppendStatus(startMessage);
        SetBusyState(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await action(cts.Token);
        }
        catch (Exception ex)
        {
            AppendStatus($"Error: {ex.Message}");
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool busy)
    {
        ApiUrlTextBox.IsEnabled = !busy;
        EmailTextBox.IsEnabled = !_isAuthenticated && !busy;
        PasswordInput.IsEnabled = !_isAuthenticated && !busy;
        EmailLoginButton.IsEnabled = !_isAuthenticated && !busy;
        GoogleLoginButton.IsEnabled = !_isAuthenticated && !busy;

        RosterScanButton.IsEnabled = _isAuthenticated && !busy;
        MatchMonitorButton.IsEnabled = _isAuthenticated && !busy;
        LogoutButton.IsEnabled = _isAuthenticated && !busy;
    }

    private void SetAuthenticatedState(bool authenticated, string message)
    {
        _isAuthenticated = authenticated;
        SessionBadgeText.Text = authenticated ? "Connected" : "Not connected";
        SessionDot.Fill = authenticated
            ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
            : new SolidColorBrush(Color.FromRgb(244, 63, 94));

        DisplayNameText.Text = message;
        LoginPanel.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        DashboardPanel.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        SetBusyState(false);
    }

    private void AppendStatus(string message)
    {
        StatusTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        StatusTextBox.ScrollToEnd();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    protected override async void OnClosed(EventArgs e)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        if (_workerClient is not null)
        {
            await _workerClient.DisposeAsync();
            _workerClient = null;
        }
        _workerLauncher.Dispose();
        base.OnClosed(e);
    }
}

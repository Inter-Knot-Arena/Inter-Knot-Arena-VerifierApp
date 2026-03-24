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
    private enum AppUiState
    {
        Unauthenticated,
        Authenticating,
        Ready,
        ScanRunning,
        MonitorRunning,
        Error
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DpapiTokenStore _tokenStore = new();
    private readonly WorkerProcessLauncher _workerLauncher = new();
    private NamedPipeWorkerClient? _workerClient;
    private CancellationTokenSource? _monitorCts;
    private BundledAssetPaths? _bundledAssets;
    private readonly SemaphoreSlim _tokenRefreshGate = new(1, 1);
    private bool _isAuthenticated;
    private bool _isBusy;
    private bool _authInProgress;
    private bool _scanRunning;
    private bool _monitorRunning;
    private bool _lastActionFailed;
    private AppUiState _uiState = AppUiState.Unauthenticated;

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
            AppendStatus("AUTH_RESTORE_MISS", "No stored verifier token found.");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (tokens.ExpiresAt > now + 5_000)
        {
            if (string.IsNullOrWhiteSpace(tokens.UserId))
            {
                tokens = await ResolveUserIdAsync(tokens, CancellationToken.None);
                await _tokenStore.SaveAsync(tokens, CancellationToken.None);
            }
            SetAuthenticatedState(true, "Session restored from local store");
            AppendStatus("AUTH_RESTORE_OK", "Existing verifier session restored.");
            return;
        }

        if (tokens.RefreshExpiresAt <= now)
        {
            await _tokenStore.ClearAsync(CancellationToken.None);
            SetAuthenticatedState(false, "Session expired. Please sign in again.");
            AppendStatus("AUTH_RESTORE_EXPIRED", "Stored session expired and was cleared.");
            return;
        }

        try
        {
            using var api = BuildApiClient();
            var refreshed = await api.RefreshVerifierTokenAsync(
                tokens.RefreshToken,
                tokens.UserId,
                CancellationToken.None
            );
            if (string.IsNullOrWhiteSpace(refreshed.UserId))
            {
                refreshed = await ResolveUserIdAsync(refreshed, CancellationToken.None);
            }
            await _tokenStore.SaveAsync(refreshed, CancellationToken.None);
            SetAuthenticatedState(true, "Session refreshed");
            AppendStatus("AUTH_REFRESH_OK", "Verifier token refreshed successfully.");
        }
        catch (Exception ex)
        {
            await _tokenStore.ClearAsync(CancellationToken.None);
            SetAuthenticatedState(false, "Sign in required");
            AppendStatus("AUTH_REFRESH_FAIL", $"Failed to refresh stored token: {ex.Message}");
        }
    }

    private async void EmailLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        _authInProgress = true;
        RecomputeState();
        try
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
                AppendStatus("AUTH_EMAIL_OK", "Email login completed. Verifier features unlocked.");
            });
        }
        finally
        {
            _authInProgress = false;
            RecomputeState();
        }
    }

    private async void GoogleLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        _authInProgress = true;
        RecomputeState();
        try
        {
            await RunUiActionAsync("Starting Google OAuth login...", async ct =>
            {
                var tokens = await LoginWithGoogleFlowAsync(ct);
                await _tokenStore.SaveAsync(tokens, ct);
                SetAuthenticatedState(true, "Signed in with Google");
                AppendStatus("AUTH_GOOGLE_OK", "Google login completed. Verifier features unlocked.");
            });
        }
        finally
        {
            _authInProgress = false;
            RecomputeState();
        }
    }

    private async void RosterScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isAuthenticated)
        {
            AppendStatus("SCAN_AUTH_REQUIRED", "Sign in first to run roster scan.");
            return;
        }

        if (_monitorRunning)
        {
            AppendStatus("SCAN_BLOCKED_MONITOR", "Stop match monitor before running roster scan.");
            return;
        }

        if (TryRelaunchElevatedForGame(out var elevationError))
        {
            Application.Current.Shutdown();
            return;
        }
        if (!string.IsNullOrWhiteSpace(elevationError))
        {
            AppendStatus("SCAN_ELEVATION_REQUIRED", elevationError);
            return;
        }

        _scanRunning = true;
        RecomputeState();
        try
        {
            await RunUiActionAsync("Running visible roster scan...", async ct =>
            {
                await EnsureWorkerAsync(ct);
                var worker = _workerClient ?? throw new InvalidOperationException("Worker is not ready");
                using var api = BuildApiClient();
                var orchestrator = new ScanOrchestrator(api, worker, new NativeBridge());
                var region = GetRegion();
                var fullSync = FullSyncCheckBox.IsChecked == true;
                var locale = GetLocale();
                var resolution = GetResolution();
                var result = await orchestrator.ExecuteRosterScanAsync(region, fullSync, locale, resolution, ct);
                AppendStatus("SCAN_IMPORT_RESULT", $"Roster import status: {result.Status}. {result.Message}");
            });
        }
        finally
        {
            _scanRunning = false;
            RecomputeState();
        }
    }

    private async void MatchMonitorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isAuthenticated)
        {
            AppendStatus("MONITOR_AUTH_REQUIRED", "Sign in first to run match monitor.");
            return;
        }

        if (_scanRunning)
        {
            AppendStatus("MONITOR_BLOCKED_SCAN", "Wait for roster scan to finish before starting monitor.");
            return;
        }

        if (_monitorCts is not null)
        {
            _monitorCts.Cancel();
            _monitorCts.Dispose();
            _monitorCts = null;
            _monitorRunning = false;
            RecomputeState();
            MatchMonitorButton.Content = "Start Match Monitor";
            AppendStatus("MONITOR_STOPPED", "Match monitor stopped.");
            return;
        }

        if (TryRelaunchElevatedForGame(out var monitorElevationError))
        {
            Application.Current.Shutdown();
            return;
        }
        if (!string.IsNullOrWhiteSpace(monitorElevationError))
        {
            AppendStatus("MONITOR_ELEVATION_REQUIRED", monitorElevationError);
            return;
        }

        await RunUiActionAsync("Starting match monitor...", async ct =>
        {
            await EnsureWorkerAsync(ct);
            var worker = _workerClient ?? throw new InvalidOperationException("Worker is not ready");
            var matchId = MatchIdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(matchId))
            {
                throw new InvalidOperationException("Match ID is required.");
            }
            var tokens = await _tokenStore.ReadAsync(ct)
                         ?? throw new InvalidOperationException("Verifier session is missing. Sign in again.");
            if (string.IsNullOrWhiteSpace(tokens.UserId))
            {
                throw new InvalidOperationException("User id is missing in verifier token. Sign in again.");
            }
            var locale = GetLocale();
            var resolution = GetResolution();
            var apiBaseUri = ParseApiBaseUri();

            _monitorCts = new CancellationTokenSource();
            var monitorToken = _monitorCts.Token;
            _monitorRunning = true;
            RecomputeState();
            MatchMonitorButton.Content = "Stop Match Monitor";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var api = BuildApiClient(apiBaseUri);
                    var monitor = new MatchMonitorService(api, worker, new NativeBridge());
                    await monitor.RunMatchAsync(
                        matchId,
                        tokens.UserId,
                        locale,
                        resolution,
                        (phase, detection) =>
                        {
                            var marker = detection.Result switch
                            {
                                "VIOLATION" => "VIOLATION",
                                "LOW_CONF" => "LOW_CONF",
                                _ => "PASS"
                            };
                            Dispatcher.Invoke(() =>
                            {
                                AppendStatus(
                                    $"MONITOR_{marker}",
                                    $"{phase}: {marker} | frameHash={detection.FrameHash}"
                                );
                            });
                        },
                        monitorToken
                    );
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _monitorRunning = false;
                        RecomputeState();
                        MatchMonitorButton.Content = "Start Match Monitor";
                        AppendStatus("MONITOR_COMPLETED", "Match monitor completed.");
                    });
                }
                catch (OperationCanceledException)
                {
                    // expected on stop
                }
                catch (Exception ex)
                {
                    await ResetWorkerClientAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _monitorRunning = false;
                        _lastActionFailed = true;
                        RecomputeState();
                        AppendStatus("MONITOR_ERROR", $"Monitor error: {ex.Message}");
                    });
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
                    AppendStatus("AUTH_REVOKE_WARN", $"Token revoke warning: {ex.Message}");
                }
            }

            if (_monitorCts is not null)
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
                _monitorCts = null;
                _monitorRunning = false;
                RecomputeState();
                MatchMonitorButton.Content = "Start Match Monitor";
            }

            await _tokenStore.ClearAsync(ct);
            SetAuthenticatedState(false, "Signed out");
            AppendStatus("AUTH_LOGOUT_OK", "Session cleared.");
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

        var tokens = await api.ExchangeDeviceCodeAsync(
            new VerifierDeviceExchangeRequest(
                callback.RequestId,
                callback.Code,
                codeVerifier
            ),
            ct
        );
        return await ResolveUserIdAsync(tokens, ct);
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
        var tokens = await api.ExchangeDeviceCodeAsync(
            new VerifierDeviceExchangeRequest(callbackRequestId, callbackCode, codeVerifier),
            ct
        );
        return await ResolveUserIdAsync(tokens, ct);
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

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        if (_workerClient is not null)
        {
            try
            {
                if (await _workerClient.HealthAsync(ct))
                {
                    return;
                }
            }
            catch
            {
                // stale client or dead worker process; restart below
            }

            await _workerClient.DisposeAsync();
            _workerClient = null;
            _workerLauncher.Dispose();
        }

        _bundledAssets ??= BundledAssetManager.EnsureExtracted(Assembly.GetExecutingAssembly());
        NativeLibraryBootstrap.Initialize(_bundledAssets.NativeDllPath);

        _workerLauncher.Start(
            _bundledAssets.WorkerExePath,
            pathPrependDirectory: _bundledAssets.CudaRoot,
            bundleRoot: _bundledAssets.RootPath,
            ocrRoot: _bundledAssets.OcrScanRoot,
            cvRoot: _bundledAssets.CvRoot
        );
        Exception? lastError = null;
        var startedAt = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(15))
        {
            ct.ThrowIfCancellationRequested();
            NamedPipeWorkerClient? candidate = null;
            try
            {
                candidate = new NamedPipeWorkerClient(connectTimeoutMs: 1200);
                var healthy = await candidate.HealthAsync(ct);
                if (!healthy)
                {
                    throw new InvalidOperationException("Worker health endpoint returned false.");
                }

                _workerClient = candidate;
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (candidate is not null)
                {
                    await candidate.DisposeAsync();
                }
            }

            await Task.Delay(250, ct);
        }

        throw new InvalidOperationException("Worker startup failed after retries.", lastError);
    }

    private async Task ResetWorkerClientAsync()
    {
        var client = _workerClient;
        _workerClient = null;
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // ignored
            }
        }
        _workerLauncher.Dispose();
    }

    private bool TryRelaunchElevatedForGame(out string? errorMessage)
    {
        var executablePath = Environment.ProcessPath ??
                             Process.GetCurrentProcess().MainModule?.FileName ??
                             string.Empty;
        var outcome = ElevationRelaunchHelper.TryRelaunchIfGameRequiresElevation(
            new NativeBridge(),
            executablePath,
            Environment.GetCommandLineArgs().Skip(1),
            out errorMessage,
            out _,
            waitForExit: false
        );
        return outcome == ElevationRelaunchOutcome.Relaunched;
    }

    private async Task<VerifierTokens> ResolveUserIdAsync(VerifierTokens tokens, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tokens.UserId))
        {
            return tokens;
        }

        using var api = new InterKnotApiClient(
            new HttpClient
            {
                BaseAddress = ParseApiBaseUri()
            },
            _ => Task.FromResult<string?>(tokens.AccessToken)
        );
        var user = await api.GetCurrentUserAsync(ct);
        return tokens with
        {
            UserId = user.Id
        };
    }

    private string GetRegion()
    {
        if (RegionComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
        {
            return value;
        }
        return "OTHER";
    }

    private string GetLocale()
    {
        if (LocaleComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
        {
            return value;
        }
        return "EN";
    }

    private string GetResolution()
    {
        if (ResolutionComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
        {
            return value.Trim().ToLowerInvariant();
        }
        return "auto";
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

    private InterKnotApiClient BuildApiClient(Uri? baseUriOverride = null)
    {
        var http = new HttpClient
        {
            BaseAddress = baseUriOverride ?? ParseApiBaseUri()
        };
        return new InterKnotApiClient(http, GetAccessTokenForRequestAsync);
    }

    private async Task<string?> GetAccessTokenForRequestAsync(CancellationToken ct)
    {
        var tokens = await _tokenStore.ReadAsync(ct);
        if (tokens is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (tokens.ExpiresAt > now + 30_000)
        {
            return tokens.AccessToken;
        }

        await _tokenRefreshGate.WaitAsync(ct);
        try
        {
            tokens = await _tokenStore.ReadAsync(ct);
            if (tokens is null)
            {
                return null;
            }

            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (tokens.ExpiresAt > now + 30_000)
            {
                return tokens.AccessToken;
            }

            if (tokens.RefreshExpiresAt <= now)
            {
                await _tokenStore.ClearAsync(ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAuthenticatedState(false, "Session expired. Please sign in again.");
                    AppendStatus("AUTH_REFRESH_EXPIRED", "Verifier refresh token expired.");
                });
                return null;
            }

            var refreshHttp = new HttpClient
            {
                BaseAddress = await Dispatcher.InvokeAsync(ParseApiBaseUri)
            };
            using var refreshApi = new InterKnotApiClient(refreshHttp, _ => Task.FromResult<string?>(null));
            var refreshed = await refreshApi.RefreshVerifierTokenAsync(tokens.RefreshToken, tokens.UserId, ct);
            if (string.IsNullOrWhiteSpace(refreshed.UserId))
            {
                refreshed = await ResolveUserIdAsync(refreshed, ct);
            }

            await _tokenStore.SaveAsync(refreshed, ct);
            await Dispatcher.InvokeAsync(() => AppendStatus("AUTH_REFRESH_OK", "Access token auto-refreshed."));
            return refreshed.AccessToken;
        }
        finally
        {
            _tokenRefreshGate.Release();
        }
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
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        AppendStatus("OP_START", startMessage, correlationId);
        SetBusyState(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await action(cts.Token);
            _lastActionFailed = false;
            AppendStatus("OP_OK", "Operation completed.", correlationId);
        }
        catch (Exception ex)
        {
            _lastActionFailed = true;
            AppendStatus("OP_ERROR", ex.Message, correlationId);
        }
        finally
        {
            SetBusyState(false);
            RecomputeState();
        }
    }

    private void SetBusyState(bool busy)
    {
        _isBusy = busy;
        ApplyControlState();
    }

    private void SetAuthenticatedState(bool authenticated, string message)
    {
        _isAuthenticated = authenticated;
        DisplayNameText.Text = message;
        LoginPanel.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        DashboardPanel.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        RecomputeState();
    }

    private void RecomputeState()
    {
        _uiState = _authInProgress switch
        {
            true => AppUiState.Authenticating,
            false when !_isAuthenticated => AppUiState.Unauthenticated,
            false when _scanRunning => AppUiState.ScanRunning,
            false when _monitorRunning => AppUiState.MonitorRunning,
            false when _lastActionFailed => AppUiState.Error,
            _ => AppUiState.Ready
        };

        SessionBadgeText.Text = _uiState switch
        {
            AppUiState.Authenticating => "Authenticating",
            AppUiState.ScanRunning => "Scan running",
            AppUiState.MonitorRunning => "Monitor running",
            AppUiState.Error => "Error",
            AppUiState.Ready => "Connected",
            _ => "Not connected"
        };

        SessionDot.Fill = _uiState switch
        {
            AppUiState.Authenticating => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
            AppUiState.ScanRunning => new SolidColorBrush(Color.FromRgb(14, 165, 233)),
            AppUiState.MonitorRunning => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            AppUiState.Error => new SolidColorBrush(Color.FromRgb(244, 63, 94)),
            AppUiState.Ready => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            _ => new SolidColorBrush(Color.FromRgb(244, 63, 94))
        };

        ApplyControlState();
    }

    private void ApplyControlState()
    {
        var authInputEnabled = !_isAuthenticated && !_isBusy && !_scanRunning && !_monitorRunning;
        ApiUrlTextBox.IsEnabled = !_isBusy && !_scanRunning && !_monitorRunning;
        EmailTextBox.IsEnabled = authInputEnabled;
        PasswordInput.IsEnabled = authInputEnabled;
        EmailLoginButton.IsEnabled = authInputEnabled;
        GoogleLoginButton.IsEnabled = authInputEnabled;

        RosterScanButton.IsEnabled = _isAuthenticated && !_isBusy && !_monitorRunning && !_scanRunning;
        MatchMonitorButton.IsEnabled = _isAuthenticated && !_isBusy && !_scanRunning;
        LocaleComboBox.IsEnabled = _isAuthenticated && !_isBusy && !_scanRunning && !_monitorRunning;
        ResolutionComboBox.IsEnabled = _isAuthenticated && !_isBusy && !_scanRunning && !_monitorRunning;
        LogoutButton.IsEnabled = _isAuthenticated && !_isBusy && !_scanRunning;
    }

    private void AppendStatus(string message)
    {
        AppendStatus("INFO", message, null);
    }

    private void AppendStatus(string code, string message, string? correlationId = null)
    {
        var correlation = string.IsNullOrWhiteSpace(correlationId) ? "-" : correlationId;
        StatusTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{code}] [{correlation}] {message}{Environment.NewLine}");
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
        _monitorRunning = false;
        if (_workerClient is not null)
        {
            await _workerClient.DisposeAsync();
            _workerClient = null;
        }
        _workerLauncher.Dispose();
        _tokenRefreshGate.Dispose();
        base.OnClosed(e);
    }
}

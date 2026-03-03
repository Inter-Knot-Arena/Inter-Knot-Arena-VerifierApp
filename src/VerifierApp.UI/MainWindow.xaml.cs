using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using VerifierApp.ApiClient;
using VerifierApp.Auth;
using VerifierApp.Core.Services;
using VerifierApp.WorkerHost;

namespace VerifierApp.UI;

public partial class MainWindow : Window
{
    private readonly DpapiTokenStore _tokenStore = new();
    private readonly WorkerProcessLauncher _workerLauncher = new();
    private NamedPipeWorkerClient? _workerClient;
    private CancellationTokenSource? _monitorCts;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void LoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Starting OAuth loopback login...", async ct =>
        {
            var codeVerifier = PkceService.CreateCodeVerifier();
            var codeChallenge = PkceService.CreateCodeChallenge(codeVerifier);
            var state = Guid.NewGuid().ToString("N");
            var port = FindFreePort();

            await using var callbackServer = new LoopbackCallbackServer(port);
            callbackServer.Start();

            using var api = BuildApiClient();
            var start = await api.StartDeviceAuthAsync(
                new VerifierApp.Core.Models.VerifierDeviceStartRequest(
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
                new VerifierApp.Core.Models.VerifierDeviceExchangeRequest(
                    callback.RequestId,
                    callback.Code,
                    codeVerifier
                ),
                ct
            );
            await _tokenStore.SaveAsync(tokens, ct);
            AppendStatus("Login completed and verifier tokens stored.");
        });
    }

    private async void RosterScanButton_OnClick(object sender, RoutedEventArgs e)
    {
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
        if (_monitorCts is not null)
        {
            _monitorCts.Cancel();
            _monitorCts.Dispose();
            _monitorCts = null;
            MatchMonitorButton.Content = "Start Match Monitor";
            AppendStatus("Match monitor stopped.");
            return;
        }

        await RunUiActionAsync("Starting match monitor...", async _ =>
        {
            await EnsureWorkerAsync();
            var worker = _workerClient ?? throw new InvalidOperationException("Worker is not ready");
            var matchId = MatchIdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(matchId))
            {
                throw new InvalidOperationException("Match ID is required.");
            }

            _monitorCts = new CancellationTokenSource();
            MatchMonitorButton.Content = "Stop Match Monitor";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var api = BuildApiClient();
                    var monitor = new MatchMonitorService(api, worker);
                    await monitor.RunMatchAsync(matchId, _monitorCts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AppendStatus("Match monitor completed.");
                        MatchMonitorButton.Content = "Start Match Monitor";
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
            }, _monitorCts.Token);
        });
    }

    private async Task EnsureWorkerAsync()
    {
        if (_workerClient is not null)
        {
            return;
        }

        var workerExe = Path.Combine(AppContext.BaseDirectory, "VerifierWorker.exe");
        if (!File.Exists(workerExe))
        {
            throw new FileNotFoundException(
                "VerifierWorker.exe not found. Run scripts/build.ps1 first.",
                workerExe
            );
        }

        _workerLauncher.Start(workerExe);
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

    private InterKnotApiClient BuildApiClient()
    {
        var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("API URL is invalid.");
        }
        var http = new HttpClient
        {
            BaseAddress = baseUri
        };
        return new InterKnotApiClient(http, async ct =>
        {
            var tokens = await _tokenStore.ReadAsync(ct);
            return tokens?.AccessToken;
        });
    }

    private async Task RunUiActionAsync(string startMessage, Func<CancellationToken, Task> action)
    {
        AppendStatus(startMessage);
        LoginButton.IsEnabled = false;
        RosterScanButton.IsEnabled = false;
        MatchMonitorButton.IsEnabled = false;
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
            LoginButton.IsEnabled = true;
            RosterScanButton.IsEnabled = true;
            MatchMonitorButton.IsEnabled = true;
        }
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

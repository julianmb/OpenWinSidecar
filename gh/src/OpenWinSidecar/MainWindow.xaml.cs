using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenWinSidecar.Core.Models;
using OpenWinSidecar.Core.Services;
using OpenWinSidecar.Service;
using Forms = System.Windows.Forms;

namespace OpenWinSidecar;

public partial class MainWindow : Window
{
    private readonly SidecarManager _manager = new();
    private readonly StreamingServerHost _streamingHost = new();
    private readonly DispatcherTimer _timer = new();
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit = false;

    // Logs live in a separate window; a small in-memory buffer backfills it on open.
    private LogWindow? _logWindow;
    private readonly List<string> _logBuffer = new();
    private const int MaxLogBufferLines = 2000;
    private readonly string? _screenshotPath;
    private readonly bool _expandAllForScreenshot;
    private readonly bool _autoStartRequested;

    public MainWindow()
    {
        InitializeComponent();
        CenterOnPrimaryScreen();
        try
        {
            var appIcon = LoadAppIcon();
            if (appIcon != null)
            {
                var iconSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    appIcon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                iconSource.Freeze();
                Icon = iconSource;
                HeaderIconImage.Source = iconSource; // branded header tile (light theme)
            }
        }
        catch { }

        InitializeSystemTray();

        // --screenshot <path> [--expand]: render the window to a PNG and exit (UI diagnostics / CI)
        var args = Environment.GetCommandLineArgs();
        int shotIdx = Array.IndexOf(args, "--screenshot");
        if (shotIdx >= 0 && shotIdx + 1 < args.Length)
        {
            _screenshotPath = args[shotIdx + 1];
            _expandAllForScreenshot = args.Contains("--expand");
            _isExplicitExit = true;
            _notifyIcon?.Dispose();
            _notifyIcon = null;
        }
        // --autostart: launched at sign-in via the Run key — bring the display + service up
        if (args.Contains("--autostart")) _autoStartRequested = true;

        _manager.IsStreamingActive = () => _streamingHost.IsRunning;
        _manager.OnStartStreaming = () => _streamingHost.Start();
        _manager.OnStopStreaming = () => _streamingHost.Stop();

        // BeginInvoke, never Invoke: a blocking Invoke stalls the capture/streaming threads on UI
        // rendering roughly once per second, which the client feels as a periodic network gap.
        _streamingHost.OnLog += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));

        _streamingHost.OnRunningStateChanged += _ =>
        {
            Dispatcher.Invoke(UpdateUi);
        };

        _manager.ProcessManager.OnLogReceived += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));

        _manager.ProcessManager.OnStatusChanged += () =>
        {
            Dispatcher.Invoke(UpdateUi);
        };

        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (s, e) => await RefreshDataAsync();

        Loaded += async (s, e) =>
        {
            // Title version comes from the assembly (csproj <Version>); the XAML default is
            // only a design-time placeholder.
            TxtVersion.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0");

            PopulateResolutionPresets();
            PopulateStreamDisplayCombo();
            LoadSettingsIntoUi();
            LoadStreamSettingsIntoUi();
            ApplyStreamSettingsFromUi();
            // Immediate-apply dropdowns (no Apply buttons): attach after the initial population
            // so the programmatic SelectedIndex doesn't trigger a display change.
            CmbResolution.SelectionChanged += CmbResolution_SelectionChanged;
            CmbDpi.SelectionChanged += CmbDpi_SelectionChanged;
            // Stream controls - attach handlers after loading settings
            CmbStreamDisplay.SelectionChanged += StreamSettingChanged;
            CmbStreamFps.SelectionChanged += StreamSettingChanged;
            CmbStreamQuality.SelectionChanged += StreamSettingChanged;
            CmbStreamCodec.SelectionChanged += StreamSettingChanged;
            CmbStreamColorDepth.SelectionChanged += StreamSettingChanged;
            CmbStreamZoom.SelectionChanged += StreamSettingChanged;
            await RefreshDataAsync();
            _timer.Start();
            await CheckFfmpegBannerAsync();

            if (_screenshotPath != null)
            {
                if (_expandAllForScreenshot)
                {
                    ExpandAllExpanders(this);
                }
                await Task.Delay(600);
                CaptureScreenshot(_screenshotPath);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Auto-start streaming on launch so the server is immediately active
            if (!_streamingHost.IsRunning)
            {
                await ToggleDisplayAsync(on: true);
                SetStatus("Sidecar streaming server active and ready.");
            }

            if (_autoStartRequested)
            {
                Hide();
            }
            else
            {
                RestoreWindowPosition();
                Show();
                WindowState = WindowState.Normal;
                Topmost = true;
                Topmost = false;
                Activate();
                Focus();
            }
        };
    }

    private static void ExpandAllExpanders(System.Windows.DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Expander exp && exp.Name != "LogExpander") exp.IsExpanded = true;
            ExpandAllExpanders(child);
        }
    }

    private void CaptureScreenshot(string path)
    {
        try
        {
            double width = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 1020);
            double height = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 580);
            System.Windows.Media.Visual visual = this;
            if (_expandAllForScreenshot && MainContent.ActualHeight > 0)
            {
                visual = MainContent;
                width = MainContent.ActualWidth > 0 ? MainContent.ActualWidth : 900;
                height = MainContent.ActualHeight;
            }

            Measure(new System.Windows.Size(width, height));
            Arrange(new System.Windows.Rect(0, 0, width, height));
            UpdateLayout();

            var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
        }
    }

    private void InitializeSystemTray()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "OpenWinSidecar",
            Visible = true,
            Icon = LoadAppIcon() ?? SystemIcons.Application
        };

        _notifyIcon.DoubleClick += (s, e) => ShowAndRestoreWindow();
        _notifyIcon.Click += (s, e) =>
        {
            if (e is Forms.MouseEventArgs me && me.Button == Forms.MouseButtons.Left)
            {
                ShowAndRestoreWindow();
            }
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ShowAndRestoreWindow());
        menu.Items.Add("Preview on this PC", null, (s, e) => OpenWebViewer());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Turn iPad display on", null, (s, e) => ToggleDisplay(on: true));
        menu.Items.Add("Turn iPad display off", null, (s, e) => ToggleDisplay(on: false));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) =>
        {
            _isExplicitExit = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        });

        _notifyIcon.ContextMenuStrip = menu;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MAXIMIZEBOX & ~WS_MINIMIZEBOX);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }
    }

    private void CenterOnPrimaryScreen()
    {
        try
        {
            double pW = SystemParameters.PrimaryScreenWidth;
            double pH = SystemParameters.PrimaryScreenHeight;
            double w = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 1020);
            double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 580);
            Left = Math.Max(40, (pW - w) / 2);
            Top = Math.Max(40, (pH - h) / 2);
        }
        catch { }
    }

    private void ShowAndRestoreWindow()
    {
        if (Left < 0 || Left > SystemParameters.VirtualScreenWidth || Top < 0)
        {
            CenterOnPrimaryScreen();
        }
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
        Focus();
    }

    /// <summary>
    /// Loads the app icon for the tray. Tries the .ico file next to the executable first
    /// (copied via the csproj None item in a normal build), then falls back to the icon
    /// embedded in the exe via &lt;ApplicationIcon&gt; — which is the only copy that
    /// survives a self-contained single-file publish (None items aren't extracted).
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenWinSidecar.ico"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenWinSidecar.Console.ico"),
                // dotnet run layout: bin/Debug/net10.0-windows → up to the project folder
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "app.ico"),
            };

            foreach (var path in candidates)
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    using var stream = File.OpenRead(full);
                    return new Icon(stream);
                }
            }

            // Self-contained single-file publish: the .ico file isn't on disk, but the
            // icon is embedded in the exe via <ApplicationIcon>. Extract it from there.
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch { }
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPosition();
        App.WriteLog($"MainWindow OnClosing: isExplicitExit={_isExplicitExit}");
        // Always minimize/hide to system tray on window close ('X')
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Still running in the system tray.", Forms.ToolTipIcon.Info);
            return;
        }

        _streamingHost.Stop();
        _streamingHost.Dispose();
        VirtualDisplayManager.DisableVirtualDisplay();
        _notifyIcon?.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.WriteLog("MainWindow OnClosed");
        base.OnClosed(e);
    }

    private void SetStatus(string message)
    {
        TxtStatus.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            await _manager.RefreshStateAsync();
            UpdateUi();
            // Monitors appear/change asynchronously (virtual display enable); keep the
            // Stream Controls display list in sync without disturbing the chosen device.
            var currentSelection = (CmbStreamDisplay.SelectedItem as ComboBoxItem)?.Tag as string;
            PopulateStreamDisplayCombo();
            if (currentSelection != null && CmbStreamDisplay.Items.Cast<ComboBoxItem>()
                    .Any(i => (string?)i.Tag == currentSelection))
            {
                CmbStreamDisplay.SelectedItem = CmbStreamDisplay.Items
                    .Cast<ComboBoxItem>().First(i => (string?)i.Tag == currentSelection);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
        }
    }

    private void UpdateUi()
    {
        bool isRunning = _streamingHost.IsRunning || _manager.ProcessManager.IsProcessRunning;
        bool isDisplayOn = isRunning && _manager.IsVirtualDisplayActive;

        // Service state: shape + text + color together (filled square = running,
        // hollow circle = stopped) so it reads under any color-vision deficiency
        StatusShape.Background = isRunning ? (SolidColorBrush)FindResource("Good") : null;
        StatusShape.BorderBrush = isRunning ? (SolidColorBrush)FindResource("Good") : (SolidColorBrush)FindResource("Bad");
        StatusShape.CornerRadius = isRunning ? new CornerRadius(2) : new CornerRadius(5);

        StatusChip.Background = isRunning ? (SolidColorBrush)FindResource("GoodBg") : (SolidColorBrush)FindResource("BadBg");
        StatusChip.BorderBrush = isRunning ? (SolidColorBrush)FindResource("Good") : (SolidColorBrush)FindResource("Bad");
        TxtServiceState.Foreground = isRunning ? (SolidColorBrush)FindResource("Good") : (SolidColorBrush)FindResource("Bad");

        TxtServiceState.Text = isRunning ? "Running" : "Stopped";

        // FFmpeg banner self-healing: the startup check can transiently miss FFmpeg (PATH /
        // where.exe flakiness) and show the banner while HEVC is actually streaming. Once a
        // hardware encoder is active the dependency is proven — collapse the banner here
        // rather than contradicting the session card's "Intel QuickSync" + HEVC client rows.
        if (FfmpegBanner.Visibility == Visibility.Visible
            && OpenWinSidecar.Service.Encoders.HevcStreamEncoder.HardwareEncoderActive)
        {
            FfmpegBanner.Visibility = Visibility.Collapsed;
        }

        // Hero card: the display state distinguishes "driver disabled / error / missing"
        // from the normal "off, ready to turn on" so the user knows *why* Turn on isn't
        // working instead of hitting a dead-end "Off" with no explanation.
        var deviceState = _manager.DeviceState;
        if (isDisplayOn)
        {
            var virtualMonitor = _manager.DisplayMonitors.FirstOrDefault(m => m.IsVirtual);
            TxtDisplayState.Text = "On — streaming";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            TxtDisplayDetail.Text = virtualMonitor != null
                ? $"{virtualMonitor.DeviceName} · {virtualMonitor.Width}×{virtualMonitor.Height} @ {virtualMonitor.RefreshRate} Hz"
                : "Virtual display active & streaming";
            BtnToggleDisplay.Content = "Turn off";
            BtnToggleDisplay.Style = (Style)FindResource("DangerButton");
            BtnToggleDisplay.IsEnabled = true;
        }
        else if (deviceState == VirtualDisplayDeviceState.Disabled)
        {
            TxtDisplayState.Text = "Driver disabled";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            TxtDisplayDetail.Text = VirtualDisplayManager.IsElevated()
                ? "Click to enable the driver and start streaming"
                : "Restart the app as administrator to enable the driver";
            BtnToggleDisplay.Content = VirtualDisplayManager.IsElevated() ? "Turn on" : "Restart as admin";
            BtnToggleDisplay.Style = (Style)FindResource("PrimaryButton");
            BtnToggleDisplay.IsEnabled = true;
        }
        else if (deviceState == VirtualDisplayDeviceState.Problem)
        {
            TxtDisplayState.Text = "Driver error";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            TxtDisplayDetail.Text = "The virtual display driver reported an error — try Restart driver";
            BtnToggleDisplay.Content = "Restart driver";
            BtnToggleDisplay.Style = (Style)FindResource("PrimaryButton");
            BtnToggleDisplay.IsEnabled = true;
        }
        else if (deviceState == VirtualDisplayDeviceState.Missing)
        {
            TxtDisplayState.Text = "Driver not installed";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            TxtDisplayDetail.Text = "The virtual display driver was not found — reinstall OpenWinSidecar";
            BtnToggleDisplay.Content = "Turn on";
            BtnToggleDisplay.Style = (Style)FindResource("PrimaryButton");
            BtnToggleDisplay.IsEnabled = false;
        }
        else
        {
            // Normal off — driver is Started (or Unknown) but not streaming
            TxtDisplayState.Text = "Off";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            TxtDisplayDetail.Text = "The virtual display is offline — click Turn on to stream";
            BtnToggleDisplay.Content = "Turn on";
            BtnToggleDisplay.Style = (Style)FindResource("PrimaryButton");
            BtnToggleDisplay.IsEnabled = true;
        }

        // Connect URL & network endpoint selection (prioritize Wi-Fi and USB)
        var endpoints = _manager.NetworkEndpoints.Where(e => e.Priority > 0).ToList();
        if (endpoints.Count == 0) endpoints = _manager.NetworkEndpoints;

        var prevSelected = CmbNetworkEndpoints.SelectedItem as NetworkEndpointInfo;
        string? prevIp = prevSelected?.PrimaryIpAddress;

        CmbNetworkEndpoints.SelectionChanged -= CmbNetworkEndpoints_SelectionChanged;
        CmbNetworkEndpoints.ItemsSource = endpoints;

        var targetEndpoint = endpoints.FirstOrDefault(e => e.PrimaryIpAddress == prevIp)
                             ?? endpoints.FirstOrDefault();

        if (targetEndpoint != null)
        {
            CmbNetworkEndpoints.SelectedItem = targetEndpoint;
            TxtConnectUrl.Text = $"http://{targetEndpoint.PrimaryIpAddress}:8080";
        }
        else
        {
            TxtConnectUrl.Text = "http://localhost:8080";
        }
        CmbNetworkEndpoints.SelectionChanged += CmbNetworkEndpoints_SelectionChanged;

        UpdateQrCode(TxtConnectUrl.Text);

        // Clients — live from the streaming engine (the legacy registry entries are unrelated).
        var clients = _streamingHost.IsRunning
            ? OpenWinSidecar.Service.Capture.ClientFrameSink.Snapshot()
            : new List<OpenWinSidecar.Service.Capture.ClientFrameSink.ConnectedClientSnapshot>();
        TxtClientsEmpty.Visibility = clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtClientsHint.Visibility = clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientsList.Visibility = clients.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtClientsHeader.Text = clients.Count == 0 ? "Connected clients" : $"Connected clients ({clients.Count})";
        ClientsList.ItemsSource = clients;

        // Session card
        var sessionMonitor = _manager.DisplayMonitors.FirstOrDefault(m => m.IsVirtual);
        TxtSessionState.Text = _streamingHost.IsRunning ? "Streaming" : "Stopped";
        TxtSessionDisplay.Text = sessionMonitor != null
            ? $"{sessionMonitor.DeviceName} · {sessionMonitor.Width}×{sessionMonitor.Height} @ {sessionMonitor.RefreshRate} Hz"
            : "—";
        TxtSessionEncoder.Text = _streamingHost.IsRunning
            ? OpenWinSidecar.Service.Encoders.HevcStreamEncoder.SelectedEncoderName
            : "—";
        TxtSessionClients.Text = clients.Count.ToString();
        TxtSessionUptime.Text = _streamingHost.IsRunning
            ? FormatUptime(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime())
            : "—";

        // Performance Sparkline
        double maxFps = clients.Count > 0 ? clients.Max(c => c.Fps) : 0;
        double avgLat = clients.Count > 0 ? clients.Where(c => c.LatencyMs > 0).Select(c => (double)c.LatencyMs).DefaultIfEmpty(0).Average() : 0;
        UpdateSparkline(maxFps, avgLat);
    }

    private readonly Queue<(double fps, double lat)> _sparklineHistory = new();

    private void UpdateSparkline(double currentFps, double currentLat)
    {
        _sparklineHistory.Enqueue((currentFps, currentLat));
        while (_sparklineHistory.Count > 30)
            _sparklineHistory.Dequeue();

        TxtSparkFps.Text = currentFps > 0 ? $"{currentFps:F0} fps" : "— fps";
        TxtSparkLat.Text = currentLat > 0 ? $"{currentLat:F0} ms" : "— ms";

        CanvasSparkline.Children.Clear();
        double w = CanvasSparkline.ActualWidth > 0 ? CanvasSparkline.ActualWidth : 180;
        double h = CanvasSparkline.ActualHeight > 0 ? CanvasSparkline.ActualHeight : 32;

        if (_sparklineHistory.Count < 2) return;

        var fpsPoints = new System.Windows.Media.PointCollection();
        var latPoints = new System.Windows.Media.PointCollection();

        int count = _sparklineHistory.Count;
        double step = w / Math.Max(count - 1, 1);
        int i = 0;

        foreach (var (fps, lat) in _sparklineHistory)
        {
            double x = i * step;
            double yFps = h - Math.Clamp(fps / 120.0, 0, 1) * (h - 4) - 2;
            fpsPoints.Add(new System.Windows.Point(x, yFps));

            double yLat = h - Math.Clamp(lat / 120.0, 0, 1) * (h - 4) - 2;
            latPoints.Add(new System.Windows.Point(x, yLat));
            i++;
        }

        // 60fps guideline at 50% height
        var midLine = new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w,
            Y1 = h / 2.0, Y2 = h / 2.0,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 2 }
        };
        CanvasSparkline.Children.Add(midLine);

        var polyLat = new System.Windows.Shapes.Polyline
        {
            Points = latPoints,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A)),
            StrokeThickness = 1.2,
            Opacity = 0.85
        };
        CanvasSparkline.Children.Add(polyLat);

        var polyFps = new System.Windows.Shapes.Polyline
        {
            Points = fpsPoints,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58)),
            StrokeThickness = 1.5
        };
        CanvasSparkline.Children.Add(polyFps);
    }

    private void BtnKickClient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string addr && !string.IsNullOrEmpty(addr))
        {
            bool kicked = OpenWinSidecar.Service.Capture.ClientFrameSink.DisconnectByAddress(addr);
            if (kicked)
            {
                SetStatus($"Disconnected client {addr}.");
                _ = RefreshDataAsync();
            }
        }
    }

    private void RestoreWindowPosition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\OpenWinSidecar");
            if (key != null)
            {
                var leftVal = key.GetValue("WindowLeft");
                var topVal = key.GetValue("WindowTop");
                if (leftVal is int left && topVal is int top)
                {
                    var rect = new System.Drawing.Rectangle(left, top, (int)Width, (int)Height);
                    bool onAnyScreen = System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
                    if (onAnyScreen)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = left;
                        Top = top;
                        return;
                    }
                }
            }
        }
        catch { }
        CenterOnPrimaryScreen();
    }

    private void SaveWindowPosition()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\OpenWinSidecar");
                key?.SetValue("WindowLeft", (int)Left);
                key?.SetValue("WindowTop", (int)Top);
            }
        }
        catch { }
    }

    private static string FormatUptime(TimeSpan t)
        => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
         : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
         : $"{t.Minutes}m {t.Seconds}s";

    private void CmbNetworkEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbNetworkEndpoints.SelectedItem is NetworkEndpointInfo endpoint)
        {
            TxtConnectUrl.Text = $"http://{endpoint.PrimaryIpAddress}:8080";
            UpdateQrCode(TxtConnectUrl.Text);
            SetStatus($"Active connection endpoint switched to {endpoint.DisplayLabel}");
        }
    }

    // ----- Primary action -----

    private async void BtnToggleDisplay_Click(object sender, RoutedEventArgs e)
    {
        var btnLabel = BtnToggleDisplay.Content?.ToString() ?? "";

        // The display card repurposes the toggle button for driver triage when the
        // VDD is disabled, in error, or needs elevation. Route those labels to the
        // correct action instead of the normal on/off toggle.
        if (btnLabel == "Restart as admin")
        {
            BtnStartElevated_Click(sender, e);
            return;
        }
        if (btnLabel == "Restart driver")
        {
            BtnRestartDriver_Click(sender, e);
            return;
        }

        bool shouldTurnOn = btnLabel == "Turn on"
                            || !(_streamingHost.IsRunning || _manager.ProcessManager.IsProcessRunning)
                            || !_manager.IsVirtualDisplayActive;

        await ToggleDisplayAsync(shouldTurnOn);
    }

    private void ToggleDisplay(bool on)
    {
        _ = ToggleDisplayAsync(on);
    }

    private async Task ToggleDisplayAsync(bool on)
    {
        BtnToggleDisplay.IsEnabled = false;
        try
        {
            var (ok, msg) = on
                ? _manager.EnableVirtualDisplayAndStartService()
                : _manager.DisableVirtualDisplayAndStopService();

            SetStatus(msg);
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", msg, Forms.ToolTipIcon.Info);

            // Immediate UI update so user gets instant visual feedback
            UpdateUi();

            await RefreshSoonAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Toggle error: {ex.Message}");
        }
        finally
        {
            BtnToggleDisplay.IsEnabled = true;
        }
    }

    private async Task RefreshSoonAsync()
    {
        // Display topology changes take a moment to settle
        await Task.Delay(1200);
        await RefreshDataAsync();
        await Task.Delay(2000);
        await RefreshDataAsync();
    }

    // ----- Display settings -----

    private void PopulateResolutionPresets()
    {
        var presets = new (string label, string val)[]
        {
            ("iPad Air 10.9\" — 2360 × 1640 (native, recommended)", "2360,1640,60"),
            ("iPad Air 10.9\" — 2360 × 1640 @ 120Hz (ProMotion)", "2360,1640,120"),
            ("iPad Air 10.9\" — 1180 × 820 (fast, @2x logical)", "1180,820,60"),
            ("iPad Air 10.9\" — 1180 × 820 @ 120Hz (ProMotion)", "1180,820,120"),
            ("iPad Pro 11\" M4 — 2420 × 1668 (native)", "2420,1668,60"),
            ("iPad Pro 11\" M4 — 2420 × 1668 @ 120Hz (ProMotion)", "2420,1668,120"),
            ("iPad Pro 11\" M4 — 1210 × 834 (fast)", "1210,834,60"),
            ("iPad Pro 11\" M4 — 1210 × 834 @ 120Hz (ProMotion)", "1210,834,120"),
            ("iPad Pro 11\" (1st–4th) — 2388 × 1668 (native)", "2388,1668,60"),
            ("iPad Pro 11\" (1st–4th) — 2388 × 1668 @ 120Hz (ProMotion)", "2388,1668,120"),
            ("iPad Pro 11\" (1st–4th) — 1194 × 834 (fast)", "1194,834,60"),
            ("iPad Pro 11\" (1st–4th) — 1194 × 834 @ 120Hz (ProMotion)", "1194,834,120"),
            ("iPad Pro 13\" M4 — 2752 × 2064 (native)", "2752,2064,60"),
            ("iPad Pro 13\" M4 — 2752 × 2064 @ 120Hz (ProMotion)", "2752,2064,120"),
            ("iPad Pro 13\" M4 — 1376 × 1032 (fast)", "1376,1032,60"),
            ("iPad Pro 12.9\" — 2732 × 2048 (native)", "2732,2048,60"),
            ("iPad Pro 12.9\" — 2732 × 2048 @ 120Hz (ProMotion)", "2732,2048,120"),
            ("iPad Pro 12.9\" — 1366 × 1024 (fast)", "1366,1024,60"),
            ("iPad mini 8.3\" — 2266 × 1488 (native)", "2266,1488,60"),
            ("iPad mini 8.3\" — 1133 × 744 (fast)", "1133,744,60"),
            ("Full HD — 1920 × 1080", "1920,1080,60"),
            ("QHD — 2560 × 1440", "2560,1440,60"),
        };

        foreach (var (label, val) in presets)
        {
            CmbResolution.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        }
        CmbResolution.SelectedIndex = 0;
    }

    /// <summary>The monitor the display settings apply to: the virtual display, else the primary.</summary>
    private DisplayMonitorInfo? GetTargetMonitor()
    {
        var monitors = _manager.DisplayMonitors;
        if (monitors.Count == 0) return null;
        if (CmbStreamDisplay.SelectedItem is ComboBoxItem selected && selected.Tag is string deviceName)
        {
            var match = monitors.FirstOrDefault(m => m.DeviceName == deviceName);
            if (match != null) return match;
        }
        return monitors.FirstOrDefault(m => m.IsVirtual) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private void CmbResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var monitor = GetTargetMonitor();
        if (monitor == null)
        {
            SetStatus("No display detected to apply a resolution to.");
            return;
        }

        if (CmbResolution.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var parts = tag.Split(',');
            if (parts.Length >= 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && int.TryParse(parts[2], out int hz))
            {
                ApplyResolution(monitor, w, h, hz);
            }
        }
    }

    private void BtnApplyCustomResolution_Click(object sender, RoutedEventArgs e)
    {
        var monitor = GetTargetMonitor();
        if (monitor == null)
        {
            SetStatus("No display detected to apply a resolution to.");
            return;
        }

        if (int.TryParse(TxtCustomWidth.Text.Trim(), out int w) && int.TryParse(TxtCustomHeight.Text.Trim(), out int h))
        {
            int.TryParse(TxtCustomHz.Text.Trim(), out int hz);
            if (hz <= 0) hz = 60;
            ApplyResolution(monitor, w, h, hz);
        }
        else
        {
            System.Windows.MessageBox.Show("Enter valid numeric width and height values.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ApplyResolution(DisplayMonitorInfo monitor, int width, int height, int refreshRate)
    {
        bool ok = DisplayResolutionManager.SetDisplayResolution(monitor.DeviceName, width, height, refreshRate);
        if (ok) ApplyStreamSettingsFromUi();
        if (ok)
        {
            SetStatus($"Resolution changed to {width}×{height} @ {refreshRate} Hz on {monitor.DeviceName}.");
            await Task.Delay(1200);
            await RefreshDataAsync();
        }
        else
        {
            SetStatus($"Could not apply {width}×{height} @ {refreshRate} Hz to {monitor.DeviceName}.");
            System.Windows.MessageBox.Show($"Could not apply {width}×{height} @ {refreshRate} Hz to {monitor.DeviceName}.\nMake sure the mode is supported by the display driver.", "Resolution error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CmbDpi_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var monitor = GetTargetMonitor();
        if (monitor == null)
        {
            SetStatus("No display detected to apply scaling to.");
            return;
        }

        if (CmbDpi.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int percent))
        {
            bool ok = WindowsDpiService.SetMonitorDpiPercent(monitor.Index, percent);
            if (ok)
            {
                SetStatus($"Windows display scaling set to {percent}% on {monitor.DeviceName}.");
            }
            else
            {
                System.Windows.MessageBox.Show("Could not update display scaling.", "DPI error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ----- Maintenance -----

    private void BtnStartElevated_Click(object sender, RoutedEventArgs e)
    {
        var (started, message) = _manager.ProcessManager.StartElevated();
        UpdateUi();
        SetStatus(started ? "Service started elevated." : $"Could not start elevated: {message}");
    }

    private void BtnRestartDriver_Click(object sender, RoutedEventArgs e)
    {
        VirtualDisplayManager.RestartVirtualDisplayDriver();
        SetStatus("Virtual display driver restarted.");
        _ = RefreshSoonAsync();
    }

    private void BtnForceExtend_Click(object sender, RoutedEventArgs e)
    {
        VirtualDisplayManager.EnableExtendMode();
        SetStatus("Displays set to extend mode.");
        _ = RefreshSoonAsync();
    }

    private void BtnForceMirror_Click(object sender, RoutedEventArgs e)
    {
        VirtualDisplayManager.EnableMirrorMode();
        SetStatus("Displays set to mirror mode.");
        _ = RefreshSoonAsync();
    }

    private void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
    {
        bool ok = VirtualDisplayManager.InstallVirtualDisplayDriver();
        if (ok)
        {
            SetStatus("Virtual display driver installed.");
            _ = RefreshSoonAsync();
        }
        else
        {
            System.Windows.MessageBox.Show("Could not install the driver. Run the console as Administrator and try again.", "Install driver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----- Connect & logs -----

    private string _qrUrl = "";

    /// <summary>Regenerates the connect QR code when the URL changes (cheap enough for the 3s poll).</summary>
    private void UpdateQrCode(string url)
    {
        if (url == _qrUrl) return;
        _qrUrl = url;

        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(
                6,
                new byte[] { 0x11, 0x11, 0x16, 0xFF },   // dark modules on a white card — QR needs contrast
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            var image = new BitmapImage();
            using var ms = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            QrImage.Source = image;
        }
        catch
        {
            // The QR is a convenience — never break the console over it
        }
    }

    private void QrCard_Click(object sender, RoutedEventArgs e) => ShowQrPopup(TxtConnectUrl.Text);

    /// <summary>Large across-the-room QR window; click anywhere to close.</summary>
    private void ShowQrPopup(string url)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(
                12,
                new byte[] { 0x11, 0x11, 0x16, 0xFF },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            var image = new BitmapImage();
            using var ms = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();

            var popup = new Window
            {
                Title = "Scan to connect",
                Icon = Icon, // inherit the main window's app icon
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(30) };
            var img = new System.Windows.Controls.Image { Source = image, Width = 420, Height = 420, Margin = new Thickness(0, 0, 0, 14) };
            var label = new TextBlock
            {
                Text = url,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 15,
                Foreground = System.Windows.Media.Brushes.Black,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            var hint = new TextBlock
            {
                Text = "Point the iPad camera at this code — click anywhere to close",
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.DimGray,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            stack.Children.Add(img);
            stack.Children.Add(label);
            stack.Children.Add(hint);
            popup.Content = stack;
            popup.MouseLeftButtonUp += (s, _) => popup.Close();
            popup.Show();
        }
        catch { }
    }

    private void OpenWebViewer()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "http://localhost:8080", UseShellExecute = true });
        }
        catch { }
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e) => OpenWebViewer();

    // ---- FFmpeg one-click fix ----

    private bool _installingFfmpeg;

    /// <summary>
    /// Shows the FFmpeg banner when the HEVC dependency is missing. Resolution spawns
    /// where.exe and can scan the WinGet tree (~100-300ms), so it runs off the UI thread.
    /// </summary>
    private async Task CheckFfmpegBannerAsync()
    {
        // The encoder's actual outcome is authoritative: if a hardware encoder passed its
        // probe, FFmpeg is by definition working and the banner must not show — even when
        // this file-resolution lookup transiently misses (PATH/where.exe flakiness).
        if (OpenWinSidecar.Service.Encoders.HevcStreamEncoder.HardwareEncoderActive)
        {
            FfmpegBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var ffmpeg = await Task.Run(
            () => OpenWinSidecar.Service.Encoders.HevcStreamEncoder.FindFfmpegExecutable());
        if (ffmpeg != null)
        {
            FfmpegBanner.Visibility = Visibility.Collapsed;
            return;
        }
        TxtFfmpegDetail.Text = OpenWinSidecar.Service.Encoders.FfmpegBootstrap.FindWingetExecutable() != null
            ? "Hardware HEVC needs the Gyan FFmpeg build. One-click install via winget (~170 MB download)."
            : "Hardware HEVC needs the Gyan FFmpeg build, but winget was not found. Install it from a terminal: winget install --id Gyan.FFmpeg -e";
        FfmpegBanner.Visibility = Visibility.Visible;
    }

    private async void BtnInstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (_installingFfmpeg) return;
        _installingFfmpeg = true;
        BtnInstallFfmpeg.IsEnabled = false;
        TxtFfmpegDetail.Text = "Installing Gyan.FFmpeg via winget — this downloads ~170 MB and can take a few minutes…";

        try
        {
            await Task.Run(() =>
                OpenWinSidecar.Service.Encoders.FfmpegBootstrap.InstallFfmpegViaWingetAsync(
                    msg => Dispatcher.BeginInvoke(() => AppendLog(msg))));

            // Re-resolve regardless of exit code: winget may have succeeded where a
            // non-zero code means "already installed" (which is fine for us).
            OpenWinSidecar.Service.Encoders.HevcStreamEncoder.ResetFfmpegCache();
            var ffmpeg = await Task.Run(
                () => OpenWinSidecar.Service.Encoders.HevcStreamEncoder.FindFfmpegExecutable());

            if (ffmpeg != null)
            {
                FfmpegBanner.Visibility = Visibility.Collapsed;
                AppendLog("[FFmpeg] FFmpeg is available — new client sessions use hardware HEVC.");
                SetStatus("FFmpeg installed — hardware HEVC enabled");
            }
            else
            {
                TxtFfmpegDetail.Text = "Installation did not produce a usable FFmpeg. Install manually: winget install --id Gyan.FFmpeg -e (full build), then restart the app.";
                AppendLog("[FFmpeg] FFmpeg still not found after the winget attempt.");
            }
        }
        finally
        {
            BtnInstallFfmpeg.IsEnabled = true;
            _installingFfmpeg = false;
        }
    }

    /// <summary>
    /// Appends a timestamped line to the log buffer and, if open, the separate log window.
    /// Must be called on the UI thread (callers wrap in Dispatcher.Invoke).
    /// </summary>
    private void AppendLog(string msg)
    {
        // Filter out noisy HEVC and Hub diagnostics by default.
        if (msg.StartsWith("[HEVC] parser stalls:") ||
            msg.StartsWith("[HEVC] first-VCL seen with no pending push stamp") ||
            msg.Contains("capture=") || msg.Contains("compose=") || msg.Contains("sinks="))
        {
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _logBuffer.Add(line);
        if (_logBuffer.Count > MaxLogBufferLines)
            _logBuffer.RemoveRange(0, _logBuffer.Count - MaxLogBufferLines);
        _logWindow?.Append(line + Environment.NewLine);
        TxtLogsPreview.AppendText(line + Environment.NewLine);
        if (TxtLogsPreview.LineCount > 1500)
        {
            var tail = _logBuffer.Skip(Math.Max(0, _logBuffer.Count - 400));
            TxtLogsPreview.Text = string.Join(Environment.NewLine, tail) + Environment.NewLine;
        }
        TxtLogsPreview.ScrollToEnd();
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow { Owner = this };
            _logWindow.Closed += (_, _) => _logWindow = null;
            foreach (var line in _logBuffer)
                _logWindow.Append(line + Environment.NewLine);
            _logWindow.Show();
        }
        else
        {
            _logWindow.Activate();
        }
    }

    private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtConnectUrl.Text);
            SetStatus("Connection URL copied to clipboard.");
        }
        catch { }
    }


    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    // ----- Preferences -----

    private void PopulateStreamDisplayCombo()
    {
        CmbStreamDisplay.Items.Clear();
        var monitors = _manager.DisplayMonitors;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var label = m.IsPrimary
                ? $"Main Screen ({m.Width}x{m.Height})"
                : m.IsVirtual
                    ? $"Virtual iPad Screen ({m.Width}x{m.Height})"
                    : $"Display {i + 1} ({m.Width}x{m.Height})";
            CmbStreamDisplay.Items.Add(new ComboBoxItem { Content = label, Tag = m.DeviceName });
        }
        if (CmbStreamDisplay.Items.Count > 0)
            CmbStreamDisplay.SelectedItem = CmbStreamDisplay.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
                monitors.Any(m => m.IsVirtual && m.DeviceName == (string)i.Tag)) ?? CmbStreamDisplay.Items[0];
    }

    private void LoadStreamSettingsIntoUi()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\OpenWinSidecar\Stream");
            if (key != null)
            {
                if (key.GetValue("DeviceName") is string device)
                    CmbStreamDisplay.SelectedItem = CmbStreamDisplay.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == device)
                        ?? CmbStreamDisplay.SelectedItem;
                if (key.GetValue("Zoom") is string zoom)
                    CmbStreamZoom.SelectedItem = CmbStreamZoom.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == zoom)
                        ?? CmbStreamZoom.SelectedItem;

                // FPS
                var fpsVal = key.GetValue("Fps");
                if (fpsVal is int fps)
                {
                    var fpsItem = CmbStreamFps.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is string s && s == fps.ToString());
                    if (fpsItem != null) CmbStreamFps.SelectedItem = fpsItem;
                }

                // Quality
                var qualityVal = key.GetValue("Quality");
                if (qualityVal is int quality)
                {
                    var qualityItem = CmbStreamQuality.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is string s && s == quality.ToString());
                    if (qualityItem != null) CmbStreamQuality.SelectedItem = qualityItem;
                }

                // Codec
                var codecVal = key.GetValue("Codec") as string;
                if (!string.IsNullOrEmpty(codecVal))
                {
                    var codecItem = CmbStreamCodec.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is string s && s.Equals(codecVal, StringComparison.OrdinalIgnoreCase));
                    if (codecItem != null) CmbStreamCodec.SelectedItem = codecItem;
                }

                // Color depth
                var depthVal = key.GetValue("ColorDepth");
                if (depthVal is int depth)
                {
                    var depthItem = CmbStreamColorDepth.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is string s && s == depth.ToString());
                    if (depthItem != null) CmbStreamColorDepth.SelectedItem = depthItem;
                }
            }
        }
        catch { }
    }

    private void SaveStreamSettingsToRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\OpenWinSidecar\Stream");
            if (key == null) return;

            if (CmbStreamDisplay.SelectedItem is ComboBoxItem display) key.SetValue("DeviceName", (string)display.Tag);
            if (CmbStreamZoom.SelectedItem is ComboBoxItem zoom) key.SetValue("Zoom", (string)zoom.Tag);

            if (CmbStreamFps.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is string fpsStr && int.TryParse(fpsStr, out var fps))
                key.SetValue("Fps", fps, Microsoft.Win32.RegistryValueKind.DWord);

            if (CmbStreamQuality.SelectedItem is ComboBoxItem qualityItem && qualityItem.Tag is string qualityStr && int.TryParse(qualityStr, out var quality))
                key.SetValue("Quality", quality, Microsoft.Win32.RegistryValueKind.DWord);

            if (CmbStreamCodec.SelectedItem is ComboBoxItem codecItem && codecItem.Tag is string codec)
                key.SetValue("Codec", codec, Microsoft.Win32.RegistryValueKind.String);

            if (CmbStreamColorDepth.SelectedItem is ComboBoxItem depthItem && depthItem.Tag is string depthStr && int.TryParse(depthStr, out var depth))
                key.SetValue("ColorDepth", depth, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    private void ApplyStreamSettingsFromUi()
    {
        string Value(System.Windows.Controls.ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
        _streamingHost.SetStreamSettings(new HostStreamSettings
        {
            DeviceName = (CmbStreamDisplay.SelectedItem as ComboBoxItem)?.Tag as string,
            Fps = int.Parse(Value(CmbStreamFps, "60")),
            Quality = int.Parse(Value(CmbStreamQuality, "80")),
            Codec = Value(CmbStreamCodec, "hevc") == "hevc" ? OpenWinSidecar.Service.Protocol.StreamCodec.HEVC : OpenWinSidecar.Service.Protocol.StreamCodec.IntraTurbo,
            ColorDepth = int.Parse(Value(CmbStreamColorDepth, "8")),
            Zoom = double.Parse(Value(CmbStreamZoom, "1.0"), System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private void StreamSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyStreamSettingsFromUi();
        SaveStreamSettingsToRegistry();
        SetStatus("Stream settings applied. Display changes reconnect viewers.");
    }

    private void LoadSettingsIntoUi()
    {
        var s = _manager.CurrentSettings;
        ChkAutoStart.IsChecked = s.ServerStartType == ServerStartMode.On;
        TxtPassword.Password = s.EncryptionPassword;
        UpdateAccessBadge(!string.IsNullOrWhiteSpace(s.EncryptionPassword));
    }

    /// <summary>
    /// Shows whether the server is open to the LAN or password protected. "Open" is a yellow/
    /// warning state (anyone on the network can view the screen and inject input).
    /// </summary>
    private void UpdateAccessBadge(bool hasPassword)
    {
        if (hasPassword)
        {
            AccessBadgeShape.Background = (SolidColorBrush)FindResource("Good");
            TxtAccessBadge.Foreground = (SolidColorBrush)FindResource("Good");
            TxtAccessBadge.Text = "Password protected";
        }
        else
        {
            AccessBadgeShape.Background = (SolidColorBrush)FindResource("Bad");
            TxtAccessBadge.Foreground = (SolidColorBrush)FindResource("Bad");
            TxtAccessBadge.Text = "Open access — anyone on this network can connect";
        }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = new SidecarSettings
        {
            ServerStartType = ChkAutoStart.IsChecked == true ? ServerStartMode.On : ServerStartMode.Off,
            EncryptionPassword = TxtPassword.Password ?? string.Empty
        };

        bool saved = _manager.RegistryManager.SaveSettings(s);
        SetAutostartRunKey(ChkAutoStart.IsChecked == true);
        UpdateAccessBadge(!string.IsNullOrWhiteSpace(s.EncryptionPassword));
        SetStatus(saved ? "Preferences saved." : "Could not save preferences (run as Administrator).");
    }

    /// <summary>
    /// Makes 'Start with Windows' real: a per-user Run key launches the Console with
    /// --autostart at sign-in, which enables the virtual display and starts streaming.
    /// </summary>
    private static void SetAutostartRunKey(bool enabled)
    {
        const string valueName = "OpenWinSidecar";
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenWinSidecar.exe");
                if (File.Exists(exe))
                    key.SetValue(valueName, $"\"{exe}\" --autostart");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private void ChkAutoPoll_Checked(object sender, RoutedEventArgs e) => _timer.Start();
    private void ChkAutoPoll_Unchecked(object sender, RoutedEventArgs e) => _timer.Stop();
}

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenWinSidecar.Core.Models;
using OpenWinSidecar.Core.Services;
using Forms = System.Windows.Forms;

namespace OpenWinSidecar.Console;

public partial class MainWindow : Window
{
    private readonly SpacedeskManager _manager = new();
    private readonly DispatcherTimer _timer = new();
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit = false;
    private readonly string? _screenshotPath;
    private readonly bool _expandAllForScreenshot;

    public MainWindow()
    {
        InitializeComponent();
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

        _manager.ProcessManager.OnLogReceived += msg =>
        {
            Dispatcher.Invoke(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                TxtLogsConsole.AppendText(line);
                TxtLogsConsole.ScrollToEnd();
            });
        };

        _manager.ProcessManager.OnStatusChanged += () =>
        {
            Dispatcher.Invoke(UpdateUi);
        };

        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (s, e) => await RefreshDataAsync();

        Loaded += async (s, e) =>
        {
            PopulateResolutionPresets();
            LoadSettingsIntoUi();
            await RefreshDataAsync();
            _timer.Start();

            if (_screenshotPath != null)
            {
                if (_expandAllForScreenshot)
                {
                    ExpandAllExpanders(this);
                }
                await Task.Delay(600);
                CaptureScreenshot(_screenshotPath);
                System.Windows.Application.Current.Shutdown();
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
            // In expanded mode render the whole content panel (unclipped by the window viewport)
            System.Windows.Media.Visual visual = this;
            double width = ActualWidth, height = ActualHeight;
            if (_expandAllForScreenshot && MainContent.ActualHeight > 0)
            {
                visual = MainContent;
                width = MainContent.ActualWidth;
                height = MainContent.ActualHeight;
            }

            var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Screenshot failed: {ex.Message}", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        menu.Items.Add("Open web viewer", null, (s, e) => OpenWebViewer());
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

    private void ShowAndRestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    /// <summary>
    /// Loads the app icon for the tray. The .ico sits next to the executable (copied via
    /// ApplicationIcon); fall back through AppDomain base directory, then the repo source
    /// tree, so the tray icon works from `dotnet run` as well as the published exe.
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
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
        }
        catch { }
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit && ChkMinimizeToTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Still running in the system tray.", Forms.ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon?.Dispose();
            base.OnClosing(e);
        }
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
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
        }
    }

    private void UpdateUi()
    {
        bool isRunning = _manager.ProcessManager.IsProcessRunning;
        bool isDisplayOn = _manager.IsVirtualDisplayActive;

        StatusDot.Fill = isRunning ? (SolidColorBrush)FindResource("Good") : (SolidColorBrush)FindResource("Bad");
        TxtServiceState.Text = isRunning ? $"Running · PID {_manager.ProcessManager.ProcessId} · {_manager.ProcessManager.MemoryUsageMb:F0} MB" : "Stopped";

        // Hero card: single source of truth for the iPad display state
        if (isDisplayOn)
        {
            var virtualMonitor = _manager.DisplayMonitors.FirstOrDefault(m => m.IsVirtual);
            TxtDisplayState.Text = isRunning ? "On — streaming" : "On";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            TxtDisplayDetail.Text = virtualMonitor != null
                ? $"{virtualMonitor.DeviceName} · {virtualMonitor.Width}×{virtualMonitor.Height} @ {virtualMonitor.RefreshRate} Hz"
                : "Virtual display active";
            BtnToggleDisplay.Content = "Turn off";
        }
        else
        {
            TxtDisplayState.Text = "Off";
            TxtDisplayState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            TxtDisplayDetail.Text = "The virtual display is disabled";
            BtnToggleDisplay.Content = "Turn on";
        }

        // Connect URL: prefer a real LAN endpoint over loopback
        var endpoint = _manager.NetworkEndpoints.FirstOrDefault(e =>
                           !e.PrimaryIpAddress.StartsWith("127.") && !e.PrimaryIpAddress.StartsWith("169.254"))
                       ?? _manager.NetworkEndpoints.FirstOrDefault();
        TxtConnectUrl.Text = endpoint != null ? $"http://{endpoint.PrimaryIpAddress}:8080" : "http://localhost:8080";
        UpdateQrCode(TxtConnectUrl.Text);

        // Clients
        var clients = _manager.Clients;
        TxtClientsEmpty.Visibility = clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientsList.Visibility = clients.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtClientsHeader.Text = clients.Count == 0 ? "Connected clients" : $"Connected clients ({clients.Count})";
        ClientsList.ItemsSource = clients;
    }

    // ----- Primary action -----

    private void BtnToggleDisplay_Click(object sender, RoutedEventArgs e)
    {
        ToggleDisplay(on: !_manager.IsVirtualDisplayActive);
    }

    private void ToggleDisplay(bool on)
    {
        var (ok, msg) = on
            ? _manager.EnableVirtualDisplayAndStartService()
            : _manager.DisableVirtualDisplayAndStopService();

        SetStatus(msg);
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", msg, Forms.ToolTipIcon.Info);
        _ = RefreshSoonAsync();
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
            ("iPad Air 10.9\" — 1180 × 820 (fast, @2x logical)", "1180,820,60"),
            ("iPad Air 10.9\" — 2360 × 1640 (native)", "2360,1640,60"),
            ("iPad Pro 11\" M4 — 1210 × 834 (fast)", "1210,834,60"),
            ("iPad Pro 11\" M4 — 2420 × 1668 (native)", "2420,1668,60"),
            ("iPad Pro 13\" M4 — 1376 × 1032 (fast)", "1376,1032,60"),
            ("iPad Pro 13\" M4 — 2752 × 2064 (native)", "2752,2064,60"),
            ("iPad Pro 12.9\" — 1366 × 1024 (fast)", "1366,1024,60"),
            ("iPad Pro 12.9\" — 2732 × 2048 (native)", "2732,2048,60"),
            ("iPad mini 8.3\" — 1133 × 744 (fast)", "1133,744,60"),
            ("iPad mini 8.3\" — 2266 × 1488 (native)", "2266,1488,60"),
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
        return monitors.FirstOrDefault(m => m.IsVirtual) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private void BtnApplyResolution_Click(object sender, RoutedEventArgs e)
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

    private void BtnApplyDpi_Click(object sender, RoutedEventArgs e)
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

    private void BtnStartService_Click(object sender, RoutedEventArgs e)
    {
        var (started, message) = _manager.ProcessManager.StartInteractive();
        UpdateUi();
        SetStatus(started ? "Service started." : $"Could not start service: {message}");
        if (!started)
        {
            System.Windows.MessageBox.Show($"Could not start the service:\n\n{message}\n\nTry 'Start service elevated' instead.", "Service start error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnStartElevated_Click(object sender, RoutedEventArgs e)
    {
        var (started, message) = _manager.ProcessManager.StartElevated();
        UpdateUi();
        SetStatus(started ? "Service started elevated." : $"Could not start elevated: {message}");
    }

    private void BtnStopService_Click(object sender, RoutedEventArgs e)
    {
        _manager.ProcessManager.StopInteractive();
        UpdateUi();
        SetStatus("Service stopped.");
    }

    private void BtnCleanStale_Click(object sender, RoutedEventArgs e)
    {
        _manager.ProcessManager.KillAllStaleProcesses();
        UpdateUi();
        SetStatus("Stale processes cleaned.");
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

    private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtConnectUrl.Text);
            SetStatus("Connection URL copied to clipboard.");
        }
        catch { }
    }

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLogsConsole.Text))
        {
            System.Windows.Clipboard.SetText(TxtLogsConsole.Text);
            SetStatus("Log copied to clipboard.");
        }
    }

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        TxtLogsConsole.Clear();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    // ----- Preferences -----

    private void LoadSettingsIntoUi()
    {
        var s = _manager.CurrentSettings;
        ChkAutoStart.IsChecked = s.ServerStartType == ServerStartMode.On;
        ChkIosUsb.IsChecked = s.IosUsbControlEnabled;
        TxtPassword.Text = s.EncryptionPassword;
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = new SpacedeskSettings
        {
            ServerStartType = ChkAutoStart.IsChecked == true ? ServerStartMode.On : ServerStartMode.Off,
            IosUsbControlEnabled = ChkIosUsb.IsChecked == true,
            EncryptionPassword = TxtPassword.Text ?? string.Empty
        };

        bool saved = _manager.RegistryManager.SaveSettings(s);
        SetStatus(saved ? "Preferences saved." : "Could not save preferences (run as Administrator).");
    }

    private void ChkAutoPoll_Checked(object sender, RoutedEventArgs e) => _timer.Start();
    private void ChkAutoPoll_Unchecked(object sender, RoutedEventArgs e) => _timer.Stop();
}

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public MainWindow()
    {
        InitializeComponent();
        InitializeSystemTray();

        _manager.ProcessManager.OnLogReceived += msg =>
        {
            Dispatcher.Invoke(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                TxtLogsConsole.AppendText(line);
                if (ChkAutoScrollLogs.IsChecked == true)
                {
                    TxtLogsConsole.ScrollToEnd();
                }
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
            await RefreshDataAsync();
            PopulateDisplayTargets();
            LoadSettingsIntoUi();
            _timer.Start();
        };
    }

    private void InitializeSystemTray()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "OpenWinSidecar Dashboard",
            Visible = true,
            Icon = SystemIcons.Application
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
        menu.Items.Add("🖥️ Open Console", null, (s, e) => ShowAndRestoreWindow());
        menu.Items.Add("🌐 Open Web Viewer", null, (s, e) => OpenWebViewer());
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("⚡ Turn ON 3rd Screen & Stream", null, (s, e) => BtnEnable3rdScreen_Click(this, new RoutedEventArgs()));
        menu.Items.Add("🔌 Turn OFF 3rd Screen & Stop", null, (s, e) => BtnDisable3rdScreen_Click(this, new RoutedEventArgs()));
        menu.Items.Add("🛑 Complete Shutdown (All)", null, (s, e) => BtnCompleteShutdown_Click(this, new RoutedEventArgs()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("▶️ Start Service Only", null, (s, e) => _manager.ProcessManager.StartInteractive());
        menu.Items.Add("⏹️ Stop Service Only", null, (s, e) => _manager.ProcessManager.StopInteractive());
        menu.Items.Add("🔄 Restart Driver & Displays", null, (s, e) => RestartDriverAction());
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("❌ Exit OpenWinSidecar", null, (s, e) =>
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit && ChkMinimizeToTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "OpenWinSidecar is running in the system tray.", Forms.ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon?.Dispose();
            base.OnClosing(e);
        }
    }

    private void PopulateResolutionPresets()
    {
        CmbResolutionPresets.Items.Clear();

        var presets = new (string label, string val)[]
        {
            ("📱 iPad 10.9\" Air / 10th-11th Gen — 1180 x 820 (@2x Logical / Fast)", "1180,820,60"),
            ("📱 iPad 10.9\" Air / 10th-11th Gen — 2360 x 1640 (Native 2K Retina)", "2360,1640,60"),
            ("🚀 iPad Pro 11\" M4 — 1210 x 834 (@2x Logical)", "1210,834,60"),
            ("🚀 iPad Pro 11\" M4 — 2420 x 1668 (Native Retina)", "2420,1668,60"),
            ("👑 iPad Pro 13\" M4 — 1376 x 1032 (@2x Logical)", "1376,1032,60"),
            ("👑 iPad Pro 13\" M4 — 2752 x 2064 (Native 3K Retina)", "2752,2064,60"),
            ("👑 iPad Pro 12.9\" / Air 13\" — 1366 x 1024 (@2x Logical)", "1366,1024,60"),
            ("👑 iPad Pro 12.9\" / Air 13\" — 2732 x 2048 (Native 3K Retina)", "2732,2048,60"),
            ("📱 iPad 10.2\" (7th-9th Gen) — 1080 x 810 (@2x Logical)", "1080,810,60"),
            ("📱 iPad 10.2\" (7th-9th Gen) — 2160 x 1620 (Native Retina)", "2160,1620,60"),
            ("📱 iPad mini 8.3\" (6th Gen / A17 Pro) — 1133 x 744 (@2x Logical)", "1133,744,60"),
            ("📱 iPad mini 8.3\" (6th Gen / A17 Pro) — 2266 x 1488 (Native Retina)", "2266,1488,60"),
            ("💻 1080p Full HD Standard — 1920 x 1080 @ 60Hz", "1920,1080,60"),
            ("💻 1440p QHD Standard — 2560 x 1440 @ 60Hz", "2560,1440,60"),
            ("💻 4K UHD Standard — 3840 x 2160 @ 60Hz", "3840,2160,60")
        };

        foreach (var (label, val) in presets)
        {
            var item = new ComboBoxItem { Content = label, Tag = val };
            CmbResolutionPresets.Items.Add(item);
        }

        CmbResolutionPresets.SelectedIndex = 1; // Default to 2360x1640 Native 2K
    }

    private void PopulateDisplayTargets()
    {
        var monitors = _manager.DisplayMonitors;
        if (monitors.Count == 0) monitors = DisplayResolutionManager.GetAllMonitorsDetailed();

        int prevSelectedIdx = CmbDisplayTarget.SelectedIndex;
        CmbDisplayTarget.Items.Clear();

        int defaultSelection = 0;

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var item = new ComboBoxItem
            {
                Content = $"{m.DisplayName} ({m.DeviceName}) — {m.Width}x{m.Height} @ {m.RefreshRate}Hz",
                Tag = m
            };
            CmbDisplayTarget.Items.Add(item);

            if (m.IsVirtual || (!m.IsPrimary && defaultSelection == 0))
            {
                defaultSelection = i;
            }
        }

        if (CmbDisplayTarget.Items.Count > 0)
        {
            CmbDisplayTarget.SelectedIndex = (prevSelectedIdx >= 0 && prevSelectedIdx < CmbDisplayTarget.Items.Count)
                ? prevSelectedIdx
                : defaultSelection;
        }
    }

    private void CmbDisplayTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbDisplayTarget.SelectedItem is ComboBoxItem item && item.Tag is DisplayMonitorInfo mon)
        {
            TxtActiveScreenHeader.Text = $"{mon.DisplayName} ({mon.DeviceName})";
            TxtScreenMetrics.Text = $"Resolution: {mon.Width} x {mon.Height} @ {mon.RefreshRate}Hz • Supported Modes: {mon.SupportedModes.Count}";
            TxtDisplayTypeBadge.Text = mon.IsPrimary ? "PRIMARY DISPLAY" : (mon.IsVirtual ? "VIRTUAL DISPLAY" : "SECONDARY DISPLAY");

            TxtCustomWidth.Text = mon.Width.ToString();
            TxtCustomHeight.Text = mon.Height.ToString();
            TxtCustomHz.Text = mon.RefreshRate.ToString();

            // Populate driver supported modes
            CmbSupportedModes.Items.Clear();
            foreach (var mode in mon.SupportedModes)
            {
                CmbSupportedModes.Items.Add(new ComboBoxItem
                {
                    Content = mode.DisplayText,
                    Tag = mode
                });
            }
            if (CmbSupportedModes.Items.Count > 0) CmbSupportedModes.SelectedIndex = 0;
        }
    }

    private void BtnApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (CmbResolutionPresets.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var parts = tag.Split(',');
            if (parts.Length >= 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && int.TryParse(parts[2], out int hz))
            {
                ApplyResolutionToSelectedMonitor(w, h, hz);
            }
        }
    }

    private void BtnApplyCustomResolution_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtCustomWidth.Text.Trim(), out int w) &&
            int.TryParse(TxtCustomHeight.Text.Trim(), out int h))
        {
            int.TryParse(TxtCustomHz.Text.Trim(), out int hz);
            if (hz <= 0) hz = 60;
            ApplyResolutionToSelectedMonitor(w, h, hz);
        }
        else
        {
            System.Windows.MessageBox.Show("Please enter valid numeric width and height values.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnApplyDriverMode_Click(object sender, RoutedEventArgs e)
    {
        if (CmbSupportedModes.SelectedItem is ComboBoxItem item && item.Tag is DisplayModeInfo mode)
        {
            ApplyResolutionToSelectedMonitor(mode.Width, mode.Height, mode.RefreshRate);
        }
    }

    private void ApplyResolutionToSelectedMonitor(int width, int height, int refreshRate)
    {
        if (CmbDisplayTarget.SelectedItem is ComboBoxItem item && item.Tag is DisplayMonitorInfo mon)
        {
            bool ok = DisplayResolutionManager.SetDisplayResolution(mon.DeviceName, width, height, refreshRate);
            if (ok)
            {
                System.Windows.MessageBox.Show($"Successfully applied resolution {width}x{height} @ {refreshRate}Hz to {mon.DeviceName}!", "Resolution Changed", MessageBoxButton.OK, MessageBoxImage.Information);
                Dispatcher.InvokeAsync(async () =>
                {
                    await RefreshDataAsync();
                    PopulateDisplayTargets();
                });
            }
            else
            {
                System.Windows.MessageBox.Show($"Could not apply {width}x{height} @ {refreshRate}Hz to {mon.DeviceName}. Ensure mode is supported by display driver.", "Resolution Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void BtnApplyDpi_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDpiScaling.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int percent))
        {
            int monitorIndex = CmbDisplayTarget.SelectedIndex >= 0 ? CmbDisplayTarget.SelectedIndex : 0;
            bool ok = WindowsDpiService.SetMonitorDpiPercent(monitorIndex, percent);
            if (ok)
            {
                System.Windows.MessageBox.Show($"Windows Display Scaling set to {percent}% for Display #{monitorIndex + 1}.", "DPI Scale Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                Dispatcher.InvokeAsync(async () =>
                {
                    await RefreshDataAsync();
                });
            }
            else
            {
                System.Windows.MessageBox.Show("Could not update display scaling. Please ensure you are running on Windows 10/11.", "DPI Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void BtnStartService_Click(object sender, RoutedEventArgs e)
    {
        var (started, message) = _manager.ProcessManager.StartInteractive();
        UpdateUi();
        if (started)
        {
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", message, Forms.ToolTipIcon.Info);
        }
        else
        {
            System.Windows.MessageBox.Show($"Could not start service:\n\n{message}\n\nTry clicking '🛡️ Start Elevated (Administrator)' instead.", "Service Start Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnStartElevated_Click(object sender, RoutedEventArgs e)
    {
        var (started, message) = _manager.ProcessManager.StartElevated();
        UpdateUi();
        if (started)
        {
            _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Streaming service launched as Administrator.", Forms.ToolTipIcon.Info);
        }
        else
        {
            System.Windows.MessageBox.Show($"Could not launch elevated service:\n\n{message}", "Elevation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnEnable3rdScreen_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = _manager.EnableVirtualDisplayAndStartService();
        Dispatcher.InvokeAsync(async () =>
        {
            await RefreshDataAsync();
            PopulateDisplayTargets();
        });
        UpdateUi();
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", msg, Forms.ToolTipIcon.Info);
    }

    private void BtnDisable3rdScreen_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = _manager.DisableVirtualDisplayAndStopService();
        Dispatcher.InvokeAsync(async () =>
        {
            await RefreshDataAsync();
            PopulateDisplayTargets();
        });
        UpdateUi();
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", msg, Forms.ToolTipIcon.Info);
    }

    private void BtnCompleteShutdown_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = _manager.CompleteShutdown();
        Dispatcher.InvokeAsync(async () =>
        {
            await RefreshDataAsync();
            PopulateDisplayTargets();
        });
        UpdateUi();
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", msg, Forms.ToolTipIcon.Info);
        System.Windows.MessageBox.Show(msg, "Complete Shutdown (Service & Driver)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnStopService_Click(object sender, RoutedEventArgs e)
    {
        _manager.ProcessManager.StopInteractive();
        UpdateUi();
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Streaming service stopped.", Forms.ToolTipIcon.Info);
    }

    private async void BtnRestartService_Click(object sender, RoutedEventArgs e) => await RestartServiceAction();

    private async Task RestartServiceAction()
    {
        _manager.ProcessManager.StopInteractive();
        await Task.Delay(1000);
        _manager.ProcessManager.StartInteractive();
        await RefreshDataAsync();
        UpdateUi();
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Streaming service restarted.", Forms.ToolTipIcon.Info);
    }

    private void BtnCleanStale_Click(object sender, RoutedEventArgs e)
    {
        _manager.ProcessManager.KillAllStaleProcesses();
        UpdateUi();
        System.Windows.MessageBox.Show("Terminated any stale or orphaned OpenWinSidecar, spacedesk, and FFmpeg processes.", "Clean Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestartDriverAction()
    {
        VirtualDisplayManager.RestartVirtualDisplayDriver();
        Dispatcher.InvokeAsync(async () =>
        {
            await RefreshDataAsync();
            PopulateDisplayTargets();
        });
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Virtual Display Driver restarted and displays re-extended.", Forms.ToolTipIcon.Info);
    }

    private void ForceExtendAction()
    {
        VirtualDisplayManager.EnableExtendMode();
        Dispatcher.InvokeAsync(async () =>
        {
            await RefreshDataAsync();
            PopulateDisplayTargets();
        });
        _notifyIcon?.ShowBalloonTip(2000, "OpenWinSidecar", "Desktop forced to Extend Mode across all monitors.", Forms.ToolTipIcon.Info);
    }

    private void BtnRestartDriver_Click(object sender, RoutedEventArgs e)
    {
        RestartDriverAction();
        System.Windows.MessageBox.Show("Virtual Display Driver restarted and desktop re-extended.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnForceExtend_Click(object sender, RoutedEventArgs e)
    {
        ForceExtendAction();
        System.Windows.MessageBox.Show("Windows desktop forced to Extend Mode across all monitors.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnForceMirror_Click(object sender, RoutedEventArgs e)
    {
        VirtualDisplayManager.EnableMirrorMode();
        System.Windows.MessageBox.Show("Windows desktop set to Mirror / Clone Mode.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
    {
        bool ok = VirtualDisplayManager.InstallVirtualDisplayDriver();
        if (ok)
        {
            System.Windows.MessageBox.Show("IddCx Virtual Display Driver installed successfully!", "Driver Installed", MessageBoxButton.OK, MessageBoxImage.Information);
            Dispatcher.InvokeAsync(async () =>
            {
                await RefreshDataAsync();
                PopulateDisplayTargets();
            });
        }
        else
        {
            System.Windows.MessageBox.Show("Could not install driver. Please make sure to run as Administrator.", "Install Driver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            TxtLastUpdated.Text = $"Error: {ex.Message}";
        }
    }

    private void UpdateUi()
    {
        bool isRunning = _manager.ProcessManager.IsProcessRunning;

        StatusDot.Fill = isRunning ? (SolidColorBrush)FindResource("SuccessGreen") : (SolidColorBrush)FindResource("DangerRed");
        StatusLabel.Text = isRunning ? "Service Active (Direct3D 11 DXGI / HEVC QSV)" : "Service Stopped";

        int? pid = _manager.ProcessManager.ProcessId;
        double mem = _manager.ProcessManager.MemoryUsageMb;

        TxtPid.Text = pid.HasValue ? pid.Value.ToString() : "N/A";
        TxtMemory.Text = isRunning ? $"{mem:F1} MB" : "0 MB";

        ProcessMetaLabel.Text = isRunning ? $"[PID: {pid} • {mem:F1} MB]" : "";

        if (isRunning)
        {
            ServiceBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 6, 78, 59));
            TxtServiceBadge.Text = "STREAMING ACTIVE";
            TxtServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 52, 211, 153));
        }
        else
        {
            ServiceBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 127, 29, 29));
            TxtServiceBadge.Text = "SERVICE OFFLINE";
            TxtServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 248, 113, 113));
        }

        bool is3rdScreenActive = _manager.IsVirtualDisplayActive;
        if (is3rdScreenActive)
        {
            Border3rdScreenBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 6, 78, 59));
            Txt3rdScreenBadge.Text = isRunning ? "3RD SCREEN: ACTIVE & STREAMING" : "3RD SCREEN: ACTIVE (IDLE)";
            Txt3rdScreenBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 52, 211, 153));
        }
        else
        {
            Border3rdScreenBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 39, 39, 42));
            Txt3rdScreenBadge.Text = "3RD SCREEN: DISABLED / OFFLINE";
            Txt3rdScreenBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 161, 161, 170));
        }

        ClientsList.ItemsSource = null;
        ClientsList.ItemsSource = _manager.Clients;

        EndpointsList.ItemsSource = null;
        EndpointsList.ItemsSource = _manager.NetworkEndpoints;

        TxtLastUpdated.Text = $"Last refreshed: {DateTime.Now:HH:mm:ss} | {_manager.DisplayMonitors.Count} monitor(s) detected";
    }

    private void LoadSettingsIntoUi()
    {
        var s = _manager.CurrentSettings;
        ChkAutoStart.IsChecked = s.ServerStartType == ServerStartMode.On;
        ChkIosUsb.IsChecked = s.IosUsbControlEnabled;
        TxtPassword.Text = s.EncryptionPassword;
    }

    private void OpenWebViewer()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://localhost:8080",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e) => OpenWebViewer();

    private void BtnCopyEndpointUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string ip)
        {
            string url = $"http://{ip}:8080";
            System.Windows.Clipboard.SetText(url);
            _notifyIcon?.ShowBalloonTip(1500, "URL Copied", $"Copied {url} to clipboard! Paste into iPad Safari.", Forms.ToolTipIcon.Info);
        }
    }

    private void BtnEnableAdb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb.exe",
                Arguments = "reverse tcp:28252 tcp:28252",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            System.Windows.MessageBox.Show("ADB USB port reverse forwarding enabled!\n\nConnect Android via USB cable and navigate to http://localhost:28252.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            System.Windows.MessageBox.Show("Could not find adb.exe. Ensure Android SDK Platform Tools is in PATH.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        TxtLogsConsole.Clear();
    }

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLogsConsole.Text))
        {
            System.Windows.Clipboard.SetText(TxtLogsConsole.Text);
            _notifyIcon?.ShowBalloonTip(1500, "Logs Copied", "Console logs copied to clipboard.", Forms.ToolTipIcon.Info);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
        PopulateDisplayTargets();
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = new SpacedeskSettings
        {
            ServerStartType = ChkAutoStart.IsChecked == true ? ServerStartMode.On : ServerStartMode.Off,
            IosUsbControlEnabled = ChkIosUsb.IsChecked == true,
            EncryptionPassword = TxtPassword.Text ?? string.Empty
        };

        var saved = _manager.RegistryManager.SaveSettings(s);
        if (saved)
        {
            System.Windows.MessageBox.Show("Configuration saved successfully.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show("Could not save settings. Please run as Administrator.", "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChkAutoPoll_Checked(object sender, RoutedEventArgs e) => _timer.Start();
    private void ChkAutoPoll_Unchecked(object sender, RoutedEventArgs e) => _timer.Stop();
}

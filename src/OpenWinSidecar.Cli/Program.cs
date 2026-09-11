using OpenWinSidecar.Core.Models;
using OpenWinSidecar.Core.Services;

var manager = new SidecarManager();
await manager.RefreshStateAsync();

if (args.Length == 0)
{
    PrintHeader();
    PrintUsage();
    return;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "status":
        await ShowStatusAsync(manager);
        break;

    case "screen-on":
    case "screen_on":
    case "enable-screen":
    case "screen" when args.Length > 1 && args[1].ToLowerInvariant() == "on":
        Enable3rdScreen(manager);
        break;

    case "screen-off":
    case "screen_off":
    case "disable-screen":
    case "screen" when args.Length > 1 && args[1].ToLowerInvariant() == "off":
        Disable3rdScreen(manager);
        break;

    case "shutdown":
    case "shutdown-all":
        CompleteShutdown(manager);
        break;

    case "start":
        await StartServiceAsync(manager);
        break;

    case "stop":
        await StopServiceAsync(manager);
        break;

    case "restart":
        await RestartServiceAsync(manager);
        break;

    case "driver" when args.Length > 1 && args[1].ToLowerInvariant() == "restart":
    case "restart-driver":
        RestartDriver();
        break;

    case "clients":
        ShowClients(manager);
        break;

    case "network":
    case "net":
        ShowNetwork(manager);
        break;

    case "config":
        await HandleConfigAsync(manager, args.Skip(1).ToArray());
        break;

    case "help":
    case "--help":
    case "-h":
        PrintHeader();
        PrintUsage();
        break;

    default:
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown command: {command}");
        Console.ResetColor();
        PrintUsage();
        break;
}

static void PrintHeader()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("==================================================");
    Console.WriteLine("        OpenWinSidecar CLI (.NET 10 LTS)          ");
    Console.WriteLine("==================================================");
    Console.ResetColor();
}

static void PrintUsage()
{
    Console.WriteLine("Usage: openwinsidecar <command> [options]\n");
    Console.WriteLine("Commands:");
    Console.WriteLine("  screen on               Enable Virtual Display (3rd screen) & start streaming");
    Console.WriteLine("  screen off              Disable Virtual Display (3rd screen) & stop streaming");
    Console.WriteLine("  shutdown                Complete shutdown of streaming service and virtual display driver");
    Console.WriteLine("  status                  Show service, driver, network, and connected clients status");
    Console.WriteLine("  start                   Start the OpenWinSidecar streaming service");
    Console.WriteLine("  stop                    Stop the OpenWinSidecar streaming service");
    Console.WriteLine("  restart                 Restart the OpenWinSidecar streaming service");
    Console.WriteLine("  driver restart          Restart the Virtual Display Driver and re-extend desktop");
    Console.WriteLine("  clients                 List all client displays and configuration");
    Console.WriteLine("  network                 List active network adapters and IP endpoints");
    Console.WriteLine("  config                  Show current configuration");
    Console.WriteLine("  config --autostart <on|off>       Toggle automatic server start");
    Console.WriteLine("  config --ios-usb <on|off>         Toggle iOS USB connection support");
    Console.WriteLine("  config --password <pwd>           Set encryption password");
    Console.WriteLine("  config --delay <sec>              Set Video Wall disconnect delay");
    Console.WriteLine();
}

static async Task ShowStatusAsync(SidecarManager manager)
{
    await manager.RefreshStateAsync();
    PrintHeader();

    Console.Write("Streaming Service: ");
    if (manager.CurrentServiceState == SidecarServiceState.Running)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[ RUNNING ]");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ {manager.CurrentServiceState.ToString().ToUpper()} ]");
    }
    Console.ResetColor();

    Console.WriteLine($"Server Auto-Start: {(manager.CurrentSettings.ServerStartType == ServerStartMode.On ? "ON" : "OFF")}");
    Console.WriteLine($"iOS USB Support:   {(manager.CurrentSettings.IosUsbControlEnabled ? "Enabled" : "Disabled")}");
    Console.WriteLine($"Encryption:        {(string.IsNullOrEmpty(manager.CurrentSettings.EncryptionPassword) ? "Disabled" : "Protected")}");
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Connected Clients ({manager.Clients.Count(c => c.IsConnected)} active):");
    Console.ResetColor();
    if (manager.Clients.Count == 0)
    {
        Console.WriteLine("  (No client displays found)");
    }
    else
    {
        foreach (var client in manager.Clients)
        {
            var icon = client.IsConnected ? "[ACTIVE]" : "[OFFLINE]";
            var color = client.IsConnected ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.ForegroundColor = color;
            Console.WriteLine($"  * {icon} {client.DisplayName} - {client.ResolutionText} @ {client.PositionText} (Quality: {client.CompressionQuality}%, {client.EncodingText})");
            Console.ResetColor();
        }
    }
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Network Endpoints (for iPad Safari connection):");
    Console.ResetColor();
    foreach (var net in manager.NetworkEndpoints)
    {
        Console.WriteLine($"  * {net.Name} ({net.InterfaceType}): {string.Join(", ", net.IpAddresses)}");
    }
    Console.WriteLine();
}

static void Enable3rdScreen(SidecarManager manager)
{
    Console.WriteLine("Enabling Virtual 3rd Screen driver and starting streaming service...");
    var (ok, msg) = manager.EnableVirtualDisplayAndStartService();
    if (ok)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SUCCESS] {msg}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[RESULT] {msg}");
    }
    Console.ResetColor();
}

static void Disable3rdScreen(SidecarManager manager)
{
    Console.WriteLine("Disabling Virtual 3rd Screen driver and stopping streaming service...");
    var (ok, msg) = manager.DisableVirtualDisplayAndStopService();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[SUCCESS] {msg}");
    Console.ResetColor();
}

static void CompleteShutdown(SidecarManager manager)
{
    Console.WriteLine("Performing complete shutdown of streaming service and virtual display driver...");
    var (ok, msg) = manager.CompleteShutdown();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[SUCCESS] {msg}");
    Console.ResetColor();
}

static void RestartDriver()
{
    Console.WriteLine("Restarting Virtual Display Driver and re-extending desktop...");
    bool ok = VirtualDisplayManager.RestartVirtualDisplayDriver();
    if (ok)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[SUCCESS] Virtual Display Driver restarted successfully.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[WARNING] Driver restart command dispatched.");
    }
    Console.ResetColor();
}

static async Task StartServiceAsync(SidecarManager manager)
{
    Console.WriteLine("Starting OpenWinSidecar service...");
    var (started, msg) = manager.ProcessManager.StartInteractive();
    if (started)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SUCCESS] {msg}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAILED] {msg}");
    }
    Console.ResetColor();
}

static async Task StopServiceAsync(SidecarManager manager)
{
    Console.WriteLine("Stopping OpenWinSidecar service...");
    var ok = await manager.ServiceController.StopServiceAsync();
    if (ok)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Service stopped successfully.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed to stop service. Please run as Administrator.");
    }
    Console.ResetColor();
}

static async Task RestartServiceAsync(SidecarManager manager)
{
    Console.WriteLine("Restarting OpenWinSidecar service...");
    var ok = await manager.ServiceController.RestartServiceAsync();
    if (ok)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Service restarted successfully.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed to restart service. Please run as Administrator.");
    }
    Console.ResetColor();
}

static void ShowClients(SidecarManager manager)
{
    PrintHeader();
    Console.WriteLine($"Total Known Clients: {manager.Clients.Count}");
    Console.WriteLine(new string('-', 70));

    foreach (var client in manager.Clients)
    {
        Console.ForegroundColor = client.IsConnected ? ConsoleColor.Green : ConsoleColor.White;
        Console.WriteLine($"Device GUID:   {client.DeviceGuid}");
        Console.WriteLine($"Status:        {client.StatusText}");
        Console.WriteLine($"Resolution:    {client.ResolutionText}");
        Console.WriteLine($"Position:      {client.PositionText}");
        Console.WriteLine($"Compression:   {client.CompressionQuality}%");
        Console.WriteLine($"Encoding:      {client.EncodingText}");
        if (client.IsConnected)
        {
            Console.WriteLine($"Target ID:     {client.VideoPresentTargetId}");
            Console.WriteLine($"Adapter LUID:  {client.AdapterLuid}");
        }
        Console.ResetColor();
        Console.WriteLine(new string('-', 70));
    }
}

static void ShowNetwork(SidecarManager manager)
{
    PrintHeader();
    Console.WriteLine("Active Network Interfaces:\n");
    foreach (var net in manager.NetworkEndpoints)
    {
        Console.WriteLine($"Adapter:     {net.Name}");
        Console.WriteLine($"Description: {net.Description}");
        Console.WriteLine($"Type:        {net.InterfaceType}");
        Console.WriteLine($"Status:      {net.Status}");
        Console.WriteLine($"IPs:         {string.Join(", ", net.IpAddresses)}");
        Console.WriteLine(new string('-', 50));
    }
}

static async Task HandleConfigAsync(SidecarManager manager, string[] args)
{
    var settings = manager.RegistryManager.GetSettings();

    if (args.Length == 0)
    {
        PrintHeader();
        Console.WriteLine("Current OpenWinSidecar Configuration:");
        Console.WriteLine($"  Server Start Mode:          {settings.ServerStartType}");
        Console.WriteLine($"  iOS USB Control:            {(settings.IosUsbControlEnabled ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Encryption Password:        {settings.EncryptionPassword}");
        Console.WriteLine($"  VideoWall Disconnect Delay: {settings.VideoWallDisconnectDelay}s");
        return;
    }

    for (int i = 0; i < args.Length; i++)
    {
        var flag = args[i].ToLowerInvariant();
        if (flag == "--autostart" && i + 1 < args.Length)
        {
            var val = args[++i].ToLowerInvariant();
            settings.ServerStartType = (val == "on" || val == "1" || val == "true") ? ServerStartMode.On : ServerStartMode.Off;
        }
        else if (flag == "--ios-usb" && i + 1 < args.Length)
        {
            var val = args[++i].ToLowerInvariant();
            settings.IosUsbControlEnabled = (val == "on" || val == "1" || val == "true");
        }
        else if (flag == "--password" && i + 1 < args.Length)
        {
            settings.EncryptionPassword = args[++i];
        }
        else if (flag == "--delay" && i + 1 < args.Length && int.TryParse(args[++i], out var d))
        {
            settings.VideoWallDisconnectDelay = d;
        }
    }

    var success = manager.RegistryManager.SaveSettings(settings);
    if (success)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Configuration saved successfully.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed to save configuration. Please run with Administrator privileges.");
    }
    Console.ResetColor();
}

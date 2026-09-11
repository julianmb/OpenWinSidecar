using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenWinSidecar.Service.Protocol;

public class SidecarDiscoveryServer : IDisposable
{
    private readonly int _port;
    private UdpClient? _udpListener;
    private CancellationTokenSource? _cts;

    public SidecarDiscoveryServer(int port = 28252)
    {
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        try
        {
            _udpListener = new UdpClient();
            _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

            Console.WriteLine($"[Discovery] UDP Discovery Listener active on port {_port}");

            while (!token.IsCancellationRequested)
            {
                var result = await _udpListener.ReceiveAsync(token);
                var requestStr = Encoding.UTF8.GetString(result.Buffer);

                // Sidecar discovery query or beacon
                var hostName = Environment.MachineName;
                var responsePayload = $"SIDECAR_SERVER;NAME={hostName};PORT={_port};VER=1.0;DISPLAYS=1\n";
                var responseBytes = Encoding.UTF8.GetBytes(responsePayload);

                await _udpListener.SendAsync(responseBytes, responseBytes.Length, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Listener error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpListener?.Close();
        _udpListener?.Dispose();
    }

    public void Dispose() => Stop();
}

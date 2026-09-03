using System.Diagnostics;
using System.IO;

namespace OpenWinSidecar.Service.Encoders;

/// <summary>
/// Ultra-Low Latency Hardware HEVC / H.265 Stream Encoder powered by Intel Arc Quick Sync Video (QSV).
/// Encodes raw 60 FPS BGRA frames into Annex-B NAL units in ~1-3ms on GPU.
/// </summary>
public sealed class HevcQsvStreamEncoder : IDisposable
{
    private Process? _ffmpegProc;
    private Stream? _stdin;
    private Stream? _stdout;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _width;
    private int _height;
    private int _bitrateKbps;
    private readonly Action<byte[], bool> _onPacketEncoded;
    private bool _isInitialized;
    private readonly object _syncLock = new();

    public bool IsActive => _isInitialized && _ffmpegProc != null && !_ffmpegProc.HasExited;

    public HevcQsvStreamEncoder(Action<byte[], bool> onPacketEncoded)
    {
        _onPacketEncoded = onPacketEncoded;
    }

    public static string? FindFfmpegExecutable()
    {
        // 1. Check PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "ffmpeg.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(1000);
                if (!string.IsNullOrEmpty(line) && File.Exists(line.Trim())) return line.Trim();
            }
        }
        catch { }

        // 2. Check standard WinGet paths
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetDir))
        {
            var matches = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories);
            if (matches.Length > 0) return matches[0];
        }

        return "ffmpeg.exe";
    }

    public bool Initialize(int width, int height, int bitrateKbps = 4000)
    {
        lock (_syncLock)
        {
            Shutdown();

            _width = width;
            _height = height;
            _bitrateKbps = bitrateKbps;

            var ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                Console.WriteLine("[HEVC QSV] FFmpeg executable not found.");
                return false;
            }

            try
            {
                // Low-latency Intel QSV hardware encoder arguments (0 B-frames, Annex-B output)
                var args = $"-hide_banner -loglevel error -init_hw_device qsv=hw -filter_hw_device hw " +
                           $"-f rawvideo -pix_fmt bgra -s {_width}x{_height} -r 60 -i pipe:0 " +
                           $"-c:v hevc_qsv -preset veryfast -b:v {_bitrateKbps}k -maxrate {(_bitrateKbps * 3 / 2)}k -bufsize {(_bitrateKbps / 4)}k " +
                           $"-g 60 -bf 0 -an -f hevc pipe:1";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _ffmpegProc = Process.Start(psi);
                if (_ffmpegProc == null) return false;

                _stdin = _ffmpegProc.StandardInput.BaseStream;
                _stdout = _ffmpegProc.StandardOutput.BaseStream;
                _cts = new CancellationTokenSource();

                _readTask = Task.Run(() => ReadNalStreamLoopAsync(_stdout, _cts.Token));

                _isInitialized = true;
                Console.WriteLine($"[HEVC QSV] Hardware HEVC Encoder started: {_width}x{_height} @ {_bitrateKbps} kbps");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEVC QSV] Init error: {ex.Message}");
                Shutdown();
                return false;
            }
        }
    }

    public bool PushRawFrame(byte[] rawBgra)
    {
        if (!_isInitialized || _stdin == null) return false;

        try
        {
            _stdin.Write(rawBgra, 0, rawBgra.Length);
            _stdin.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReadNalStreamLoopAsync(Stream stdout, CancellationToken token)
    {
        var buffer = new byte[65536];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead <= 0) break;

                // Deliver compressed HEVC NAL chunk
                var packet = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, packet, 0, bytesRead);

                // Detect HEVC Keyframe / IRAP (IDR / CRA / BLA NAL units: 16-23)
                bool isKeyframe = DetectHevcKeyframe(packet);
                _onPacketEncoded(packet, isKeyframe);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[HEVC QSV] Stream reader error: {ex.Message}");
        }
    }

    private static bool DetectHevcKeyframe(byte[] nalData)
    {
        for (int i = 0; i < nalData.Length - 5; i++)
        {
            if (nalData[i] == 0 && nalData[i + 1] == 0 && (nalData[i + 2] == 1 || (nalData[i + 2] == 0 && nalData[i + 3] == 1)))
            {
                int nalStart = (nalData[i + 2] == 1) ? i + 3 : i + 4;
                if (nalStart < nalData.Length)
                {
                    int nalType = (nalData[nalStart] >> 1) & 0x3F;
                    // NAL types 16..21 are IDR/CRA keyframes, 32=VPS, 33=SPS, 34=PPS
                    if (nalType >= 16 && nalType <= 34) return true;
                }
            }
        }
        return false;
    }

    public void Shutdown()
    {
        _isInitialized = false;
        _cts?.Cancel();

        try
        {
            _stdin?.Close();
            _stdin?.Dispose();
            _stdin = null;
        }
        catch { }

        try
        {
            if (_ffmpegProc != null && !_ffmpegProc.HasExited)
            {
                _ffmpegProc.Kill();
                _ffmpegProc.WaitForExit(1000);
            }
            _ffmpegProc?.Dispose();
            _ffmpegProc = null;
        }
        catch { }
    }

    public void Dispose() => Shutdown();
}

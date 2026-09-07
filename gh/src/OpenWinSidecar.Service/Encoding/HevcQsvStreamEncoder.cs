using System.Diagnostics;
using System.IO;

namespace OpenWinSidecar.Service.Encoders;

/// <summary>
/// Ultra-low latency hardware HEVC (H.265) encoder powered by Intel Quick Sync via FFmpeg.
///
/// Output framing: FFmpeg emits a raw Annex-B byte stream. This class parses it into NAL
/// units and re-frames it as access-unit-aligned chunks of 4-byte length-prefixed NALs,
/// plus a one-shot HEVCDecoderConfigurationRecord ("hvcC") and the matching codec string —
/// exactly what Safari's WebCodecs VideoDecoder expects via the `description` field.
/// (Chrome accepts bare Annex-B; Safari wants the out-of-band description.)
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
    private readonly Action<string, byte[]>? _onDescriptionReady;
    private bool _isInitialized;
    private bool _counted; // only instances that reached a successful Initialize decrement on Shutdown
    private readonly object _syncLock = new();

    // ---- Annex-B parser state (single reader thread) ----
    private byte[] _acc = new byte[1 << 17];
    private int _accLen;
    private bool _inNalContinuation; // retained buffer bytes are the middle of an unfinished NAL

    // ---- HEVC parameter sets + access-unit assembly ----
    private byte[]? _vps, _sps, _pps;
    private readonly List<byte[]> _pendingNonVcl = new();   // length-prefixed VPS/SPS/PPS/SEI awaiting the next picture
    private readonly List<byte[]> _auNals = new();          // length-prefixed VCL NALs of the current picture
    private bool _auHasKey;
    private bool _descriptionSent;
    private readonly object _bitstreamLock = new();
    private System.Threading.Timer? _auFlushTimer;
    private int _nalCount, _vclCount, _auCount, _packetCount;
    private long _lastDataTickMs = Environment.TickCount64;

    public bool IsActive => _isInitialized && _ffmpegProc != null && !_ffmpegProc.HasExited;

    /// <summary>Live count of running QSV encoder processes across all clients.</summary>
    public static int ActiveEncoderCount => _activeEncoders;
    private static int _activeEncoders;
    private const int MaxEncoders = 4; // each is an ffmpeg process with QSV GPU surfaces behind it

    public HevcQsvStreamEncoder(Action<byte[], bool> onPacketEncoded, Action<string, byte[]>? onDescriptionReady = null)
    {
        _onPacketEncoded = onPacketEncoded;
        _onDescriptionReady = onDescriptionReady;
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

            // Reset bitstream state — a fresh encoder instance streams fresh parameter sets
            _accLen = 0;
            _inNalContinuation = false;
            _vps = _sps = _pps = null;
            _pendingNonVcl.Clear();
            _auNals.Clear();
            _auHasKey = false;
            _descriptionSent = false;

            var ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                Console.WriteLine("[HEVC QSV] FFmpeg executable not found.");
                return false;
            }

            // Each encoder is an ffmpeg process holding QSV GPU surfaces — cap the total
            if (_activeEncoders >= MaxEncoders)
            {
                Console.WriteLine($"[HEVC QSV] Encoder limit reached ({_activeEncoders}/{MaxEncoders}) — refusing new instance.");
                return false;
            }

            try
            {
                // Low-latency Intel QSV hardware encoder:
                //   format=nv12,hwupload   BGRA→QSV surfaces default to 4:4:4 (Rext profile),
                //                          which iPads software-decode — NV12 keeps Main profile
                //                          hardware decode on every Apple device.
                //                          (extra_hw_frames breaks the D3D11 pool on this driver.)
                //   -preset ultrafast      minimal GPU time per frame — the encoder shares the
                //                          GPU with Desktop Duplication capture, and heavy
                //                          presets stalled the producer loop (60→37 ticks/s)
                //   -bf 0                  no B-frames (output order == input order)
                //   -g 240                 4s GOP — every client has its own encoder instance and
                //                          therefore joins on an IDR, so frequent keyframes only
                //                          cost quality
                //   async_depth default     1 forced a synchronous GPU wait per frame (part of the
                //                          same capture stall); QSV's small default buffer keeps
                //                          added latency in the single-frame range
                var args = $"-hide_banner -loglevel error -init_hw_device qsv=hw -filter_hw_device hw " +
                           $"-f rawvideo -pix_fmt bgra -s {_width}x{_height} -r 60 -i pipe:0 " +
                           $"-vf format=nv12,hwupload " +
                           $"-c:v hevc_qsv -preset ultrafast -b:v {_bitrateKbps}k -maxrate {(_bitrateKbps * 3 / 2)}k -bufsize {(_bitrateKbps / 4)}k " +
                           $"-g 240 -bf 0 -an -f hevc pipe:1";

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

                // Surface FFmpeg's stderr — with -loglevel error this only carries real failures,
                // and an unread stderr pipe would otherwise silently block or hide crashes
                _ffmpegProc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine("[HEVC QSV][ff] " + e.Data);
                };
                _ffmpegProc.BeginErrorReadLine();

                _stdin = _ffmpegProc.StandardInput.BaseStream;
                _stdout = _ffmpegProc.StandardOutput.BaseStream;
                _cts = new CancellationTokenSource();

                _readTask = Task.Run(() => ReadNalStreamLoopAsync(_stdout, _cts.Token));

                // The encoder writes access units atomically, and a NAL's end is only knowable
                // when the next start code arrives. When the stream goes silent (idle-skip starves
                // the encoder), force-complete the accumulated NAL and flush the pending AU.
                _auFlushTimer = new System.Threading.Timer(
                    _ =>
                    {
                        lock (_bitstreamLock)
                        {
                            long silentMs = Environment.TickCount64 - _lastDataTickMs;
                            if (_accLen > 0 && silentMs >= 120)
                            {
                                var completeNal = new byte[_accLen];
                                Buffer.BlockCopy(_acc, 0, completeNal, 0, _accLen);
                                _accLen = 0;
                                _inNalContinuation = false;
                                OnNal(completeNal);
                            }
                            FlushAccessUnit();
                        }
                    },
                    null, TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(33));

                _isInitialized = true;
                _counted = true;
                Interlocked.Increment(ref _activeEncoders);
                Console.WriteLine($"[HEVC QSV] Hardware HEVC Encoder started: {_width}x{_height} @ {_bitrateKbps} kbps, GOP 240");
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
        catch (Exception ex)
        {
            if (_pushFailLogged == 0 && Interlocked.Exchange(ref _pushFailLogged, 1) == 0)
            {
                string stderrSnapshot = "";
                try { stderrSnapshot = _ffmpegProc == null ? "" : _ffmpegProc.StandardError.ReadToEnd(); } catch { }
                Console.WriteLine($"[HEVC QSV] PushRawFrame failed: {ex.Message} | exited={_ffmpegProc?.HasExited} exitCode={(_ffmpegProc?.HasExited == true ? _ffmpegProc.ExitCode : -1)} | stderr: {stderrSnapshot}");
            }
            return false;
        }
    }

    private int _pushFailLogged;

    private async Task ReadNalStreamLoopAsync(Stream stdout, CancellationToken token)
    {
        var buffer = new byte[65536];
        int reads = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead <= 0)
                {
                    // EOF: FFmpeg's stdout closed — it exited (crash or kill)
                    Console.WriteLine($"[HEVC QSV] stdout closed. reads={reads} NALs={_nalCount} vcl={_vclCount} aus={_auCount} sent={_packetCount} processExited={_ffmpegProc?.HasExited}");
                    break;
                }

                reads++;
                if (reads <= 5)
                    Console.WriteLine($"[HEVC QSV] read #{reads}: {bytesRead} bytes, first={BitConverter.ToString(buffer, 0, Math.Min(12, bytesRead))}");

                _lastDataTickMs = Environment.TickCount64;
                ParseAnnexB(buffer, bytesRead);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[HEVC QSV] Stream reader error: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Annex-B parsing and WebCodecs re-framing
    // ------------------------------------------------------------------

    private void ParseAnnexB(byte[] chunk, int len)
    {
        lock (_bitstreamLock)
        {
            if (_accLen + len > _acc.Length)
            {
                int newSize = Math.Max(_acc.Length * 2, _accLen + len);
                var grown = new byte[newSize];
                Buffer.BlockCopy(_acc, 0, grown, 0, _accLen);
                _acc = grown;
            }

            Buffer.BlockCopy(chunk, 0, _acc, _accLen, len);
            _accLen += len;

            // When previous passes retained the beginning of an unfinished NAL, that data
            // starts this pass's NAL — without this, any NAL spanning two reads is discarded
            int nalStart = _inNalContinuation ? 0 : -1;
            int i = 0;
            while (i + 2 < _accLen)
            {
                if (_acc[i] == 0 && _acc[i + 1] == 0 && _acc[i + 2] == 1)
                {
                    int codeLen = (i > 0 && _acc[i - 1] == 0) ? 4 : 3;
                    int codeStart = i - (codeLen - 3);

                    if (nalStart >= 0)
                    {
                        int nalEnd = codeStart;
                        while (nalEnd > nalStart && _acc[nalEnd - 1] == 0) nalEnd--; // trailing_zero_8bits
                        if (nalEnd > nalStart)
                        {
                            var nal = new byte[nalEnd - nalStart];
                            Buffer.BlockCopy(_acc, nalStart, nal, 0, nalEnd - nalStart);
                            OnNal(nal);
                        }
                    }

                    nalStart = i + 3;
                    i += 3;
                }
                else
                {
                    i++;
                }
            }

            if (nalStart > 0)
            {
                // Keep the unfinished NAL's data for the next pass
                Buffer.BlockCopy(_acc, nalStart, _acc, 0, _accLen - nalStart);
                _accLen -= nalStart;
                _inNalContinuation = true;
            }
            else if (nalStart == 0)
            {
                _inNalContinuation = true; // still inside a NAL, no complete start code found
            }
            else if (_accLen > 4)
            {
                // No start code at all and not inside a NAL — leading garbage; a partial
                // start code may hide in the last bytes, so keep only those
                Buffer.BlockCopy(_acc, _accLen - 3, _acc, 0, 3);
                _accLen = 3;
                _inNalContinuation = false;
            }
        }
    }

    private void OnNal(byte[] nal)
    {
        int type = (nal[0] >> 1) & 0x3F;
        _nalCount++;

        if (type == 32) { _vps = nal; _pendingNonVcl.Add(LengthPrefixed(nal)); TryEmitDescription(); }
        else if (type == 33) { _sps = nal; _pendingNonVcl.Add(LengthPrefixed(nal)); TryEmitDescription(); }
        else if (type == 34) { _pps = nal; _pendingNonVcl.Add(LengthPrefixed(nal)); TryEmitDescription(); }
        else if (type == 39 || type == 40) { _pendingNonVcl.Add(LengthPrefixed(nal)); } // SEI
        else if (type <= 31)
        {
            // VCL NAL — first_slice_segment_in_pic_flag is the top bit of the byte after the 2-byte NAL header
            bool firstSlice = nal.Length > 2 && (nal[2] & 0x80) != 0;
            if (firstSlice && _auNals.Count > 0) FlushAccessUnit();

            if (_auNals.Count == 0)
                Console.WriteLine($"[HEVC QSV] first VCL NAL: type={type} len={nal.Length} bytes={BitConverter.ToString(nal, 0, Math.Min(8, nal.Length))}");

            _vclCount++;
            _auNals.Add(LengthPrefixed(nal));
            if (type >= 16 && type <= 21) _auHasKey = true; // BLA/IDR/CRA
        }
        else
        {
            _pendingNonVcl.Add(LengthPrefixed(nal));
        }

        if (_nalCount % 600 == 0)
            Console.WriteLine($"[HEVC QSV] NALs={_nalCount} vcl={_vclCount} aus={_auCount} sent={_packetCount}");
    }

    private void FlushAccessUnit()
    {
        TryEmitDescription();

        if (_auNals.Count == 0) return;

        int total = 0;
        foreach (var n in _pendingNonVcl) total += n.Length;
        foreach (var n in _auNals) total += n.Length;

        var payload = new byte[total];
        int offset = 0;
        foreach (var n in _pendingNonVcl) { Buffer.BlockCopy(n, 0, payload, offset, n.Length); offset += n.Length; }
        foreach (var n in _auNals) { Buffer.BlockCopy(n, 0, payload, offset, n.Length); offset += n.Length; }

        _pendingNonVcl.Clear();
        _auNals.Clear();
        bool key = _auHasKey;
        _auHasKey = false;

        _auCount++;
        if (_auCount == 1)
            Console.WriteLine($"[HEVC QSV] first access unit: {payload.Length} bytes, key={key}");

        _onPacketEncoded(payload, key);
    }

    private void TryEmitDescription()
    {
        if (_descriptionSent || _sps == null || _pps == null) return;
        _descriptionSent = true;

        if (_onDescriptionReady == null) return;

        var record = BuildHvcC(_vps, _sps, _pps);
        var codecString = BuildCodecString();
        Console.WriteLine($"[HEVC QSV] hvcC description ready: {codecString} ({record.Length} bytes) sps={BitConverter.ToString(_sps, 0, Math.Min(20, _sps.Length))}");
        _onDescriptionReady(codecString, record);
    }

    private static byte[] LengthPrefixed(byte[] nal)
    {
        var outBuf = new byte[nal.Length + 4];
        outBuf[0] = (byte)(nal.Length >> 24);
        outBuf[1] = (byte)(nal.Length >> 16);
        outBuf[2] = (byte)(nal.Length >> 8);
        outBuf[3] = (byte)nal.Length;
        Buffer.BlockCopy(nal, 0, outBuf, 4, nal.Length);
        return outBuf;
    }

    /// <summary>
    /// Builds an HEVCDecoderConfigurationRecord (ISO 14496-15) from the stream's
    /// VPS/SPS/PPS. The general profile/compat/constraint/level fields are copied
    /// from the SPS's profile_tier_level structure — read from the emulation-prevention-
    /// stripped RBSP (the zero-heavy constraint region triggers 00 00 03 escaping).
    /// The hvcC itself stores the original wire-format NAL bytes.
    /// </summary>
    private static byte[] BuildHvcC(byte[]? vps, byte[] sps, byte[] pps)
    {
        using var ms = new MemoryStream();

        ms.WriteByte(1); // configurationVersion

        // general profile/tier/level copied from the unescaped SPS RBSP:
        // [0..1] NAL header, [2] vpsId/subLayers/nesting, [3..14] profile_tier_level
        byte[] rbsp = RemoveEmulationPrevention(sps);
        var general = new byte[12];
        if (rbsp.Length >= 15)
            Buffer.BlockCopy(rbsp, 3, general, 0, 12);
        else
            general = new byte[] { 0x01, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x78 };
        ms.Write(general, 0, 12);

        ms.WriteByte(0xF0); ms.WriteByte(0x00); // min_spatial_segmentation_idc (reserved 1111)
        ms.WriteByte(0xFC);                     // parallelismType (reserved 111111, single)
        ms.WriteByte(0xFD);                     // chromaFormatIdc = 1 (4:2:0)
        ms.WriteByte(0xF8);                     // bitDepthLumaMinus8 = 0
        ms.WriteByte(0xF8);                     // bitDepthChromaMinus8 = 0
        ms.WriteByte(0x00); ms.WriteByte(0x00); // avgFrameRate

        bool temporalIdNested = rbsp.Length > 2 && (rbsp[2] & 0x01) != 0;
        // constantFrameRate(2)=0 | numTemporalLayers(3)=1 | temporalIdNested(1) | lengthSizeMinusOne(2)=3
        ms.WriteByte((byte)(0x08 | (temporalIdNested ? 0x04 : 0x00) | 0x03));

        var arrays = new List<byte[]>(3);
        if (vps != null) arrays.Add(vps);
        arrays.Add(sps);
        arrays.Add(pps);

        ms.WriteByte((byte)arrays.Count);
        foreach (var nal in arrays)
        {
            int type = (nal[0] >> 1) & 0x3F;
            ms.WriteByte((byte)(0x80 | type)); // array_completeness=1
            ms.WriteByte(0x00); ms.WriteByte(0x01); // numNalus = 1
            ms.WriteByte((byte)(nal.Length >> 8));
            ms.WriteByte((byte)nal.Length);
            ms.Write(nal, 0, nal.Length);
        }

        return ms.ToArray();
    }

    /// <summary>Removes emulation-prevention bytes (00 00 03 → 00 00) from a NAL.</summary>
    private static byte[] RemoveEmulationPrevention(byte[] nal)
    {
        var rbsp = new byte[nal.Length];
        int outLen = 0;
        int zeros = 0;
        for (int i = 0; i < nal.Length; i++)
        {
            if (zeros >= 2 && nal[i] == 0x03)
            {
                zeros = 0;
                continue;
            }
            rbsp[outLen++] = nal[i];
            zeros = nal[i] == 0 ? zeros + 1 : 0;
        }
        Array.Resize(ref rbsp, outLen);
        return rbsp;
    }

    /// <summary>Builds the RFC 6381 codec string from the SPS, e.g. "hvc1.1.6.L93.B0".</summary>
    private string BuildCodecString()
    {
        try
        {
            if (_sps != null)
            {
                var rbsp = RemoveEmulationPrevention(_sps);
                if (rbsp.Length >= 15)
                {
                    int profileIdc = rbsp[3] & 0x1F;
                    bool highTier = (rbsp[3] & 0x20) != 0;
                    uint compat = ((uint)rbsp[4] << 24) | ((uint)rbsp[5] << 16) | ((uint)rbsp[6] << 8) | rbsp[7];
                    int levelIdc = rbsp[14];

                    string compatHex = compat.ToString("X").TrimStart('0');
                    if (compatHex.Length == 0) compatHex = "0";

                    var constraint = new byte[6];
                    Buffer.BlockCopy(rbsp, 8, constraint, 0, 6);
                    int last = 5;
                    while (last >= 0 && constraint[last] == 0) last--;
                    string constraintHex = last >= 0
                        ? BitConverter.ToString(constraint, 0, last + 1).Replace("-", "")
                        : "0";

                    return $"hvc1.{profileIdc}.{compatHex}.{(highTier ? "H" : "L")}{levelIdc}.{constraintHex}";
                }
            }
        }
        catch { }
        return "hvc1.1.6.L93.B0";
    }

    public void Shutdown()
    {
        _isInitialized = false;
        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref _activeEncoders);
        }
        _cts?.Cancel();
        _auFlushTimer?.Dispose();
        _auFlushTimer = null;

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

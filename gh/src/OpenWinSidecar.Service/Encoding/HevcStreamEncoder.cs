using System.Diagnostics;
using System.IO;

namespace OpenWinSidecar.Service.Encoders;

/// <summary>
/// Ultra-low latency hardware HEVC (H.265) encoder via FFmpeg, spanning GPU vendors through the
/// <see cref="HevcEncoderProfile"/> abstraction (Intel QSV, NVIDIA NVENC, AMD AMF) — the vendor is
/// selected once per process by a startup probe.
///
/// Output framing: FFmpeg emits a raw Annex-B byte stream. This class parses it into NAL units and
/// re-frames it as access-unit-aligned chunks of 4-byte length-prefixed NALs, plus a one-shot
/// HEVCDecoderConfigurationRecord ("hvcC") and the matching codec string — exactly what Safari's
/// WebCodecs VideoDecoder expects via the `description` field. (Chrome accepts bare Annex-B;
/// Safari wants the out-of-band description.) The framing/parsing is vendor-agnostic.
/// </summary>
public sealed class HevcStreamEncoder : IDisposable
{
    private HevcEncoderProfile? _profile;
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
    private bool _firstVclLogged;
    private readonly object _bitstreamLock = new();
    private System.Threading.Timer? _auFlushTimer;
    private int _nalCount, _vclCount, _auCount, _packetCount;

    public bool IsActive => _isInitialized && _ffmpegProc != null && !_ffmpegProc.HasExited;

    /// <summary>Display name of the hardware encoder selected at startup (for the Console).</summary>
    public static string SelectedEncoderName => HevcEncoderProfiles.SelectedDisplayName ?? "JPEG (no hardware encoder)";

    /// <summary>
    /// True when a hardware encoder passed its probe (implying a working FFmpeg). The FFmpeg
    /// banner hides itself when this is set — the encoder's actual outcome is authoritative
    /// over a file-resolution lookup that can transiently miss.
    /// </summary>
    public static bool HardwareEncoderActive => HevcEncoderProfiles.HasSelectedProfile;

    /// <summary>Live count of running hardware encoder processes across all clients.</summary>
    public static int ActiveEncoderCount => _activeEncoders;
    private static int _activeEncoders;
    private const int MaxEncoders = 4; // each is an ffmpeg process with hardware encoder surfaces behind it

    public HevcStreamEncoder(Action<byte[], bool> onPacketEncoded, Action<string, byte[]>? onDescriptionReady = null)
    {
        _onPacketEncoded = onPacketEncoded;
        _onDescriptionReady = onDescriptionReady;
    }

    private static string? _cachedFfmpegPath;
    private static bool _warnedFfmpegMissing;

    /// <summary>
    /// Explicit ffmpeg.exe candidates checked after PATH (where.exe), most-specific first:
    /// an optional app-local side-by-side bundle ({app}\ffmpeg\bin) wins, then the winget
    /// portable alias ({localappdata}\Microsoft\WinGet\Links) — which covers a fresh
    /// "winget install Gyan.FFmpeg" whose PATH change hasn't propagated to this process —
    /// before the slower recursive Packages scan.
    /// </summary>
    public static string[] GetFfmpegSearchPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
        ];
    }

    public static string? FindFfmpegExecutable()
    {
        // Resolved once per process: this spawns `where.exe` and can scan the WinGet package
        // tree, which added ~100-300ms to every encoder start (and every forceidr/encoder
        // restart). The path does not change at runtime — unless the dashboard installs
        // FFmpeg (one-click winget fix), which calls ResetFfmpegCache.
        if (_cachedFfmpegPath != null) return _cachedFfmpegPath;
        _cachedFfmpegPath = ResolveFfmpegExecutable();
        return _cachedFfmpegPath;
    }

    /// <summary>
    /// Forces the next <see cref="FindFfmpegExecutable"/> to re-resolve. Called by the
    /// dashboard after installing FFmpeg at runtime so new client sessions pick up the
    /// hardware encoder without an app restart.
    /// </summary>
    public static void ResetFfmpegCache() => _cachedFfmpegPath = null;

    private static string? ResolveFfmpegExecutable()
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

        // 2. Explicit candidates (app-local bundle, WinGet Links alias)
        foreach (var candidate in GetFfmpegSearchPaths())
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 3. Scan the WinGet portable package tree (Gyan.FFmpeg via winget lands here).
        // AllDirectories throws on any subfolder the account can't read (the tree contains
        // packages owned by other installs) — a transient ACL failure must read as
        // "not found", not crash the banner check or the encoder init.
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetDir))
            {
                var matches = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories);
                if (matches.Length > 0) return matches[0];
            }
        }
        catch { }

        // 4. Nothing found anywhere we know to look. Say so once, with the fix, and make
        //    the miss visible to callers (Initialize aborts; the sink degrades to JPEG).
        if (!_warnedFfmpegMissing)
        {
            _warnedFfmpegMissing = true;
            Console.WriteLine(
                "[HEVC] FFmpeg was not found in PATH, the WinGet package tree, or next to the app. " +
                "Install it with: winget install --id Gyan.FFmpeg -e (full build) — the dashboard's " +
                "'Install FFmpeg' button does this too. Streaming falls back to JPEG intra meanwhile.");
        }
        return null;
    }

    public bool Initialize(int width, int height, int bitrateKbps = 4000, int framerate = 60, int pixelDepth = 8)
    {
        lock (_syncLock)
        {
            Shutdown();

            _width = width;
            _height = height;
            _bitrateKbps = bitrateKbps;
            int depth = pixelDepth == 10 ? 10 : 8;

            // Reset bitstream state — a fresh encoder instance streams fresh parameter sets
            _accLen = 0;
            _inNalContinuation = false;
            _vps = _sps = _pps = null;
            _pendingNonVcl.Clear();
            _auNals.Clear();
            _auHasKey = false;
            _descriptionSent = false;
            _firstVclLogged = false;

            var ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                Console.WriteLine("[HEVC] FFmpeg executable not found.");
                return false;
            }

            // Each encoder is an ffmpeg process holding hardware encoder surfaces — cap the total
            if (_activeEncoders >= MaxEncoders)
            {
                Console.WriteLine($"[HEVC] Encoder limit reached ({_activeEncoders}/{MaxEncoders}) — refusing new instance.");
                return false;
            }

            // Pick the best working hardware vendor once per process (QSV → NVENC → AMF). Null means
            // none are usable on this machine, so the sink falls back to JPEG intra.
            _profile = HevcEncoderProfiles.Detect(ffmpegPath);
            if (_profile == null) return false;

            try
            {
                // Low-latency hardware encoder; the vendor is abstracted by _profile:
                //   VideoFilter     BGRA→NV12 keeps Main profile (hardware-decodable on every Apple
                //                   device); QSV additionally hwuploads to QSV surfaces (BGRA→QSV
                //                   would otherwise be 4:4:4/Rext, which iPads software-decode).
                //   -bf 0           no B-frames (output order == input order)
                //   LowLatencyArgs  vendor low-latency flags (QSV async_depth 1 +low_delay; NVENC
                //                   tune ll; AMF ultralowlatency).
                //   -g {gop}        10-minute GOP — effectively no periodic IDR. Safe because the sink
                //                   drops *input* frames (drop-oldest) rather than encoded access units, so the
                //                   encoded delta chain is always complete and a client never needs periodic
                //                   keyframes to recover. A 4 s GOP emitted a ~200 KB IDR every 4 s (~150 ms of
                //                   Wi-Fi transmit time) — a visible periodic hitch. Keyframes now happen only at
                //                   encoder start and on explicit client resync (tab-visible/backlog).
                int fps = framerate > 0 ? framerate : 60;
                int gop = fps * 600;
                // 10-bit (Main10) needs no profile flag: QSV derives Main10 from p010 input
                // (verified: ffprobe reports profile=Main 10). Gated to QSV; other vendors keep
                // their validated 8-bit filter chain.
                string videoFilter = (depth == 10 && _profile.Vendor == EncoderVendor.Qsv)
                    ? "format=p010le,hwupload"
                    : _profile.VideoFilter;
                // Stall guard (QSV only): bound worst-case frame airtime so a single frame can
                // never occupy the wire for 50-150ms (reads client-side as a micro-stop).
                // avgBytes ≈ one average frame at the target bitrate; P capped at 6x, I at 20x
                // (session IDRs are ~140-210KB and must pass). max_qp is a loose backstop so a
                // capped frame degrades gracefully instead of collapsing; scenario 1 tells QSV
                // this is display remoting (screen-content BRC tradeoffs).
                string sizeCaps = "";
                if (_profile.Vendor == EncoderVendor.Qsv)
                {
                    int avgBytes = Math.Max(4096, _bitrateKbps * 1000 / 8 / Math.Max(fps, 1));
                    sizeCaps = $" -max_frame_size_p {avgBytes * 6} -max_frame_size_i {avgBytes * 20}" +
                               $" -max_qp_i 30 -max_qp_p 38 -scenario 1";
                }
                var args = $"-hide_banner -loglevel error {_profile.GlobalArgs}" +
                           $"-f rawvideo -pix_fmt bgra -s {_width}x{_height} -r {fps} -i pipe:0 " +
                           $"-vf {videoFilter} " +
                           $"-c:v {_profile.Name} {_profile.PresetArg} -b:v {_bitrateKbps}k -maxrate {(_bitrateKbps * 3 / 2)}k -bufsize {(_bitrateKbps / 4)}k " +
                           $"-g {gop} -bf 0 {_profile.LowLatencyArgs} {_profile.RateControlArgs}{sizeCaps} -an -f hevc pipe:1";

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

                // Surface FFmpeg's stderr ÔÇö with -loglevel error this only carries real failures,
                // and an unread stderr pipe would otherwise silently block or hide crashes
                _ffmpegProc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine("[HEVC][ff] " + e.Data);
                };
                _ffmpegProc.BeginErrorReadLine();

                _stdin = _ffmpegProc.StandardInput.BaseStream;
                _stdout = _ffmpegProc.StandardOutput.BaseStream;
                _cts = new CancellationTokenSource();

                _readTask = Task.Run(() => ReadNalStreamLoopAsync(_stdout, _cts.Token));

                // The encoder writes access units atomically, and a NAL's end is only knowable
                // when the next start code arrives. When the stream goes silent (idle-skip starves
                // the encoder), flush complete access units — but NEVER synthesize a NAL from
                // unterminated bytes: a read boundary can split a NAL, and emitting the prefix as
                // a whole NAL irreversibly corrupts the client's decoder (Safari hard-fails with
                // "Decoder failure" and the session permanently degrades to JPEG). The final
                // pre-silence frame is simply held until its terminator arrives with the next
                // data; showing it one frame late is invisible, a dead decoder is not.
                _auFlushTimer = new System.Threading.Timer(
                    _ =>
                    {
                        lock (_bitstreamLock)
                        {
                            FlushAccessUnit();
                        }
                    },
                    null, TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(33));

                _isInitialized = true;
                _counted = true;
                Interlocked.Increment(ref _activeEncoders);
                Console.WriteLine($"[HEVC] Encoder started: {_width}x{_height} @ {_bitrateKbps} kbps, {gop}-frame GOP, {depth}-bit — {_profile.DisplayName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEVC] Init error: {ex.Message}");
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
            LogPushFailure(ex);
            return false;
        }
    }

    private void LogPushFailure(Exception ex)
    {
        if (_pushFailLogged == 0 && Interlocked.Exchange(ref _pushFailLogged, 1) == 0)
        {
            string stderrSnapshot = "";
            try { stderrSnapshot = _ffmpegProc == null ? "" : _ffmpegProc.StandardError.ReadToEnd(); } catch { }
            Console.WriteLine($"[HEVC] PushRawFrame failed: {ex.Message} | exited={_ffmpegProc?.HasExited} exitCode={(_ffmpegProc?.HasExited == true ? _ffmpegProc.ExitCode : -1)} | stderr: {stderrSnapshot}");
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
                    // EOF: FFmpeg's stdout closed ÔÇö it exited (crash or kill)
                    Console.WriteLine($"[HEVC] stdout closed. reads={reads} NALs={_nalCount} vcl={_vclCount} aus={_auCount} sent={_packetCount} processExited={_ffmpegProc?.HasExited}");
                    break;
                }

                reads++;
                if (reads <= 5)
                    Console.WriteLine($"[HEVC] read #{reads}: {bytesRead} bytes, first={BitConverter.ToString(buffer, 0, Math.Min(12, bytesRead))}");

                ParseAnnexB(buffer, bytesRead);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[HEVC] Stream reader error: {ex.Message}");
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
            // starts this pass's NAL ÔÇö without this, any NAL spanning two reads is discarded
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
                // No start code at all and not inside a NAL ÔÇö leading garbage; a partial
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
            // VCL NAL ÔÇö first_slice_segment_in_pic_flag is the top bit of the byte after the 2-byte NAL header
            bool firstSlice = nal.Length > 2 && (nal[2] & 0x80) != 0;
            if (firstSlice && _auNals.Count > 0) FlushAccessUnit();

            if (!_firstVclLogged)
            {
                _firstVclLogged = true;
                Console.WriteLine($"[HEVC] first VCL NAL: type={type} len={nal.Length} bytes={BitConverter.ToString(nal, 0, Math.Min(8, nal.Length))}");
            }

            _vclCount++;
            _auNals.Add(LengthPrefixed(nal));
            if (type >= 16 && type <= 21) _auHasKey = true; // BLA/IDR/CRA
        }
        else
        {
            _pendingNonVcl.Add(LengthPrefixed(nal));
        }

        if (_nalCount % 600 == 0)
            Console.WriteLine($"[HEVC] NALs={_nalCount} vcl={_vclCount} aus={_auCount} sent={_packetCount}");
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
        _packetCount++;
        if (_auCount == 1)
            Console.WriteLine($"[HEVC] first access unit: {payload.Length} bytes, key={key}");

        // Timestamped keyframe log for stall correlation: a 100-250KB IDR occupies the
        // wire for 50-150ms and shows up client-side as a ~1s-periodic micro-stop.
        if (key)
            Console.WriteLine($"[HEVC] keyframe AU #{_auCount}: {payload.Length} bytes at {DateTime.UtcNow:HH:mm:ss.fff}");

        _onPacketEncoded(payload, key);
    }

    private void TryEmitDescription()
    {
        if (_descriptionSent || _sps == null || _pps == null) return;
        _descriptionSent = true;

        if (_onDescriptionReady == null) return;

        var record = BuildHvcC(_vps, _sps, _pps);
        var codecString = BuildCodecString();
        Console.WriteLine($"[HEVC] hvcC description ready: {codecString} ({record.Length} bytes) sps={BitConverter.ToString(_sps, 0, Math.Min(20, _sps.Length))}");
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
    /// from the SPS's profile_tier_level structure ÔÇö read from the emulation-prevention-
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

    /// <summary>Removes emulation-prevention bytes (00 00 03 ÔåÆ 00 00) from a NAL.</summary>
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

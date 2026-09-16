using System.Diagnostics;

namespace OpenWinSidecar.Service.Encoders;

internal enum EncoderVendor
{
    Qsv,
    Nvenc,
    Amf
}

/// <summary>
/// Vendor-specific FFmpeg arguments for the hardware HEVC encoder. Keeping one profile per GPU
/// vendor means "add NVIDIA/AMD support" is a matter of selecting a profile, not integrating a
/// different SDK — FFmpeg exposes every vendor behind the same <c>-c:v hevc_&lt;vendor&gt;</c>
/// interface and the Annex-B/hvcC parsing downstream is vendor-agnostic.
///
/// The QSV profile is validated on Intel Arc. The NVENC and AMF profiles are written against the
/// FFmpeg encoder options but are <b>unvalidated</b> until run on that hardware (the startup probe
/// refuses a vendor whose full argument set fails, so an unvalidated profile can only ever fall
/// back to JPEG, never ship a broken stream).
/// </summary>
internal sealed class HevcEncoderProfile
{
    public required EncoderVendor Vendor { get; init; }

    /// <summary>FFmpeg encoder id, e.g. <c>hevc_qsv</c>.</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Device-init arguments placed before <c>-i</c> (empty for encoders that accept system frames).</summary>
    public string GlobalArgs { get; init; } = string.Empty;

    /// <summary>Pixel-format / hardware-upload filter chain.</summary>
    public required string VideoFilter { get; init; }

    /// <summary>Encoder preset argument, e.g. <c>-preset veryfast</c>.</summary>
    public required string PresetArg { get; init; }

    /// <summary>Vendor low-latency flags (minimal buffering / no look-ahead).</summary>
    public string LowLatencyArgs { get; init; } = string.Empty;

    /// <summary>
    /// Vendor rate-control smoothing flags: strict frame-size obedience, macroblock-level
    /// bitrate control, no scene-cut I insertion (periodic large frames read client-side as
    /// ~1s micro-stops). Empty for vendors whose tune preset already covers this.
    /// </summary>
    public string RateControlArgs { get; init; } = string.Empty;

    public bool Validated { get; init; }

    public static readonly HevcEncoderProfile Qsv = new()
    {
        Vendor = EncoderVendor.Qsv,
        Name = "hevc_qsv",
        DisplayName = "Intel QuickSync (hevc_qsv)",
        GlobalArgs = "-init_hw_device qsv=hw -filter_hw_device hw ",
        VideoFilter = "format=nv12,hwupload",
        PresetArg = "-preset veryfast",
        LowLatencyArgs = "-async_depth 1 -flags +low_delay",
        RateControlArgs = "-low_delay_brc 1 -mbbrc 1 -adaptive_i 0",
        Validated = true,
    };

    public static readonly HevcEncoderProfile Nvenc = new()
    {
        Vendor = EncoderVendor.Nvenc,
        Name = "hevc_nvenc",
        DisplayName = "NVIDIA NVENC (hevc_nvenc)",
        GlobalArgs = string.Empty,
        VideoFilter = "format=nv12",
        PresetArg = "-preset p1",
        LowLatencyArgs = "-tune ll -rc cbr",
        Validated = false,
    };

    public static readonly HevcEncoderProfile Amf = new()
    {
        Vendor = EncoderVendor.Amf,
        Name = "hevc_amf",
        DisplayName = "AMD AMF (hevc_amf)",
        GlobalArgs = string.Empty,
        VideoFilter = "format=nv12",
        PresetArg = "-quality speed",
        LowLatencyArgs = "-usage ultralowlatency -rc cbr",
        Validated = false,
    };
}

internal static class HevcEncoderProfiles
{
    // Priority order: prefer the encoder most likely to be low-latency on a given box. The probe
    // makes the order safe — a listed-but-nonfunctional vendor simply fails and we move on.
    private static readonly HevcEncoderProfile[] Priority =
    {
        HevcEncoderProfile.Qsv,
        HevcEncoderProfile.Nvenc,
        HevcEncoderProfile.Amf,
    };

    private static readonly object Lock = new();
    private static bool _detected;
    private static HevcEncoderProfile? _selected;

    /// <summary>Display name of the selected hardware encoder (for the Console), or null if none.</summary>
    internal static string? SelectedDisplayName => _selected?.DisplayName;

    /// <summary>
    /// True once a hardware encoder passed its probe — which by construction means a working
    /// FFmpeg was found and used. The dashboard's FFmpeg-missing banner defers to this: if the
    /// encoder is streaming HEVC, a null file-resolution result is a stale/failed lookup, not
    /// a missing dependency, and the banner must not show.
    /// </summary>
    internal static bool HasSelectedProfile => _selected != null;

    /// <summary>
    /// Detects the best working hardware HEVC encoder once per process by running a one-frame probe
    /// encode against each candidate in priority order. Returns null when none work, so the caller
    /// falls back to JPEG. Software encoding (libx265) is deliberately not offered: it cannot keep
    /// up at native resolutions, so JPEG intra is the better fallback.
    /// </summary>
    public static HevcEncoderProfile? Detect(string ffmpegPath)
    {
        lock (Lock)
        {
            if (_detected) return _selected;
            _detected = true;

            foreach (var profile in Priority)
            {
                if (!Probe(ffmpegPath, profile)) continue;

                _selected = profile;
                Console.WriteLine(
                    $"[HEVC] Selected hardware encoder: {profile.DisplayName}" +
                    (profile.Validated ? string.Empty : " (unvalidated on this hardware)"));
                return _selected;
            }

            Console.WriteLine("[HEVC] No working hardware HEVC encoder found — clients will use JPEG intra.");
            return null;
        }
    }

    private static bool Probe(string ffmpegPath, HevcEncoderProfile profile)
    {
        try
        {
            var args = $"-hide_banner -loglevel error {profile.GlobalArgs}" +
                       $"-f lavfi -i color=c=black:s=256x256:r=30 -frames:v 1 " +
                       $"-vf {profile.VideoFilter} -c:v {profile.Name} {profile.PresetArg} {profile.LowLatencyArgs} -f null -";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(6000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

namespace FFmpegUtils.Models;

public sealed record ConversionProgress(double Percent, string Stage, string Detail = "");

public sealed record ConversionResult(
    string OutputPath,
    long FileSizeBytes,
    int Attempts = 1,
    string? Warning = null,
    string? EncoderName = null);

public sealed record FfmpegInstallation(
    string FfmpegPath,
    string FfprobePath,
    string Version,
    bool HasGifFilters,
    bool HasSubtitleFilter,
    bool HasNvencEncoder = false,
    bool HasQsvEncoder = false,
    bool HasAmfEncoder = false)
{
    public bool IsReady => File.Exists(FfmpegPath) && File.Exists(FfprobePath);
}

public enum SubtitleVideoEncoder
{
    Auto,
    Cpu,
    Nvidia,
    Intel,
    Amd
}

public static class SubtitleVideoEncoderCatalog
{
    public const string AutoDisplayName = "自动（推荐）";
    public const string CpuDisplayName = "CPU（libx264）";
    public const string NvidiaDisplayName = "NVIDIA（NVENC）";
    public const string IntelDisplayName = "Intel（QSV）";
    public const string AmdDisplayName = "AMD（AMF）";

    public static IReadOnlyList<string> DisplayNames { get; } =
    [
        AutoDisplayName,
        CpuDisplayName,
        NvidiaDisplayName,
        IntelDisplayName,
        AmdDisplayName
    ];

    public static bool TryParseDisplayName(string? displayName, out SubtitleVideoEncoder encoder)
    {
        encoder = displayName switch
        {
            AutoDisplayName => SubtitleVideoEncoder.Auto,
            CpuDisplayName => SubtitleVideoEncoder.Cpu,
            NvidiaDisplayName => SubtitleVideoEncoder.Nvidia,
            IntelDisplayName => SubtitleVideoEncoder.Intel,
            AmdDisplayName => SubtitleVideoEncoder.Amd,
            _ => SubtitleVideoEncoder.Auto
        };
        return displayName is AutoDisplayName or CpuDisplayName or NvidiaDisplayName or IntelDisplayName or AmdDisplayName;
    }

    public static string GetDisplayName(SubtitleVideoEncoder encoder) => encoder switch
    {
        SubtitleVideoEncoder.Cpu => CpuDisplayName,
        SubtitleVideoEncoder.Nvidia => NvidiaDisplayName,
        SubtitleVideoEncoder.Intel => IntelDisplayName,
        SubtitleVideoEncoder.Amd => AmdDisplayName,
        _ => AutoDisplayName
    };

    public static string GetCodecName(SubtitleVideoEncoder encoder) => encoder switch
    {
        SubtitleVideoEncoder.Cpu => "libx264",
        SubtitleVideoEncoder.Nvidia => "h264_nvenc",
        SubtitleVideoEncoder.Intel => "h264_qsv",
        SubtitleVideoEncoder.Amd => "h264_amf",
        _ => string.Empty
    };
}

public sealed record GifConversionOptions(
    string InputPath,
    string OutputPath,
    int MaxWidth,
    int FrameRate,
    int Colors,
    string Dither,
    double? TargetSizeMb,
    double? StartSeconds,
    double? EndSeconds);

public sealed record SubtitleBurnOptions(
    string InputPath,
    string SubtitlePath,
    string OutputPath,
    string SubtitleEncoding,
    int Crf = 20,
    string Preset = "medium",
    SubtitleVideoEncoder VideoEncoder = SubtitleVideoEncoder.Auto);

public sealed record GifSizeParameters(int Width, int FrameRate, int Colors);

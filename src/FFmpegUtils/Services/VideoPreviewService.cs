using System.Diagnostics;
using System.Text;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed record VideoPreviewFrame(byte[] Pixels, int Width, int Height, double Seconds);

public interface IVideoPreviewService
{
    Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token);
    Task<VideoPreviewFrame> GetScrubFrameAsync(string ffmpeg, string path, double seconds, CancellationToken token)
        => GetFrameAsync(ffmpeg, path, seconds, false, token);
    Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token);
}

/// <summary>Bounded, silent raw-video preview; no media uploads, output files, or system codec dependency.</summary>
public sealed class VideoPreviewService : IVideoPreviewService
{
    public const int FrameRate = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token)
        => ReadStillAsync(ffmpeg, path, seconds, thumbnail ? 128 : 640, thumbnail ? 72 : 360, token);

    public Task<VideoPreviewFrame> GetScrubFrameAsync(string ffmpeg, string path, double seconds, CancellationToken token)
        => ReadStillAsync(ffmpeg, path, seconds, 320, 180, token);

    private Task<VideoPreviewFrame> ReadStillAsync(string ffmpeg, string path, double seconds, int width, int height, CancellationToken token)
        => Task.Run(async () =>
    {
        VideoPreviewFrame? result = null;
        await DecodeAsync(ffmpeg, path, seconds, null, width, height, frame => result = frame, token);
        return result ?? throw new InvalidDataException("该位置没有可预览的视频帧。");
    }, token);

    public Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token)
        => DecodeAsync(ffmpeg, path, start, end, 640, 360, frame, token);

    private async Task DecodeAsync(string ffmpeg, string path, double start, double? end, int width, int height,
        Action<VideoPreviewFrame> onFrame, CancellationToken token)
    {
        if (!File.Exists(path) || !File.Exists(ffmpeg)) throw new FileNotFoundException("视频或 FFmpeg 不存在，请重新选择。");
        await _gate.WaitAsync(token);
        try
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2", "-filter_threads", "1" };
            arguments.AddRange(VideoTimeRange.InputArguments(path, start, end));
            var scale = $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=bilinear,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
            arguments.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", end.HasValue ? $"fps={FrameRate}:eof_action=pass,{scale}" : scale]);
            if (!end.HasValue) arguments.AddRange(["-frames:v", "1"]);
            arguments.AddRange(["-pix_fmt", "bgra", "-threads", "1", "-f", "rawvideo", "pipe:1"]);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("无法启动视频预览。");
            using var registration = token.Register(() => Kill(process));
            var errors = ReadErrorTailAsync(process.StandardError);
            var pixels = new byte[checked(width * height * 4)];
            var index = 0;
            var clock = new Stopwatch();
            try
            {
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    int filled = 0;
                    try
                    {
                        while (filled < pixels.Length)
                        {
                            var read = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(filled), readTimeout.Token);
                            if (read == 0) break;
                            filled += read;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new TimeoutException("视频解码超时，请尝试其他位置或重新选择视频。"); }
                    if (filled == 0) break;
                    if (filled != pixels.Length) throw new InvalidDataException("视频预览帧不完整。");
                    token.ThrowIfCancellationRequested();
                    var position = start + index / (double)FrameRate;
                    if (end.HasValue && position >= end.Value) break;
                    if (!clock.IsRunning) clock.Start();
                    if (end.HasValue)
                    {
                        var delay = TimeSpan.FromSeconds(index / (double)FrameRate) - clock.Elapsed;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    }
                    // The receiver must consume/copy the buffer synchronously; the next read reuses it.
                    onFrame(new VideoPreviewFrame(pixels, width, height, position));
                    index++;
                }
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exitTimeout.Token);
                var error = await errors;
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || index == 0)
                    throw new InvalidDataException("无法解码该视频位置。" + (error.Length > 0 ? "\n" + error : ""));
            }
            finally
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                try { await errors.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 3000) tail.Remove(0, tail.Length - 3000);
        }
        return tail.ToString().Trim();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

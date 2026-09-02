using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class FfmpegProcessRunner
{
    public async Task RunAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        double durationSeconds,
        string stage,
        double progressStart,
        double progressEnd,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var errorLines = new ConcurrentQueue<string>();
        var latestSpeed = string.Empty;

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 FFmpeg。" );
        }

        using var registration = cancellationToken.Register(() => TryKill(process));

        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator];
                var value = line[(separator + 1)..];
                if (key.Equals("speed", StringComparison.OrdinalIgnoreCase))
                {
                    latestSpeed = value;
                }

                if (TryGetProgressSeconds(key, value, out var seconds) && durationSeconds > 0)
                {
                    var local = Math.Clamp(seconds / durationSeconds, 0, 1);
                    var percent = (progressStart + (progressEnd - progressStart) * local) * 100;
                    progress?.Report(new ConversionProgress(percent, stage, string.IsNullOrWhiteSpace(latestSpeed) ? string.Empty : $"速度 {latestSpeed}"));
                }

                if (key.Equals("progress", StringComparison.OrdinalIgnoreCase)
                    && value.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new ConversionProgress(progressEnd * 100, stage, string.IsNullOrWhiteSpace(latestSpeed) ? string.Empty : $"速度 {latestSpeed}"));
                }
            }
        }, cancellationToken);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                errorLines.Enqueue(line);
                while (errorLines.Count > 40)
                {
                    errorLines.TryDequeue(out _);
                }
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new FfmpegException($"FFmpeg 处理失败（退出代码 {process.ExitCode}）。", string.Join(Environment.NewLine, errorLines));
        }
    }

    public static bool TryGetProgressSeconds(string key, string value, out double seconds)
    {
        seconds = 0;
        if (key.Equals("out_time_us", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
        {
            seconds = microseconds / 1_000_000d;
            return true;
        }

        if (key.Equals("out_time", StringComparison.OrdinalIgnoreCase)
            && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
        {
            seconds = time.TotalSeconds;
            return true;
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation.
        }
    }
}

public sealed class FfmpegException(string message, string details) : Exception(message)
{
    public string Details { get; } = details;

    public override string ToString() => string.IsNullOrWhiteSpace(Details) ? base.ToString() : $"{Message}{Environment.NewLine}{Details}";
}

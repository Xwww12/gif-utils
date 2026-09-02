using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

internal sealed record YtDlpProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class YtDlpProcessRunner
{
    private const int MaxCapturedCharacters = 32 * 1024 * 1024;

    public static async Task<YtDlpProcessResult> CaptureAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, arguments);
        Start(process, executable);
        using var registration = cancellationToken.Register(() => TryKill(process));
        try
        {
            var outputTask = ReadToEndLimitedAsync(process.StandardOutput, MaxCapturedCharacters, cancellationToken);
            var errorTask = ReadToEndLimitedAsync(process.StandardError, MaxCapturedCharacters, cancellationToken);
            _ = outputTask.ContinueWith(
                _ => TryKill(process),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = errorTask.ContinueWith(
                _ => TryKill(process),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var firstCompleted = await Task.WhenAny(exitTask, outputTask, errorTask);
            if (firstCompleted != exitTask && firstCompleted.IsFaulted)
            {
                TryKill(process);
                await firstCompleted;
            }

            await exitTask;
            return new YtDlpProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public static async Task<YtDlpProcessResult> StreamAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string> onOutputLine,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, arguments);
        var errorLines = new ConcurrentQueue<string>();
        Start(process, executable);
        using var registration = cancellationToken.Register(() => TryKill(process));

        var outputTask = ReadLinesAsync(process.StandardOutput, line => onOutputLine(line), cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, line =>
        {
            onOutputLine(line);
            errorLines.Enqueue(line);
            while (errorLines.Count > 80)
            {
                errorLines.TryDequeue(out _);
            }
        }, cancellationToken);
        _ = outputTask.ContinueWith(
            _ => TryKill(process),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = errorTask.ContinueWith(
            _ => TryKill(process),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return new YtDlpProcessResult(process.ExitCode, string.Empty, string.Join(Environment.NewLine, errorLines));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static Process CreateProcess(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static void Start(Process process, string executable)
    {
        try
        {
            if (!process.Start())
            {
                throw new XDownloadException(XDownloadErrorKind.ToolUnavailable, $"无法启动：{executable}");
            }
        }
        catch (XDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.ToolUnavailable,
                "无法启动 X 解析组件。",
                exception.Message,
                exception);
        }
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            onLine(line);
        }
    }

    private static async Task<string> ReadToEndLimitedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var buffer = new char[32 * 1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > maximumCharacters - count)
            {
                throw new XDownloadException(
                    XDownloadErrorKind.ParseFailed,
                    "X 解析结果异常过大，已停止处理。",
                    $"最大允许 {maximumCharacters / 1024 / 1024} MiB 字符输出。");
            }

            builder.Append(buffer, 0, count);
        }
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
            // Process-tree cancellation is best effort.
        }
    }
}

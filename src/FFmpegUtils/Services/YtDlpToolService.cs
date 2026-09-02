using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class YtDlpToolService
{
    public const string Version = "2026.08.19";
    public const string ExpectedSha256 = "66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A";
    public static readonly Uri OfficialDownloadUri = new($"https://github.com/yt-dlp/yt-dlp/releases/download/{Version}/yt-dlp.exe");

    private static readonly HttpClient DefaultHttpClient = CreateDefaultHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpClient _httpClient;

    public YtDlpToolService(HttpClient? httpClient = null, string? localAppData = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new XDownloadException(XDownloadErrorKind.ToolUnavailable, "无法确定 yt-dlp 的本地存储目录。");
        }

        ToolDirectory = Path.Combine(Path.GetFullPath(root), "FFmpegUtils", "tools", Version);
        ToolPath = Path.Combine(ToolDirectory, "yt-dlp.exe");
    }

    public string ToolDirectory { get; }

    public string ToolPath { get; }

    public async Task<string> EnsureAvailableAsync(
        IProgress<XDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ToolPath) && await HasExpectedHashAsync(ToolPath, cancellationToken))
            {
                return ToolPath;
            }

            progress?.Report(new XDownloadProgress(0, "正在准备 X 解析组件", "首次使用需从 yt-dlp 官方项目下载固定版本"));
            Directory.CreateDirectory(ToolDirectory);
            var temporaryPath = Path.Combine(ToolDirectory, $"yt-dlp.{Guid.NewGuid():N}.download");
            try
            {
                await DownloadAsync(temporaryPath, progress, cancellationToken);
                if (!await HasExpectedHashAsync(temporaryPath, cancellationToken))
                {
                    throw new XDownloadException(
                        XDownloadErrorKind.ToolIntegrity,
                        "yt-dlp 完整性校验失败，已拒绝运行该文件。",
                        $"期望 SHA-256：{ExpectedSha256}");
                }

                File.Move(temporaryPath, ToolPath, overwrite: true);
                if (!await HasExpectedHashAsync(ToolPath, cancellationToken))
                {
                    throw new XDownloadException(XDownloadErrorKind.ToolIntegrity, "yt-dlp 写入后完整性校验失败。");
                }

                progress?.Report(new XDownloadProgress(100, "X 解析组件已就绪"));
                return ToolPath;
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task DownloadAsync(
        string temporaryPath,
        IProgress<XDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                OfficialDownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            long downloaded = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                downloaded += count;
                var percent = totalBytes is > 0 ? downloaded * 100d / totalBytes.Value : 0;
                progress?.Report(new XDownloadProgress(
                    percent,
                    "正在准备 X 解析组件",
                    FormatBytes(downloaded) + (totalBytes is > 0 ? $" / {FormatBytes(totalBytes.Value)}" : string.Empty),
                    downloaded,
                    totalBytes));
            }

            await output.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.ToolUnavailable,
                "无法从 yt-dlp 官方项目下载解析组件，请检查网络后重试。",
                exception.Message,
                exception);
        }
        catch (IOException exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.ToolUnavailable,
                "无法保存 yt-dlp 解析组件，请检查磁盘空间和目录权限。",
                exception.Message,
                exception);
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FFmpegUtils", "1.0"));
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary download cleanup is best effort.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

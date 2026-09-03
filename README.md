# GIF Utils

Windows 桌面工具，提供四项功能：
- **MP4 转 GIF**：截取片段、预览视频、调整 GIF 大小。
- **字幕烧录**：将字幕嵌入视频，支持 CPU / GPU 编码。
- **X 视频下载**：解析公开帖子链接，选择画质并下载。
- **图片信息**：查看尺寸、拍摄参数和 GPS，支持手动查询地址。

## 构建与运行

本仓库仅含源码，**请先构建，或下载使用已构建好版本**

**构建流程**：
1. 在 Windows 上安装 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)，选择 **SDK → Windows → x64**，不要只安装 Runtime。
2. 下载并解压源码，进入能看到 `publish.ps1` 的文件夹。在资源管理器的**地址栏**输入 `powershell`，按回车打开命令窗口。
3. 在窗口中输入下面的命令：

   ```powershell
   .\publish.ps1
   ```

   脚本会自动下载依赖、编译并打包，首次构建需要联网。看到 `Published to:` 表示构建完成，无需再单独运行编译命令。

4. 打开项目内的 `artifacts\publish\win-x64` 文件夹，双击 `GIFUtils.exe` 启动。生成的是 64 位程序，自带 .NET 运行环境。
5. 使用视频功能前，点击“选择 FFmpeg”并选中 `ffmpeg.exe`，其同目录须有 `ffprobe.exe`。随后按页面提示选择文件或粘贴链接、设置保存位置并开始处理。图片信息只需选择图片，无需 FFmpeg。

**注意**：源码不包含FFmpeg，FFmpeg下载地址：https://ffmpeg.org/download.html。

第三方许可见 [licenses](licenses)。

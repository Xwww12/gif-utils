# GIF Utils

Windows 桌面工具，提供四项功能：

- **MP4 转 GIF**：截取片段、预览视频、调整 GIF 大小。
- **字幕烧录**：将字幕嵌入视频，支持 CPU / GPU 编码。
- **X 视频下载**：解析公开帖子链接，选择画质并下载。
- **图片信息**：查看尺寸、拍摄参数和 GPS，支持手动查询地址。

## 使用

打开 `GIFUtils.exe`，按页面提示选择文件或粘贴链接即可。视频功能需先配置 `ffmpeg.exe`，其同目录须有 `ffprobe.exe`。

X 下载需要联网；地址查询会发送经纬度，不上传图片。

## 构建

仓库仅含源码。在 Windows 上安装 .NET 10 SDK，在项目根目录运行：

```powershell
.\publish.ps1
```

生成的程序：`artifacts\publish\win-x64\GIFUtils.exe`（64 位，自带 .NET 运行环境，不含 FFmpeg）。

第三方许可见 [licenses](licenses)。

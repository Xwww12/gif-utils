using FFmpegUtils.Infrastructure;
using FFmpegUtils.Models;
using FFmpegUtils.Services;

namespace FFmpegUtils.ViewModels;

public sealed class ImageInfoViewModel : ObservableObject
{
    private readonly Func<string, Task<ImageMetadataInfo>> _read;
    private readonly Func<ImageCoordinates, CancellationToken, Task<ImageAddress>> _resolve;
    private CancellationTokenSource? _addressCancellation;
    private int _addressVersion;
    private bool _isResolving;
    private string _region = "—";
    private string _nearbyAddress = "—";
    private string _addressDetail = "城市与地址来自地图匹配，并非图片原始记录。";
    private string _addressStatus = "请选择含 GPS 的图片";
    private int _requestVersion;
    private string _inputPath = "";
    private string _status = "选择图片后自动读取";
    private string _error = "";
    private bool _isReading;
    private ImageMetadataInfo _details = ImageMetadataInfo.Empty;

    public ImageInfoViewModel(Func<string, Task<ImageMetadataInfo>>? read = null,
        Func<ImageCoordinates, CancellationToken, Task<ImageAddress>>? resolve = null)
    {
        _read = read ?? new ImageMetadataService().ReadAsync;
        _resolve = resolve ?? ImageGeocodingService.Shared.ResolveAsync;
    }

    public string Region { get => _region; private set => SetProperty(ref _region, value); }
    public string NearbyAddress { get => _nearbyAddress; private set => SetProperty(ref _nearbyAddress, value); }
    public string AddressDetail { get => _addressDetail; private set { if (SetProperty(ref _addressDetail, value)) OnPropertyChanged(nameof(AddressStatusToolTip)); } }
    public string AddressStatus { get => _addressStatus; private set { if (SetProperty(ref _addressStatus, value)) OnPropertyChanged(nameof(AddressStatusToolTip)); } }
    public string AddressStatusToolTip => $"{AddressStatus}\n{AddressDetail}";
    public bool IsResolving
    {
        get => _isResolving;
        private set
        {
            if (!SetProperty(ref _isResolving, value)) return;
            OnPropertyChanged(nameof(CanResolve));
            OnPropertyChanged(nameof(AddressActionText));
        }
    }
    public string AddressActionText => IsResolving ? "取消查询" : "解析地址";
    public bool CanResolve => IsResolving || (!IsReading && Details.Coordinates is { IsValid: true, IsWgs84: true });

    public string InputPath { get => _inputPath; private set => SetProperty(ref _inputPath, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool IsReading { get => _isReading; private set { if (SetProperty(ref _isReading, value)) { OnPropertyChanged(nameof(CanSelect)); OnPropertyChanged(nameof(CanResolve)); } } }
    public bool CanSelect => !IsReading;
    public ImageMetadataInfo Details { get => _details; private set { if (SetProperty(ref _details, value)) OnPropertyChanged(nameof(CanResolve)); } }

    public async Task LoadAsync(string path)
    {
        var version = ++_requestVersion;
        CancelAddressLookup();
        Region = "—";
        NearbyAddress = "—";
        AddressDetail = "城市与地址来自地图匹配，并非图片原始记录。";
        AddressStatus = "等待读取 GPS";
        InputPath = path;
        Details = ImageMetadataInfo.Empty;
        Error = "";
        Status = "正在读取图片…";
        IsReading = true;
        try
        {
            var result = await _read(path);
            if (version != _requestVersion) return;
            Details = result;
            AddressStatus = result.Coordinates switch
            {
                { IsValid: true, IsWgs84: true } => "仅发送坐标，不上传图片",
                { IsWgs84: false } => "非 WGS 84 坐标，暂不支持地址解析",
                _ => "未记录完整有效的 GPS，无法解析地址"
            };
            Status = "读取完成 · 未记录表示未读到对应信息";
        }
        catch (Exception exception)
        {
            if (version != _requestVersion) return;
            Error = exception switch
            {
                FileNotFoundException or DirectoryNotFoundException => "图片不存在或已被移动，请重新选择。",
                UnauthorizedAccessException => "无法访问图片，请检查文件读取权限。",
                NotSupportedException => "不支持此格式，请选择 JPG、PNG、WebP、TIFF、BMP、GIF、HEIC/HEIF 或 AVIF。",
                IOException => "无法读取图片，文件可能损坏、被占用或无法访问。",
                _ => "图片无法解析，请确认文件未损坏且格式受支持。"
            };
            Status = "读取失败";
            AddressStatus = "请重新选择图片";
        }
        finally
        {
            if (version == _requestVersion) IsReading = false;
        }
    }

    // Called only after the user explicitly confirms sharing coordinates in the UI.
    public async Task ResolveAddressAsync()
    {
        if (IsResolving || !CanResolve || Details.Coordinates is not { } coordinates) return;
        var version = ++_addressVersion;
        using var cancellation = new CancellationTokenSource();
        _addressCancellation = cancellation;
        IsResolving = true;
        Region = "—";
        NearbyAddress = "—";
        AddressDetail = "城市与地址来自地图匹配，并非图片原始记录。";
        AddressStatus = "正在查询城市与附近地址…";
        try
        {
            var address = await _resolve(coordinates, cancellation.Token);
            if (version != _addressVersion) return;
            Region = address.Region;
            NearbyAddress = address.NearbyAddress;
            AddressDetail = address.Detail;
            AddressStatus = "附近匹配，仅供参考（悬停查看详情）";
        }
        catch (Exception exception)
        {
            if (version != _addressVersion) return;
            AddressStatus = exception switch
            {
                OperationCanceledException when cancellation.IsCancellationRequested => "已取消查询",
                OperationCanceledException => "查询超时，请检查网络后重试",
                ImageGeocodingException => exception.Message,
                System.Net.Http.HttpRequestException => "无法连接地图服务，请检查网络后重试",
                System.Text.Json.JsonException => "地图返回的数据无法解析，请稍后重试",
                _ => "地址解析失败，请稍后重试"
            };
            AddressDetail = AddressStatus;
        }
        finally
        {
            if (version == _addressVersion)
            {
                _addressCancellation = null;
                IsResolving = false;
            }
        }
    }

    public void CancelAddressLookup()
    {
        ++_addressVersion;
        _addressCancellation?.Cancel();
        _addressCancellation = null;
        if (IsResolving) AddressStatus = "已取消查询";
        IsResolving = false;
    }
}

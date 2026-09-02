namespace FFmpegUtils.Models;

public sealed record ImageInfoField(string Name, string Value, string Hint = "")
{
    public string ToolTip => string.IsNullOrEmpty(Hint) ? Value : $"{Value}\n{Hint}";
}

public sealed record ImageMetadataInfo(
    IReadOnlyList<ImageInfoField> Dimensions,
    IReadOnlyList<ImageInfoField> Shooting,
    IReadOnlyList<ImageInfoField> Location,
    string Warning = "",
    ImageCoordinates? Coordinates = null)
{
    public static ImageMetadataInfo Empty { get; } = new(
        Fields("像素尺寸", "宽高比", "总像素"),
        Fields("拍摄时间", "设备", "镜头", "光圈", "快门", "ISO", "焦距", "等效焦距", "曝光补偿", "闪光灯", "白平衡", "测光模式"),
        Fields("纬度", "经度", "海拔", "拍摄方向"));

    private static ImageInfoField[] Fields(params string[] names)
        => names.Select(name => new ImageInfoField(name, "—")).ToArray();
}

public sealed record ImageCoordinates(double Latitude, double Longitude, string? Datum = null)
{
    public bool IsValid => double.IsFinite(Latitude) && double.IsFinite(Longitude)
        && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;

    // GPS normally uses WGS 84. Do not silently interpret an explicitly different datum.
    public bool IsWgs84 => string.IsNullOrWhiteSpace(Datum)
        || string.Concat(Datum.Where(char.IsLetterOrDigit)).Equals("WGS84", StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;
using FFmpegUtils.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Bmp;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;
using MetadataDirectory = MetadataExtractor.Directory;

namespace FFmpegUtils.Services;

public sealed class ImageMetadataService
{
    public const string NotRecorded = "未记录";
    public const string InvalidValue = "数据无效";
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(new[] { ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".tif", ".tiff", ".bmp", ".gif", ".heic", ".heif", ".avif" });

    public static bool IsSupportedPath(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public Task<ImageMetadataInfo> ReadAsync(string path)
        => Task.Run(() => Read(path));

    public ImageMetadataInfo Read(string path)
    {
        if (!IsSupportedPath(path))
            throw new NotSupportedException("请选择 JPG、PNG、WebP、TIFF、BMP、GIF、HEIC/HEIF 或 AVIF 图片。");

        // Only read the local file. No pixel decoding, metadata rewriting or network requests.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromDirectories(ImageMetadataReader.ReadMetadata(stream));
    }

    public static ImageMetadataInfo FromDirectories(IReadOnlyList<MetadataDirectory> directories)
    {
        var ifd = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var xmp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in directories.OfType<XmpDirectory>())
        foreach (var pair in directory.GetXmpProperties())
            xmp.TryAdd(pair.Key, pair.Value);

        string? Raw(MetadataDirectory? directory, int tag, string? xmpName = null)
            => Text(directory, tag) ?? (xmpName is not null && xmp.TryGetValue(xmpName, out var value) ? Clean(value) : null);

        string Number(MetadataDirectory? directory, int tag, string xmpName, Func<double, string> format,
            Func<double, bool>? valid = null)
        {
            var raw = Raw(directory, tag, xmpName);
            if (raw is null) return NotRecorded;
            return TryNumber(raw, out var value) && (valid?.Invoke(value) ?? true) ? format(value) : InvalidValue;
        }

        string Choice(int tag, string xmpName, Func<int, string> format)
        {
            var raw = Raw(exif, tag, xmpName);
            if (raw is null) return NotRecorded;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? format(value) : InvalidValue;
        }

        var (storedWidth, storedHeight, intrinsicRotation) = ReadDimensions(directories);
        var orientationText = Raw(ifd, ExifDirectoryBase.TagOrientation, "tiff:Orientation");
        _ = int.TryParse(orientationText, out var orientation);
        var swap = intrinsicRotation.HasValue
            ? intrinsicRotation.Value is 90 or 270
            : orientation is 5 or 6 or 7 or 8;
        var width = swap ? storedHeight : storedWidth;
        var height = swap ? storedWidth : storedHeight;
        var gcd = GreatestCommonDivisor(width, height);
        var pixelCount = width * (long)height;
        var dimensions = new ImageInfoField[]
        {
            new("像素尺寸", $"{width} × {height} px", swap
                ? $"已按方向标记校正；文件存储尺寸：{storedWidth} × {storedHeight} px。"
                : $"{width} × {height} 像素；不是 DPI 或打印尺寸。"),
            new("宽高比", $"{width / gcd}:{height / gcd}", width == height ? "正方形" : width > height ? "横向图片" : "竖向图片"),
            new("总像素", pixelCount < 10_000 ? $"{pixelCount:N0} 像素" : $"{pixelCount / 1_000_000d:0.##} MP", $"共 {pixelCount:N0} 像素；1 MP = 100 万像素。")
        };

        var make = Raw(ifd, ExifDirectoryBase.TagMake, "tiff:Make");
        var model = Raw(ifd, ExifDirectoryBase.TagModel, "tiff:Model");
        var device = JoinMakeModel(make, model);
        var lens = JoinMakeModel(Raw(exif, ExifDirectoryBase.TagLensMake, "aux:LensMake"),
            Raw(exif, ExifDirectoryBase.TagLensModel, "exifEX:LensModel")
            ?? (xmp.TryGetValue("aux:Lens", out var lensText) ? Clean(lensText) : null));
        var shooting = new ImageInfoField[]
        {
            ShootingTime(Raw(exif, ExifDirectoryBase.TagDateTimeOriginal, "exif:DateTimeOriginal"), Raw(exif, 0x9011, "exifEX:OffsetTimeOriginal")),
            new("设备", device),
            new("镜头", lens),
            new("光圈", Number(exif, ExifDirectoryBase.TagFNumber, "exif:FNumber", value => $"f/{value:0.##}", value => value > 0)),
            new("快门", Number(exif, ExifDirectoryBase.TagExposureTime, "exif:ExposureTime", value => value < 1 ? $"1/{1 / value:0.###} 秒" : $"{value:0.###} 秒", value => value > 0)),
            new("ISO", Number(exif, ExifDirectoryBase.TagIsoEquivalent, "exif:ISOSpeedRatings[1]", value => $"{value:0}", value => value > 0)),
            new("焦距", Number(exif, ExifDirectoryBase.TagFocalLength, "exif:FocalLength", value => $"{value:0.##} mm", value => value > 0)),
            new("等效焦距", Number(exif, ExifDirectoryBase.Tag35MMFilmEquivFocalLength, "exif:FocalLengthIn35mmFilm", value => value == 0 ? "未知" : $"{value:0.##} mm", value => value >= 0), "35 mm 全画幅等效焦距。"),
            new("曝光补偿", Number(exif, ExifDirectoryBase.TagExposureBias, "exif:ExposureBiasValue", value => $"{value:+0.##;-0.##;0} EV")),
            new("闪光灯", Choice(ExifDirectoryBase.TagFlash, "exif:Flash", value => value < 0 ? InvalidValue : (value & 0x20) != 0 ? "无闪光灯" : (value & 1) != 0 ? "已闪光" : "未闪光")),
            new("白平衡", Choice(ExifDirectoryBase.TagWhiteBalanceMode, "exif:WhiteBalance", value => value switch { 0 => "自动", 1 => "手动", _ => $"未知（{value}）" })),
            new("测光模式", Choice(ExifDirectoryBase.TagMeteringMode, "exif:MeteringMode", value => value switch
            {
                0 => "未知", 1 => "平均测光", 2 => "中央重点", 3 => "点测光", 4 => "多点测光", 5 => "分区测光", 6 => "局部测光", 255 => "其他", _ => $"未知（{value}）"
            }))
        };

        var latitude = Coordinate(gps, true, xmp, out var latitudeNumber);
        var longitude = Coordinate(gps, false, xmp, out var longitudeNumber);
        var location = new ImageInfoField[]
        {
            new("纬度", latitude, "图片内的原始 GPS；只有手动解析地址并确认后才发送坐标。"),
            new("经度", longitude, "地址解析使用完整精度的原始坐标，不使用界面四舍五入后的数值。"),
            new("海拔", Altitude(Raw(gps, GpsDirectory.TagAltitude, "exif:GPSAltitude"), Raw(gps, GpsDirectory.TagAltitudeRef, "exif:GPSAltitudeRef"))),
            new("拍摄方向", Direction(Raw(gps, GpsDirectory.TagImgDirection, "exif:GPSImgDirection"), Raw(gps, GpsDirectory.TagImgDirectionRef, "exif:GPSImgDirectionRef")))
        };
        return new ImageMetadataInfo(dimensions, shooting, location,
            directories.Any(directory => directory.HasError) ? "部分元信息无法解析，已显示可读取的内容。" : "",
            latitudeNumber.HasValue && longitudeNumber.HasValue
                ? new ImageCoordinates(latitudeNumber.Value, longitudeNumber.Value,
                    Raw(gps, GpsDirectory.TagMapDatum, "exif:GPSMapDatum")) : null);
    }

    private static (int Width, int Height, int? Rotation) ReadDimensions(IReadOnlyList<MetadataDirectory> directories)
    {
        // Prefer the primary image header over EXIF copies (which may be stale after resizing).
        foreach (var directory in directories)
        {
            var tags = directory switch
            {
                JpegDirectory => (JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
                PngDirectory => (PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
                WebPDirectory => (WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight),
                GifHeaderDirectory => (GifHeaderDirectory.TagImageWidth, GifHeaderDirectory.TagImageHeight),
                BmpHeaderDirectory => (BmpHeaderDirectory.TagImageWidth, BmpHeaderDirectory.TagImageHeight),
                HeicImagePropertiesDirectory when directory.Name.Contains("Primary", StringComparison.Ordinal) => (HeicImagePropertiesDirectory.TagImageWidth, HeicImagePropertiesDirectory.TagImageHeight),
                _ => (0, 0)
            };
            if (tags != (0, 0) && Size(directory, tags.Item1, tags.Item2) is { } size)
            {
                int? rotation = directory is HeicImagePropertiesDirectory
                    && directory.TryGetInt32(HeicImagePropertiesDirectory.TagRotation, out var angle) ? angle : null;
                return (size.Width, size.Height, rotation);
            }
        }
        foreach (var directory in directories.OfType<ExifIfd0Directory>())
            if (Size(directory, ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight) is { } size)
                return (size.Width, size.Height, null);

        throw new InvalidDataException("未能读取主图尺寸，文件可能已损坏或不受支持。");
    }

    private static (int Width, int Height)? Size(MetadataDirectory directory, int widthTag, int heightTag)
    {
        if (!directory.TryGetInt32(widthTag, out var width) || !directory.TryGetInt32(heightTag, out var height)) return null;
        if (directory is BmpHeaderDirectory && height != int.MinValue) height = Math.Abs(height);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static string Coordinate(GpsDirectory? gps, bool latitude, IReadOnlyDictionary<string, string> xmp,
        out double? signedDegrees)
    {
        signedDegrees = null;
        var tag = latitude ? GpsDirectory.TagLatitude : GpsDirectory.TagLongitude;
        var refTag = latitude ? GpsDirectory.TagLatitudeRef : GpsDirectory.TagLongitudeRef;
        var max = latitude ? 90 : 180;
        double degrees;
        string? reference;
        if (gps?.ContainsTag(tag) == true)
        {
            Rational[]? parts;
            try { parts = gps.GetRationalArray(tag); }
            catch (MetadataException) { return InvalidValue; }
            if (parts is not { Length: 3 }) return InvalidValue;
            var values = parts.Select(part => part.ToDouble()).ToArray();
            if (values.Any(value => !double.IsFinite(value) || value < 0) || values[1] >= 60 || values[2] >= 60) return InvalidValue;
            degrees = values[0] + values[1] / 60 + values[2] / 3600;
            reference = Text(gps, refTag)?.ToUpperInvariant();
            if (reference is null) return "记录不完整";
        }
        else
        {
            var key = latitude ? "exif:GPSLatitude" : "exif:GPSLongitude";
            if (!xmp.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return NotRecorded;
            raw = raw.Trim();
            reference = char.IsLetter(raw[^1]) ? raw[^1..].ToUpperInvariant() : null;
            if (reference is not null) raw = raw[..^1];
            var parts = raw.Split(',');
            if (parts.Length is < 1 or > 3) return InvalidValue;
            var values = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!TryNumber(parts[i], out values[i]) || (i > 0 && (values[i] < 0 || values[i] >= 60))) return InvalidValue;
            if (parts.Length > 1 && values[0] < 0) return InvalidValue;
            degrees = values[0] + (parts.Length > 1 ? values[1] / 60 : 0) + (parts.Length > 2 ? values[2] / 3600 : 0);
            if (reference is null)
            {
                if (parts.Length > 1) return "记录不完整";
                reference = latitude ? degrees < 0 ? "S" : "N" : degrees < 0 ? "W" : "E";
                degrees = Math.Abs(degrees);
            }
        }
        if (!double.IsFinite(degrees) || degrees < 0 || degrees > max) return InvalidValue;
        if (latitude ? reference is not ("N" or "S") : reference is not ("E" or "W")) return InvalidValue;
        var label = reference switch { "N" => "北纬", "S" => "南纬", "E" => "东经", _ => "西经" };
        signedDegrees = reference is "S" or "W" ? -degrees : degrees;
        return $"{label} {degrees.ToString("0.000000", CultureInfo.InvariantCulture)}°";
    }

    private static ImageInfoField ShootingTime(string? raw, string? offset)
    {
        const string name = "拍摄时间";
        if (raw is null) return new(name, NotRecorded, "不使用文件创建时间或修改时间代替拍摄时间。");
        if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var validOffset = offset is not null && System.Text.RegularExpressions.Regex.IsMatch(offset, @"^[+-](0\d|1[0-4]):[0-5]\d$");
            return new(name, time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (validOffset ? $" {offset}" : ""),
                validOffset ? "保留图片记录的时区，不转换为电脑时区。" : "图片未记录有效时区；未进行时区推测或转换。");
        }
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new(name, raw.Replace('T', ' '), "保留图片记录的时间，不使用文件创建时间。");
        return new(name, InvalidValue);
    }

    private static string Altitude(string? raw, string? reference)
    {
        if (raw is null) return NotRecorded;
        if (!TryNumber(raw, out var value) || value < 0) return InvalidValue;
        return reference switch
        {
            "0" => $"{value:0.##} 米", "1" => $"{-value:0.##} 米",
            null => $"{value:0.##} 米（基准未记录）", _ => InvalidValue
        };
    }

    private static string Direction(string? raw, string? reference)
    {
        if (raw is null) return NotRecorded;
        if (!TryNumber(raw, out var value) || value < 0 || value >= 360) return InvalidValue;
        return $"{value:0.##}°（{reference?.ToUpperInvariant() switch { "T" => "真北", "M" => "磁北", _ => "基准未记录" }}）";
    }

    private static bool TryNumber(string raw, out double value)
    {
        var parts = raw.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
            value = numerator / denominator;
        else if (parts.Length != 1 || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        { value = 0; return false; }
        return double.IsFinite(value);
    }

    private static string? Text(MetadataDirectory? directory, int tag)
    {
        if (directory?.ContainsTag(tag) != true) return null;
        try { return Clean(directory.GetString(tag)); }
        catch (MetadataException) { return InvalidValue; }
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = new string(value.Trim().Where(character => !char.IsControl(character)).Take(512).ToArray());
        return value.Length == 0 ? null : value;
    }

    private static string JoinMakeModel(string? make, string? model)
        => model is null ? make ?? NotRecorded : make is null || model.Contains(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

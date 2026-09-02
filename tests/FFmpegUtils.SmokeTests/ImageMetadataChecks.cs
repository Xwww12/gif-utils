using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FFmpegUtils.Models;
using FFmpegUtils.Services;
using FFmpegUtils.ViewModels;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;

internal static class ImageMetadataChecks
{
    internal static ImageMetadataInfo Sample()
    {
        var jpeg = new JpegDirectory();
        jpeg.Set(JpegDirectory.TagImageWidth, 4000);
        jpeg.Set(JpegDirectory.TagImageHeight, 3000);
        var ifd = new ExifIfd0Directory();
        ifd.Set(ExifDirectoryBase.TagMake, "Example");
        ifd.Set(ExifDirectoryBase.TagModel, "Example Camera");
        ifd.Set(ExifDirectoryBase.TagOrientation, 6);
        ifd.Set(ExifDirectoryBase.TagImageWidth, 8000);
        ifd.Set(ExifDirectoryBase.TagImageHeight, 6000);
        var thumbnail = new ExifThumbnailDirectory(0);
        thumbnail.Set(ExifDirectoryBase.TagImageWidth, 160);
        thumbnail.Set(ExifDirectoryBase.TagImageHeight, 120);
        var exif = new ExifSubIfdDirectory();
        exif.Set(ExifDirectoryBase.TagDateTimeOriginal, "2026:09:02 08:30:00");
        exif.Set(0x9011, "+08:00");
        exif.Set(ExifDirectoryBase.TagLensModel, "24–70mm F2.8");
        exif.Set(ExifDirectoryBase.TagExposureTime, new Rational(1, 125));
        exif.Set(ExifDirectoryBase.TagFNumber, new Rational(28, 10));
        exif.Set(ExifDirectoryBase.TagIsoEquivalent, 100);
        exif.Set(ExifDirectoryBase.TagFocalLength, new Rational(50, 1));
        exif.Set(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, 50);
        exif.Set(ExifDirectoryBase.TagExposureBias, new Rational(-1, 3));
        exif.Set(ExifDirectoryBase.TagFlash, 0);
        exif.Set(ExifDirectoryBase.TagWhiteBalanceMode, 0);
        exif.Set(ExifDirectoryBase.TagMeteringMode, 5);
        var gps = Gps("S", "W");
        gps.Set(GpsDirectory.TagAltitude, new Rational(125, 10));
        gps.Set(GpsDirectory.TagAltitudeRef, 1);
        gps.Set(GpsDirectory.TagImgDirection, new Rational(180, 1));
        gps.Set(GpsDirectory.TagImgDirectionRef, "T");
        return ImageMetadataService.FromDirectories([ifd, thumbnail, exif, gps, jpeg]);
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var sample = Sample();
        check(Value(sample.Dimensions, "像素尺寸") == "3000 × 4000 px"
              && Value(sample.Dimensions, "宽高比") == "3:4" && Value(sample.Dimensions, "总像素") == "12 MP",
            "图片主图尺寸优先于缩略图/过期 EXIF，旋转后宽高与比例正确");
        check(Value(sample.Shooting, "拍摄时间") == "2026-09-02 08:30:00 +08:00"
              && Value(sample.Shooting, "设备") == "Example Camera"
              && Value(sample.Shooting, "快门") == "1/125 秒" && Value(sample.Shooting, "光圈") == "f/2.8"
              && Value(sample.Shooting, "白平衡") == "自动" && Value(sample.Shooting, "测光模式") == "分区测光",
            "拍摄时间、设备及常用拍摄参数格式正确");
        check(Value(sample.Location, "纬度") == "南纬 33.500000°"
              && Value(sample.Location, "经度") == "西经 120.250000°"
              && Value(sample.Location, "海拔") == "-12.5 米" && Value(sample.Location, "拍摄方向") == "180°（真北）",
            "GPS 南纬西经、负海拔和拍摄方向正确");

        var header = new JpegDirectory();
        header.Set(JpegDirectory.TagImageWidth, 16);
        header.Set(JpegDirectory.TagImageHeight, 12);
        var modified = new ExifIfd0Directory();
        modified.Set(ExifDirectoryBase.TagDateTime, "2020:01:01 01:02:03");
        var absent = ImageMetadataService.FromDirectories([header, modified]);
        check(Value(absent.Dimensions, "总像素") == "192 像素", "小图总像素不会四舍五入成零");
        check(absent.Shooting.All(field => field.Value == ImageMetadataService.NotRecorded)
              && absent.Location.All(field => field.Value == ImageMetadataService.NotRecorded),
            "缺失拍摄/GPS 信息显示未记录，不用修改时间冒充拍摄时间");

        var zero = Gps("N", "E");
        zero.Set(GpsDirectory.TagLatitude, new[] { new Rational(0, 1), new Rational(0, 1), new Rational(0, 1) });
        zero.Set(GpsDirectory.TagLongitude, new[] { new Rational(0, 1), new Rational(0, 1), new Rational(0, 1) });
        var atZero = ImageMetadataService.FromDirectories([header, zero]);
        check(Value(atZero.Location, "纬度") == "北纬 0.000000°" && Value(atZero.Location, "经度") == "东经 0.000000°",
            "有效零经纬度不会被误判为缺失");
        check(atZero.Coordinates is { Latitude: 0, Longitude: 0 }, "零经纬度保留为可查询坐标");
        zero.Set(GpsDirectory.TagLatitudeRef, "");
        zero.Set(GpsDirectory.TagLongitude, new[] { new Rational(181, 1), new Rational(0, 1), new Rational(0, 1) });
        var invalid = ImageMetadataService.FromDirectories([header, zero]);
        check(Value(invalid.Location, "纬度") == "记录不完整" && Value(invalid.Location, "经度") == ImageMetadataService.InvalidValue,
            "缺少 GPS 方向及越界坐标不会被猜测成正常位置");
        check(invalid.Coordinates is null && absent.Coordinates is null, "缺失、无效坐标不传递给地址服务");
        zero.Set(GpsDirectory.TagLatitudeRef, "N");
        zero.Set(GpsDirectory.TagLatitude, new[] { new Rational(1, 0), new Rational(0, 1), new Rational(0, 1) });
        check(Value(ImageMetadataService.FromDirectories([header, zero]).Location, "纬度") == ImageMetadataService.InvalidValue,
            "损坏 GPS 分母安全降级");

        var badExif = new ExifSubIfdDirectory();
        badExif.Set(ExifDirectoryBase.TagFNumber, new Rational(1, 0));
        badExif.AddError("synthetic test corruption");
        var badFields = ImageMetadataService.FromDirectories([header, badExif]);
        check(Value(badFields.Shooting, "光圈") == ImageMetadataService.InvalidValue && badFields.Warning.Length > 0,
            "部分字段损坏时保留可读尺寸并提示异常");

        var heic = new HeicImagePropertiesDirectory("HEIC Primary Item Properties");
        heic.Set(HeicImagePropertiesDirectory.TagImageWidth, 4032);
        heic.Set(HeicImagePropertiesDirectory.TagImageHeight, 3024);
        heic.Set(HeicImagePropertiesDirectory.TagRotation, 270);
        check(Value(ImageMetadataService.FromDirectories([heic]).Dimensions, "像素尺寸") == "3024 × 4032 px",
            "HEIC 主图内置旋转得到正确尺寸");

        const string xmpXml = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' xmlns:exif='http://ns.adobe.com/exif/1.0/' xmlns:tiff='http://ns.adobe.com/tiff/1.0/' exif:GPSLatitude='33,30S' exif:GPSLongitude='120,15W' exif:DateTimeOriginal='2026-09-02T08:30:00+08:00' tiff:Make='Sample' tiff:Model='XMP Camera' /></rdf:RDF></x:xmpmeta>";
        var xmp = new XmpReader().Extract(Encoding.UTF8.GetBytes(xmpXml));
        var fromXmp = ImageMetadataService.FromDirectories([header, xmp]);
        check(Value(fromXmp.Location, "纬度") == "南纬 33.500000°" && Value(fromXmp.Shooting, "设备") == "Sample XMP Camera",
            "仅包含 XMP 的拍摄信息与坐标也可读取");
        check(fromXmp.Coordinates is { Latitude: -33.5, Longitude: -120.25 }, "XMP 保留有符号数值用于地址解析");

        await CheckFilesAsync(check);
        await CheckViewModelAsync(check, sample, absent);
    }

    private static async Task CheckFilesAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "FFmpegUtilsImageInfoTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var pixels = new byte[16 * 12 * 3];
            var bitmap = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3);
            bitmap.Freeze();
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=271}", "Fixture");
            metadata.SetQuery("/app1/ifd/{ushort=272}", "Fixture Camera");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:09:02 09:00:00");
            metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", RationalValue(1, 125));
            metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
            metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new[] { RationalValue(30, 1), RationalValue(15, 1), RationalValue(0, 1) });
            metadata.SetQuery("/app1/ifd/gps/{ushort=3}", "E");
            metadata.SetQuery("/app1/ifd/gps/{ushort=4}", new[] { RationalValue(120, 1), RationalValue(30, 1), RationalValue(0, 1) });
            var photo = Path.Combine(root, "中文 测试照片.jpg");
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using (var output = File.Create(photo)) encoder.Save(output);
            var before = SHA256.HashData(File.ReadAllBytes(photo));
            var service = new ImageMetadataService();
            var result = await service.ReadAsync(photo);
            check(Value(result.Dimensions, "像素尺寸") == "12 × 16 px"
                  && Value(result.Shooting, "设备") == "Fixture Camera"
                  && Value(result.Shooting, "快门") == "1/125 秒"
                  && Value(result.Location, "纬度") == "北纬 30.250000°"
                  && Value(result.Location, "经度") == "东经 120.500000°",
                "真实 JPEG 文件读取：中文路径、EXIF 拍摄信息和 GPS");
            check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(photo))), "图片信息读取不修改原文件");
            File.Move(photo, photo + ".moved");
            check(File.Exists(photo + ".moved"), "读取结束释放文件，不锁定原图");

            foreach (var (extension, createEncoder) in new (string, Func<BitmapEncoder>)[]
            {
                ("png", () => new PngBitmapEncoder()), ("bmp", () => new BmpBitmapEncoder()),
                ("gif", () => new GifBitmapEncoder()), ("tiff", () => new TiffBitmapEncoder())
            })
            {
                var fileEncoder = createEncoder();
                var path = Path.Combine(root, "plain." + extension);
                fileEncoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) fileEncoder.Save(output);
                var plain = await service.ReadAsync(path);
                check(Value(plain.Dimensions, "像素尺寸") == "16 × 12 px"
                      && plain.Location.All(field => field.Value == ImageMetadataService.NotRecorded),
                    $"真实 {extension.ToUpperInvariant()} 尺寸读取，无 GPS 时明确显示未记录");
            }
            var corrupt = Path.Combine(root, "corrupt.jpg");
            await File.WriteAllTextAsync(corrupt, "not an image");
            var vm = new ImageInfoViewModel();
            await vm.LoadAsync(corrupt);
            check(vm.Error.Length > 0 && !vm.IsReading && ReferenceEquals(vm.Details, ImageMetadataInfo.Empty),
                "损坏图片显示友好错误，不留下旧坐标");
            await vm.LoadAsync(Path.Combine(root, "missing.jpg"));
            check(vm.Error.Contains("不存在"), "不存在的图片有明确错误提示");
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckViewModelAsync(Action<bool, string> check, ImageMetadataInfo first, ImageMetadataInfo last)
    {
        var older = new TaskCompletionSource<ImageMetadataInfo>();
        var newer = new TaskCompletionSource<ImageMetadataInfo>();
        var vm = new ImageInfoViewModel(path => path == "old" ? older.Task : newer.Task);
        var oldLoad = vm.LoadAsync("old");
        check(vm.IsReading && !vm.CanSelect && ReferenceEquals(vm.Details, ImageMetadataInfo.Empty),
            "异步读取有忙碌状态且立即清空旧信息");
        var newLoad = vm.LoadAsync("new");
        newer.SetResult(last);
        await newLoad;
        older.SetResult(first);
        await oldLoad;
        check(vm.InputPath == "new" && ReferenceEquals(vm.Details, last) && !vm.IsReading,
            "较早读取结果不会覆盖后来选中的图片");
    }

    private static GpsDirectory Gps(string latitudeRef, string longitudeRef)
    {
        var gps = new GpsDirectory();
        gps.Set(GpsDirectory.TagLatitudeRef, latitudeRef);
        gps.Set(GpsDirectory.TagLongitudeRef, longitudeRef);
        gps.Set(GpsDirectory.TagLatitude, new[] { new Rational(33, 1), new Rational(30, 1), new Rational(0, 1) });
        gps.Set(GpsDirectory.TagLongitude, new[] { new Rational(120, 1), new Rational(15, 1), new Rational(0, 1) });
        return gps;
    }

    private static ulong RationalValue(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
    private static string Value(IReadOnlyList<ImageInfoField> fields, string name) => fields.Single(field => field.Name == name).Value;
}

using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;
public sealed class ResizeOptions
{
    public int MaxKilobytes { get; set; } = 300;
    public int MaxPixels { get; set; } = 1040;
    public int Quality { get; set; } = 95;
    public bool LongEdge { get; set; }
    public bool Png { get; set; }
    public void Validate()
    {
        if (MaxKilobytes is < 10 or > 20000 || MaxPixels is < 240 or > 12000 || Quality is < 35 or > 100)
            throw new ArgumentException("용량 10~20,000KB, 크기 240~12,000px, JPEG 품질 35~100 범위로 입력하세요.");
    }
}
public sealed class ResizeManifest
{
    public string Kind { get; set; } = "BoramRMSLiteResize";
    public int Version { get; set; } = 1;
    public string SourceRoot { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ResizeOptions Options { get; set; } = new();
    public int Requested { get; set; }
    public bool Cancelled { get; set; }
    public List<ResizeEntry> Items { get; set; } = new();
}
public sealed class ResizeEntry
{
    public string SourceRelative { get; set; } = "";
    public string OutputRelative { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string OutputHash { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public long Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
public static class ImageProcessing
{
    public static BitmapSource Load(string path, int maxEdge = 0)
    {
        SafePaths.NoLinks(path);
        var file = new FileInfo(path);
        if (file.Length > 160 * 1024 * 1024) throw new IOException("160MB보다 큰 이미지는 안전을 위해 제외합니다.");
        BitmapFrame frame;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new NotSupportedException("여러 페이지/애니메이션 이미지는 지원하지 않습니다. 단일 이미지로 나누어 주세요.");
            frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 60000000) throw new NotSupportedException("6,000만 화소를 초과하는 이미지는 제외합니다.");
            frame.Freeze();
        }
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata md)
        {
            foreach (var key in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try { var v = md.GetQuery(key); if (v != null) { orientation = Convert.ToInt32(v); break; } } catch (NotSupportedException) { }
            }
        }
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
        };
        BitmapSource source = frame;
        if (!matrix.IsIdentity) { source = new TransformedBitmap(frame, new MatrixTransform(matrix)); source.Freeze(); }
        if (maxEdge > 0) source = Scale(source, Math.Min(1.0, (double)maxEdge / Math.Max(source.PixelWidth, source.PixelHeight)));
        return source;
    }
    public static BitmapSource Scale(BitmapSource src, double scale)
    {
        if (scale >= 0.999999) return src;
        var result = new TransformedBitmap(src, new ScaleTransform(scale, scale)); result.Freeze(); return result;
    }
    public static BitmapSource WhiteBackground(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgra.PixelWidth * 4); var data = new byte[checked(stride * bgra.PixelHeight)]; bgra.CopyPixels(data, stride, 0);
        for (int i = 0; i < data.Length; i += 4)
        {
            var a = data[i + 3];
            data[i] = (byte)((data[i] * a + 255 * (255 - a) + 127) / 255);
            data[i + 1] = (byte)((data[i + 1] * a + 255 * (255 - a) + 127) / 255);
            data[i + 2] = (byte)((data[i + 2] * a + 255 * (255 - a) + 127) / 255); data[i + 3] = 255;
        }
        var image = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, data, stride); image.Freeze(); return image;
    }
    public static byte[] Encode(BitmapSource src, string extension, int quality = 95)
    {
        BitmapEncoder encoder = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = quality }, ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(), ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new NotSupportedException("저장할 수 없는 이미지 형식입니다: " + extension)
        };
        encoder.Frames.Add(BitmapFrame.Create(src)); using var ms = new MemoryStream(); encoder.Save(ms); return ms.ToArray();
    }
    public static (byte[] Bytes, int Width, int Height) Compress(BitmapSource original, ResizeOptions options, CancellationToken token)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        var edge = options.LongEdge ? Math.Max(original.PixelWidth, original.PixelHeight) : original.PixelWidth;
        var current = Scale(original, Math.Min(1, (double)options.MaxPixels / edge));
        if (!options.Png) current = WhiteBackground(current);
        for (int attempt = 0; attempt < 22; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (options.Png)
            {
                var data = Encode(current, ".png");
                if (data.LongLength <= options.MaxKilobytes * 1024L) return (data, current.PixelWidth, current.PixelHeight);
            }
            else
            {
                byte[]? best = null; int lo = 35, hi = options.Quality;
                while (lo <= hi)
                {
                    token.ThrowIfCancellationRequested(); int quality = (lo + hi) / 2;
                    var data = Encode(current, ".jpg", quality);
                    if (data.LongLength <= options.MaxKilobytes * 1024L) { best = data; lo = quality + 1; } else hi = quality - 1;
                }
                if (best != null) return (best, current.PixelWidth, current.PixelHeight);
            }
            if (Math.Max(current.PixelWidth, current.PixelHeight) <= 240) break;
            current = Scale(current, 0.85);
        }
        throw new IOException("최소 가독성 크기 안에서 목표 용량을 맞추지 못했습니다. 용량 제한을 늘려 주세요.");
    }
    public static ResizeManifest ResizeBatch(FolderContext context, IReadOnlyList<ImageItem> inputs, string output, ResizeOptions options, IProgress<ResizeEntry>? progress, CancellationToken token)
    {
        options.Validate(); token.ThrowIfCancellationRequested(); output = Path.GetFullPath(output); SafePaths.NoLinks(output);
        if (inputs.Count == 0) throw new ArgumentException("처리할 이미지가 없습니다.");
        if (SafePaths.Same(output, context.Root) || inputs.Any(i => SafePaths.Under(i.FullPath, output))) throw new IOException("원본을 포함하는 폴더를 출력 위치로 사용할 수 없습니다.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("원본 보호를 위해 새 폴더 또는 빈 폴더를 선택하세요.");
        Directory.CreateDirectory(output);
        var manifest = new ResizeManifest { SourceRoot = context.Root, Requested = inputs.Count, Options = options };
        void Save() => FileChanges.AtomicWrite(Path.Combine(output, LocalData.OutputMarker), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, SettingsStore.Json)));
        Save();
        foreach (var item in inputs)
        {
            if (token.IsCancellationRequested) { manifest.Cancelled = true; break; }
            var entry = new ResizeEntry { SourceRelative = Path.GetRelativePath(context.Root, item.FullPath) };
            string? partial = null;
            try
            {
                if (!SafePaths.Under(item.FullPath, context.Root)) throw new IOException("다른 폴더의 입력 이미지입니다.");
                LocalData.EnsureUnchanged(item); entry.SourceHash = SafePaths.Hash(item.FullPath);
                var src = Load(item.FullPath); var result = Compress(src, options, token);
                token.ThrowIfCancellationRequested();
                if (SafePaths.Hash(item.FullPath) != entry.SourceHash) throw new IOException("변환 중 원본이 변경되어 저장하지 않았습니다.");
                var rel = Path.ChangeExtension(entry.SourceRelative, options.Png ? ".png" : ".jpg");
                var dest = Path.GetFullPath(Path.Combine(output, rel));
                if (!SafePaths.Under(dest, output)) throw new IOException("잘못된 출력 경로입니다.");
                dest = SafePaths.Unique(dest); SafePaths.NoLinks(dest); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                partial = Path.Combine(Path.GetDirectoryName(dest)!, "." + Guid.NewGuid().ToString("N") + ".partial");
                using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(result.Bytes); fs.Flush(true); }
                if (new FileInfo(partial).Length > options.MaxKilobytes * 1024L) throw new IOException("출력 파일 용량 검증에 실패했습니다.");
                token.ThrowIfCancellationRequested(); File.Move(partial, dest); partial = null;
                entry.OutputRelative = Path.GetRelativePath(output, dest); entry.Bytes = new FileInfo(dest).Length;
                entry.OutputHash = SafePaths.Hash(dest); entry.Width = result.Width; entry.Height = result.Height; entry.Success = true;
            }
            catch (OperationCanceledException) { entry.Error = "취소됨"; manifest.Cancelled = true; }
            catch (Exception ex) { entry.Error = ex.Message; }
            finally { if (partial != null && File.Exists(partial)) File.Delete(partial); }
            manifest.Items.Add(entry); Save(); progress?.Report(entry);
            if (manifest.Cancelled) break;
        }
        Save(); return manifest;
    }
    public static void Rotate(FolderContext ctx, ImageItem item, bool clockwise)
    {
        LocalData.EnsureUnchanged(item);
        var change = new FileChanges(LocalData.JournalRoot(ctx), ctx.Root); change.Expect(item.FullPath);
        var src = Load(item.FullPath); var rotated = new TransformedBitmap(src, new RotateTransform(clockwise ? 90 : 270)); rotated.Freeze();
        change.Write(item.FullPath, Encode(rotated, Path.GetExtension(item.FullPath), 95));
        change.Apply("원본 90도 회전 (이전 파일 백업)");
    }
}

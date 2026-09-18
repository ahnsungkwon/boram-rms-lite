using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public sealed record CompressionEntry(string Path, bool Changed, bool Skipped, long Before, long After, string Error);
public sealed class CompressionResult
{
    public List<CompressionEntry> Entries { get; } = new();
    public bool Cancelled { get; set; }
}
public static class InPlaceCompression
{
    // Strictly below 300KB under both decimal KB and binary KiB conventions.
    public const long LimitBytes = 300000;
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
    public static CompressionResult Run(FolderContext context, IReadOnlyList<ImageItem> items, IProgress<CompressionEntry>? progress, CancellationToken token)
    {
        var result = new CompressionResult();
        // Direct image compression is independent from legacy RMS state journals.
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) { result.Cancelled = true; break; }
            CompressionEntry entry;
            try { entry = CompressOne(context, item, token); }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            catch (Exception ex) { entry = new(item.FullPath, false, false, item.Length, item.Length, ex.Message); }
            result.Entries.Add(entry); progress?.Report(entry);
        }
        return result;
    }
    public static CompressionEntry CompressOne(FolderContext context, ImageItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SafePaths.Under(item.FullPath, context.Root)) throw new IOException("선택한 작업 폴더 밖의 파일입니다.");
        LocalData.EnsureUnchanged(item);
        var extension = System.IO.Path.GetExtension(item.FullPath);
        if (!Supported.Contains(extension)) throw new NotSupportedException("원래 형식을 유지하는 직접 압축을 지원하지 않습니다: " + extension);
        if ((File.GetAttributes(item.FullPath) & FileAttributes.ReadOnly) != 0) throw new IOException("읽기 전용 파일은 변경하지 않습니다.");
        var expectedHash = SafePaths.Hash(item.FullPath);
        var image = ImageProcessing.Load(item.FullPath);
        token.ThrowIfCancellationRequested();
        if (item.Length < LimitBytes) return new(item.FullPath, false, true, item.Length, item.Length, "");
        var compressed = CompressSameFormat(image, extension, token);
        if (compressed.Length >= LimitBytes || compressed.Length >= item.Length) throw new IOException("목표 용량으로 줄이지 못해 기존 파일을 유지했습니다.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.FullPath)!, ".rmslite-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(compressed); stream.Flush(true); }
            var check = ImageProcessing.Load(temp);
            if (check.PixelWidth < 1 || check.PixelHeight < 1 || new FileInfo(temp).Length != compressed.Length) throw new IOException("압축 이미지 검증 실패");
            token.ThrowIfCancellationRequested(); LocalData.EnsureUnchanged(item);
            // Deny concurrent writes while validating the old bytes; allow the atomic replacement.
            using (var original = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (!Convert.ToHexString(SHA256.HashData(original)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("압축 중 외부에서 수정되어 저장하지 않았습니다.");
                token.ThrowIfCancellationRequested();
                File.Replace(temp, item.FullPath, null);
            }
            return new(item.FullPath, true, false, item.Length, compressed.Length, "");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static byte[] CompressSameFormat(BitmapSource source, string extension, CancellationToken token)
    {
        var jpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        var current = source;
        var minEdge = Math.Min(360, Math.Max(source.PixelWidth, source.PixelHeight));
        for (var attempt = 0; attempt < 32; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (jpeg)
            {
                byte[]? best = null; var lo = 55; var hi = 95;
                while (lo <= hi)
                {
                    token.ThrowIfCancellationRequested(); var quality = (lo + hi) / 2;
                    var bytes = ImageProcessing.Encode(current, extension, quality);
                    if (bytes.Length < LimitBytes) { best = bytes; lo = quality + 1; } else hi = quality - 1;
                }
                if (best != null) return best;
            }
            else
            {
                var bytes = ImageProcessing.Encode(current, extension);
                if (bytes.Length < LimitBytes) return bytes;
            }
            var edge = Math.Max(current.PixelWidth, current.PixelHeight);
            if (edge <= minEdge) break;
            current = ImageProcessing.Scale(current, Math.Max(.85, (double)minEdge / edge));
        }
        throw new IOException("긴 변 360px 이상을 유지하며 300KB 미만으로 줄이지 못했습니다. 원본은 유지합니다.");
    }
}

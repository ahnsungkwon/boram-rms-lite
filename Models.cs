using System.IO;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;
public sealed class ImageItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    // Save applies a complete snapshot before one row notification. Selection,
    // virtualization and the current bitmap need not be reset for a checkbox.
    internal void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    private static readonly ConcurrentDictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    public string FullPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string Quota { get; set; } = "";
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public bool Card { get; set; }
    public string? LiteRevision { get; set; }
    public LiteSelections? Selections { get; set; }
    public string StateNotice { get; set; } = "";
    public long Length { get; set; }
    public long ModifiedTicks { get; set; }
    public string Account => Card ? "카드" : "CMS";
    public string StatusLabel => $"[{Account}] {Status}".Trim();
    public string Registration => "등록 여부 확인 안 됨";
    public string QuotaLabel => Quota.Length == 0 ? "구좌 미확인" : Quota + "구좌";
    public string Key => Quota + Name;
    public BitmapSource? Thumbnail { get; set; }
    public Brush QuotaBrush => Brush(Quota switch { "1" => "#557F45", "2" => "#258573", "3" => "#C08228", "4" => "#C44A48", _ => "#86589B" });
    public Brush StatusBackground => Brush(Status.Contains("취소") || Status == "오등록" ? "#FCE3E1" : Status.Contains("보완") ? "#FFF1CA" : Status.Contains("변경") ? "#DCF1EC" : Card ? "#EEE7F7" : "#EDF5E8");
    public Brush StatusForeground => Brush(Status.Contains("취소") || Status == "오등록" ? "#982E2C" : Status.Contains("보완") ? "#825717" : "#314B29");
    public static Brush Brush(string hex) => Brushes.GetOrAdd(hex, static color =>
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!;
        brush.Freeze();
        return brush;
    });
    public static (string Quota, string Name) Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"^(\d{1,2})\s*(.+)$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.Trim()) : ("", stem);
    }
}
public sealed class FolderContext
{
    public string Root { get; init; } = "";
    public string? EventRoot { get; init; }
    public string? ProcessedRoot { get; init; }
    public string Title => EventRoot == null ? new DirectoryInfo(Root).Name : new DirectoryInfo(EventRoot).Name;
    public string? TextPath => EventRoot == null ? null : Path.Combine(EventRoot, new DirectoryInfo(EventRoot).Name + ".txt");
    public string Metadata => Path.Combine(Root, ".boramrms");
    public static FolderContext Resolve(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("폴더를 찾을 수 없습니다: " + path);
        if (Directory.Exists(Path.Combine(path, "신청서", "[원본]"))) path = Path.Combine(path, "신청서", "[원본]");
        else if (Directory.Exists(Path.Combine(path, "[원본]"))) path = Path.Combine(path, "[원본]");
        SafePaths.NoLinks(path);
        var d = new DirectoryInfo(path);
        return new FolderContext { Root = d.FullName, ProcessedRoot = d.Name == "[원본]" && d.Parent?.Name == "신청서" ? d.Parent.FullName : null,
            EventRoot = d.Name == "[원본]" && d.Parent?.Name == "신청서" ? d.Parent.Parent?.FullName : null };
    }
}
public static class SafePaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);
    public static bool Under(string path, string root) => Same(path, root) || Normalize(path).StartsWith(Normalize(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void NoLinks(string path)
    {
        var p = Path.GetFullPath(path);
        while (p != null)
        {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결/심볼릭 경로는 안전을 위해 지원하지 않습니다: " + p);
            p = Path.GetDirectoryName(p);
        }
    }
    public static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        for (int n = 2; n < 10000; n++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + $" ({n})" + Path.GetExtension(path));
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("중복 파일명이 너무 많습니다.");
    }
    public static string NormalizeQuotaName(string text)
    {
        var value = text.Trim().ToUpperInvariant();
        // Pasted name-then-quota input is supported as well as individual digit keystrokes.
        var match = Regex.Match(value, @"^([^\d]+?)\s*([1-9][0-9]?)$");
        return match.Success ? match.Groups[2].Value + match.Groups[1].Value.Trim() : value;
    }
    public static string ValidName(string text)
    {
        var value = NormalizeQuotaName(text);
        if (!Regex.IsMatch(value, @"^[1-9]\d?(?!\d)\s*[^\d\s].*$") || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.EndsWith('.') || value.Length > 170)
            throw new ArgumentException("1~99 구좌와 이름을 입력하세요. 예: 3홍길동. 파일명에 사용할 수 없는 문자는 제외해 주세요.");
        return Regex.Replace(value, @"^(\d{1,2})\s+", "$1");
    }
}
public sealed class FolderLease : IDisposable
{
    private readonly Mutex _mutex;
    public bool Writable { get; }
    public FolderLease(string root)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SafePaths.Normalize(root).ToUpperInvariant())));
        _mutex = new Mutex(false, "Local\\BoramRMSLite_" + key);
        try { Writable = _mutex.WaitOne(0); } catch (AbandonedMutexException) { Writable = true; }
    }
    public void Dispose() { if (Writable) _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
public sealed record ImageViewLayout(double Width, double Height, double LeftWidth, double RightWidth, bool LogExpanded, bool Detached);
public sealed class ImageCamera
{
    public double Scale { get; set; } = 1;
    public double X { get; set; }
    public double Y { get; set; }
    public double BaseWidth { get; set; }
    public ImageViewLayout? Layout { get; set; }
}
public sealed class WorkTab : IDisposable
{
    public FolderContext Context { get; }
    public FolderLease Lease { get; }
    public string Title => Context.Title + (Lease.Writable ? "" : " · 읽기 전용");
    public string? SelectedPath { get; set; }
    public ImageCamera Camera { get; } = new();
    public Dictionary<string,int> Order { get; } = new(StringComparer.OrdinalIgnoreCase);
    public WorkTab(FolderContext context) { Context = context; Lease = new FolderLease(context.Root); }
    public void Dispose() => Lease.Dispose();
}
public sealed class SavedSettings
{
    public List<string> Folders { get; set; } = new();
    public Dictionary<string,string> Selected { get; set; } = new();
    public double Width { get; set; } = 1520;
    public double Height { get; set; } = 960;
    public double LeftWidth { get; set; } = 316;
    public double RightWidth { get; set; } = 288;
    public bool Thumbnails { get; set; }
}
public static class SettingsStore
{
    public static string DirectoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoramRMSLite");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static SavedSettings Load() { var p = Path.Combine(DirectoryPath, "settings.json"); try { return File.Exists(p) ? JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(p), Json) ?? new() : new(); } catch { return new(); } }
    public static void Save(SavedSettings value)
    {
        Directory.CreateDirectory(DirectoryPath); var p = Path.Combine(DirectoryPath, "settings.json");
        FileChanges.AtomicWrite(p, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)));
    }
}

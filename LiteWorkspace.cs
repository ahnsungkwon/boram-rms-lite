using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public sealed record LiteSelections(string Repairs = "", string Flags = "", string RepairMemo = "", string Memo = "", string PreQuota = "", string PostQuota = "");
public sealed record LiteState(string Status, string Details, bool Card, LiteSelections? Selections = null)
{
    public static LiteState From(ImageItem item) => new(item.Status, item.Details, item.Card, item.Selections);
}
public sealed class LiteRecord
{
    public int Schema { get; set; } = 1;
    public string FileName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public bool Card { get; set; }
    public LiteSelections? Selections { get; set; }
    public string? RenameFrom { get; set; }
    public string? ImageHash { get; set; }
}
public sealed record LiteEdit(string BeforePath, string AfterPath, LiteState BeforeState,
    string BeforeImageHash, string AfterImageHash, string? AfterStateRevision, string? ImageBackup = null);
public sealed record LiteSaveResult(string Path, LiteEdit? Edit);

// Each image has its own state record. The legacy multi-file journal is never consulted.
public static class LiteWorkspace
{
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string? Revision(string path) => File.Exists(path) ? SafePaths.Hash(path) : null;
    public static string StoreDirectory(string image) => Path.Combine(Path.GetDirectoryName(image)!, ".rmslite");
    public static string StatePath(string image) => Path.Combine(StoreDirectory(image),
        Digest(Encoding.UTF8.GetBytes(Path.GetFileName(image).ToUpperInvariant())) + ".json");
    private static FileStream Lock(string image)
    {
        var dir = StoreDirectory(image); SafePaths.NoLinks(dir); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "write.lock"); SafePaths.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("이 폴더에서 다른 저장이 진행 중입니다. 잠시 후 다시 저장하세요.", ex); }
    }
    private static void CheckImage(FolderContext context, ImageItem item)
    {
        if (!SafePaths.Under(item.FullPath, context.Root) || !LocalData.Extensions.Contains(Path.GetExtension(item.FullPath)))
            throw new IOException("현재 작업 폴더의 이미지가 아닙니다.");
        LocalData.EnsureUnchanged(item);
        if ((File.GetAttributes(item.FullPath) & FileAttributes.ReadOnly) != 0)
            throw new IOException("이 이미지가 읽기 전용입니다. 파일 속성을 확인하세요.");
    }
    private static LiteRecord Record(string path, LiteState state, string? from = null, string? hash = null) =>
        new() { FileName = Path.GetFileName(path), Status = state.Status, Details = state.Details, Card = state.Card, Selections = state.Selections, RenameFrom = from, ImageHash = hash };
    private static void WriteRecord(string image, LiteRecord record, string? expected)
    {
        var path = StatePath(image); SafePaths.NoLinks(path);
        if (Revision(path) != expected) throw new IOException("이 이미지의 상태가 다른 창에서 바뀌었습니다. 새로고침 후 저장하세요.");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, SettingsStore.Json));
        if (Digest(bytes) == expected) return;
        AtomicReplace(path, bytes, expected, path + ".previous");
    }
    private static void AtomicReplace(string path, byte[] bytes, string? expected, string? backup)
    {
        SafePaths.NoLinks(path); if (backup != null) SafePaths.NoLinks(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".lite-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (Revision(path) != expected) throw new IOException("저장 중 다른 프로그램에서 파일이 바뀌었습니다. 기존 파일은 덮어쓰지 않았습니다.");
            if (expected == null) File.Move(temp, path);
            else File.Replace(temp, path, backup);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static List<ImageItem> Load(FolderContext context, CancellationToken token = default)
    {
        Dictionary<string, LocalState> oldStates = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> oldCards = new(StringComparer.OrdinalIgnoreCase);
        // Read-only compatibility. No TXT lookup, no pending-journal gate, no migration writes.
        try { oldStates = LocalData.ReadStates(context); } catch { }
        try { oldCards = LocalData.ReadCards(context); } catch { }
        var items = new List<ImageItem>();
        foreach (var path in LocalData.Enumerate(context.Root, token))
        {
            token.ThrowIfCancellationRequested(); var file = new FileInfo(path);
            var rel = Path.GetRelativePath(context.Root, path); var parsed = ImageItem.Parse(file.Name);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            var old = oldStates.GetValueOrDefault(rel);
            var item = new ImageItem { FullPath = path, RelativePath = rel, Quota = parsed.Quota, Name = parsed.Name,
                Length = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks, Card = oldCards.Contains(rel) || parts.Contains("카드"),
                Status = old?.Status ?? parts.FirstOrDefault(p => p is "보완" or "주말미등록" or "취소" or "추가") ?? "", Details = old?.Details ?? "" };
            var statePath = StatePath(path);
            try
            {
                SafePaths.NoLinks(statePath);
                if (File.Exists(statePath))
                {
                    var bytes = File.ReadAllBytes(statePath); item.LiteRevision = Digest(bytes);
                    var record = JsonSerializer.Deserialize<LiteRecord>(bytes, SettingsStore.Json) ?? throw new InvalidDataException();
                    if (record.Schema != 1 || !record.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                    if (record.RenameFrom != null)
                    {
                        if (Path.GetFileName(record.RenameFrom) != record.RenameFrom || record.ImageHash != SafePaths.Hash(path) || File.Exists(Path.Combine(file.DirectoryName!, record.RenameFrom)))
                            throw new InvalidDataException("이 이미지의 이전 리네임 상태는 확정되지 않았습니다.");
                    }
                    item.Status = record.Status ?? ""; item.Details = record.Details ?? ""; item.Card = record.Card; item.Selections = record.Selections;
                }
            }
            catch (Exception ex)
            {
                item.Status = ""; item.Details = ""; item.Card = parts.Contains("카드");
                item.StateNotice = "이 이미지의 상태 기록을 읽지 못했습니다. 상태를 다시 선택해 저장하면 이전 기록을 보존하고 새로 저장합니다. " + ex.Message;
            }
            items.Add(item);
        }
        return items.OrderBy(i => int.TryParse(i.Quota, out var q) ? q : int.MaxValue).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).Select((i, n) => { i.Index = n + 1; return i; }).ToList();
    }
    public static LiteSaveResult Save(FolderContext context, ImageItem item, string? name, LiteState state)
    {
        var target = name == null ? item.FullPath : Path.Combine(Path.GetDirectoryName(item.FullPath)!, SafePaths.ValidName(name) + Path.GetExtension(item.FullPath));
        return SaveTarget(context, item, target, state);
    }
    private static LiteSaveResult SaveTarget(FolderContext context, ImageItem item, string target, LiteState state)
    {
        CheckImage(context, item);
        if (!SafePaths.Same(Path.GetDirectoryName(item.FullPath)!, Path.GetDirectoryName(target)!)) throw new IOException("Lite 리네임은 같은 폴더 안에서만 수행합니다.");
        if (state.Status.Length > 256 || state.Details.Length > 10000) throw new ArgumentException("상태 메모가 너무 깁니다.");
        var rename = !SafePaths.Same(item.FullPath, target);
        if (rename && (File.Exists(target) || Directory.Exists(target))) throw new IOException("같은 이름의 파일이 있습니다. 다른 이름을 입력하세요.");
        var before = LiteState.From(item);
        if (!rename && before == state && item.StateNotice.Length == 0) return new(item.FullPath, null);
        using var gate = Lock(item.FullPath); CheckImage(context, item);
        if (Revision(StatePath(item.FullPath)) != item.LiteRevision) throw new IOException("다른 창에서 상태가 바뀌었습니다. 새로고침 후 다시 저장하세요.");
        var imageHash = SafePaths.Hash(item.FullPath);
        if (rename)
        {
            var staged = Record(target, state, item.FileName, imageHash);
            WriteRecord(target, staged, Revision(StatePath(target)));
            CheckImage(context, item);
            if (SafePaths.Hash(item.FullPath) != imageHash) throw new IOException("저장 중 이미지가 변경되어 이름을 바꾸지 않았습니다.");
            // If interrupted here, the original image and original state are untouched.
            File.Move(item.FullPath, target);
            // A staged record is readable only when the source is absent and target bytes match.
            // Finalization failure does not invalidate the already completed rename + state.
            try { WriteRecord(target, Record(target, state), Revision(StatePath(target))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        else WriteRecord(target, Record(target, state), item.LiteRevision);
        return new(target, new(item.FullPath, target, before, imageHash, imageHash, Revision(StatePath(target))));
    }
    public static LiteEdit Rotate(FolderContext context, ImageItem item, bool clockwise)
    {
        CheckImage(context, item); using var gate = Lock(item.FullPath);
        var hash = SafePaths.Hash(item.FullPath); var image = ImageProcessing.Load(item.FullPath);
        var rotated = new TransformedBitmap(image, new RotateTransform(clockwise ? 90 : 270)); rotated.Freeze();
        var bytes = ImageProcessing.Encode(rotated, Path.GetExtension(item.FullPath), 95);
        var backup = Path.Combine(StoreDirectory(item.FullPath), Guid.NewGuid().ToString("N") + ".rotation.bak");
        CheckImage(context, item); AtomicReplace(item.FullPath, bytes, hash, backup);
        return new(item.FullPath, item.FullPath, LiteState.From(item), hash, Digest(bytes), Revision(StatePath(item.FullPath)), backup);
    }
    public static string Undo(FolderContext context, LiteEdit edit)
    {
        if (!SafePaths.Under(edit.BeforePath, context.Root) || !SafePaths.Under(edit.AfterPath, context.Root)) throw new IOException("다른 폴더의 작업은 되돌리지 않습니다.");
        if (Revision(edit.AfterPath) != edit.AfterImageHash || Revision(StatePath(edit.AfterPath)) != edit.AfterStateRevision)
            throw new IOException("그 이후에 이미지 또는 상태가 바뀌어 덮어쓰지 않았습니다.");
        var current = Load(context).Single(i => SafePaths.Same(i.FullPath, edit.AfterPath));
        if (edit.ImageBackup != null)
        {
            if (!SafePaths.Under(edit.ImageBackup, StoreDirectory(edit.AfterPath)) || Revision(edit.ImageBackup) != edit.BeforeImageHash)
                throw new IOException("이전 이미지 백업을 검증하지 못했습니다.");
            using var gate = Lock(edit.AfterPath); CheckImage(context, current);
            AtomicReplace(edit.AfterPath, File.ReadAllBytes(edit.ImageBackup), edit.AfterImageHash, null);
            return edit.AfterPath;
        }
        return SaveTarget(context, current, edit.BeforePath, edit.BeforeState).Path;
    }
}

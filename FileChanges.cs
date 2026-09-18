using System.IO;
using System.Text;
using System.Text.Json;
namespace BoramRms.Lite;

// A durable per-operation journal. Not a database transaction: a power failure is
// detected on the next write and requires an explicit, verified recovery.
public sealed partial class FileChanges
{
    private readonly List<(string Source, string Target)> _moves = new();
    private readonly Dictionary<string, byte[]> _writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _expected = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _roots;
    private readonly string _journalRoot;
    public string? JournalPath { get; private set; }
    public FileChanges(string journalRoot, params string[] roots) { _journalRoot = journalRoot; _roots = roots.Select(SafePaths.Normalize).ToArray(); }
    private void CheckPath(string path)
    {
        if (!_roots.Any(root => SafePaths.Under(path, root))) throw new IOException("작업 범위를 벗어난 경로입니다: " + path);
        SafePaths.NoLinks(path);
    }
    public byte[]? Read(string path)
    {
        CheckPath(path);
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        _expected.TryAdd(path, bytes == null ? null : Hash(bytes));
        return bytes;
    }
    public void Expect(string path) { if (!_expected.ContainsKey(path)) _ = Read(path); }
    public void Move(string source, string target)
    {
        CheckPath(source); CheckPath(target);
        if (SafePaths.Same(source, target)) return;
        Expect(source); Expect(target);
        if (_expected[source] == null) throw new FileNotFoundException("원본 파일이 없습니다.", source);
        if (_expected[target] != null && !_moves.Any(m => SafePaths.Same(m.Source, target))) throw new IOException("대상 파일을 덮어쓰지 않습니다: " + target);
        if (_moves.Any(m => SafePaths.Same(m.Target, target))) throw new IOException("작업 내 대상 경로가 중복됩니다.");
        _moves.Add((source, target));
    }
    public void Write(string path, byte[] bytes)
    {
        CheckPath(path); Expect(path);
        if (_expected[path] == Hash(bytes) && !_moves.Any(m => SafePaths.Same(m.Target, path))) _writes.Remove(path);
        else _writes[path] = bytes;
    }
    public void WriteJson<T>(string path, T data) => Write(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, SettingsStore.Json)));
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private static string? CurrentHash(string path) => File.Exists(path) ? SafePaths.Hash(path) : null;
    public string Apply(string description)
    {
        CheckPending(_journalRoot);
        foreach (var p in _expected)
            if (CurrentHash(p.Key) != p.Value) throw new IOException("파일이 다른 프로그램에서 변경되었습니다. 새로고침 후 다시 시도하세요: " + p.Key);
        var touched = _moves.SelectMany(m => new[] { m.Source, m.Target }).Concat(_writes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (touched.Length == 0) return "";
        var dir = Path.Combine(_journalRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        JournalPath = Path.Combine(dir, "journal.json");
        var journal = new ChangeJournal { Description = description, Roots = _roots, State = "preparing" };
        try
        {
            int i = 0;
            foreach (var p in touched)
            {
                var exists = File.Exists(p);
                var record = new FileSnapshot { Path = p, BeforeHash = _expected[p], AfterHash = _expected[p], ModifiedUtc = exists ? File.GetLastWriteTimeUtc(p) : default };
                if (exists)
                {
                    record.Backup = $"{i++:0000}.bak";
                    File.Copy(p, Path.Combine(dir, record.Backup));
                    if (SafePaths.Hash(Path.Combine(dir, record.Backup)) != record.BeforeHash) throw new IOException("백업 중 원본이 변경되었습니다.");
                }
                journal.Files.Add(record);
            }
            journal.State = "applying"; SaveJournal(JournalPath, journal);
            foreach (var p in _expected)
                if (CurrentHash(p.Key) != p.Value) throw new IOException("변경 전 검사 실패: " + p.Key);
            foreach (var m in _moves)
            {
                CheckPath(m.Source); CheckPath(m.Target);
                if (CurrentHash(m.Source) != _expected[m.Source] || File.Exists(m.Target)) throw new IOException("이동 직전에 파일이 바뀌었거나 대상이 생성되었습니다.");
                Directory.CreateDirectory(Path.GetDirectoryName(m.Target)!);
                File.Move(m.Source, m.Target);
                journal.Files.First(f => SafePaths.Same(f.Path, m.Source)).AfterHash = null;
                journal.Files.First(f => SafePaths.Same(f.Path, m.Target)).AfterHash = _expected[m.Source];
                SaveJournal(JournalPath, journal);
            }
            foreach (var w in _writes)
            {
                var r = journal.Files.First(f => SafePaths.Same(f.Path, w.Key));
                if (CurrentHash(w.Key) != r.AfterHash) throw new IOException("쓰기 전에 파일이 변경되었습니다: " + w.Key);
                AtomicWrite(w.Key, w.Value);
                r.AfterHash = Hash(w.Value); SaveJournal(JournalPath, journal);
            }
            journal.State = "completed"; SaveJournal(JournalPath, journal);
            return JournalPath;
        }
        catch (Exception original)
        {
            try
            {
                if (journal.State == "applying") { SaveJournal(JournalPath, journal); Undo(JournalPath, allowPending: true); }
                else { journal.State = "cancelled-before-change"; SaveJournal(JournalPath, journal); }
            }
            catch (Exception recovery) { throw new IOException($"작업 실패: {original.Message}\n자동 복구도 중단되었습니다: {recovery.Message}\n복구 기록: {JournalPath}", original); }
            throw new IOException("작업을 완료하지 못해 변경 전 상태로 복구했습니다: " + original.Message, original);
        }
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(bytes); fs.Flush(true); }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    private static void SaveJournal(string path, ChangeJournal data) => AtomicWrite(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, SettingsStore.Json)));
    public static string? LatestCompleted(string journalRoot)
    {
        if (!Directory.Exists(journalRoot)) return null;
        return Directory.EnumerateFiles(journalRoot, "journal.json", SearchOption.AllDirectories).OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault(p => ReadJournal(p).State == "completed");
    }
    public static void CheckPending(string journalRoot)
    {
        if (!Directory.Exists(journalRoot)) return;
        foreach (var p in Directory.EnumerateFiles(journalRoot, "journal.json", SearchOption.AllDirectories))
        {
            var state = ReadJournal(p).State;
            if (state is "applying" or "recovering") throw new IOException("중단된 작업 기록이 있습니다. 상단 '중단 기록 확인'에서 현재 파일 상태를 검증하세요. 기록은 임의로 삭제하지 마세요: " + p);
        }
    }
    private static ChangeJournal ReadJournal(string path)
    {
        SafePaths.NoLinks(path);
        return JsonSerializer.Deserialize<ChangeJournal>(File.ReadAllText(path), SettingsStore.Json) ?? throw new IOException("복구 기록을 읽지 못했습니다.");
    }
    public static string[] RequiredRoots(string journalPath) => ReadJournal(journalPath).Roots;
    public static void Undo(string journalPath, bool allowPending = false, string[]? allowedRoots = null)
    {
        var j = ReadJournal(journalPath); var dir = Path.GetDirectoryName(journalPath)!;
        if (allowedRoots != null && j.Roots.Any(root => !allowedRoots.Any(allowed => SafePaths.Same(root, allowed)))) throw new IOException("복구 대상 폴더를 모두 쓰기 가능한 탭으로 열어주세요.");
        if (j.State != "completed" && !(allowPending && j.State == "applying")) throw new IOException("되돌릴 수 있는 완료 작업이 아닙니다.");
        foreach (var r in j.Files)
        {
            SafePaths.NoLinks(r.Path);
            if (!j.Roots.Any(root => SafePaths.Under(r.Path, root))) throw new IOException("복구 기록의 경로 범위가 잘못되었습니다.");
            if (CurrentHash(r.Path) != r.AfterHash) throw new IOException("작업 이후 파일이 다시 변경되어 덮어쓰지 않습니다: " + r.Path);
            if (r.Backup != null) SafePaths.NoLinks(Path.Combine(dir, r.Backup));
            if (r.Backup != null && (Path.GetFileName(r.Backup) != r.Backup || SafePaths.Hash(Path.Combine(dir, r.Backup)) != r.BeforeHash)) throw new IOException("백업 파일 검증에 실패했습니다.");
        }
        j.State = "recovering"; SaveJournal(journalPath, j);
        foreach (var r in j.Files.Where(f => f.Backup != null))
        {
            if (CurrentHash(r.Path) == r.BeforeHash) continue;
            AtomicWrite(r.Path, File.ReadAllBytes(Path.Combine(dir, r.Backup!)));
            File.SetLastWriteTimeUtc(r.Path, r.ModifiedUtc);
        }
        foreach (var r in j.Files.Where(f => f.Backup == null)) if (File.Exists(r.Path)) File.Delete(r.Path);
        j.State = "undone"; SaveJournal(journalPath, j);
    }
}
public sealed class ChangeJournal
{
    public string Description { get; set; } = "";
    public string State { get; set; } = "";
    public string[] Roots { get; set; } = Array.Empty<string>();
    public List<FileSnapshot> Files { get; set; } = new();
}
public sealed class FileSnapshot
{
    public string Path { get; set; } = "";
    public string? Backup { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

using System.IO;
using System.Text.RegularExpressions;
namespace BoramRms.Lite;

public sealed record PendingReview(string Path, string JournalHash, bool CanResolve, string Detail);
public sealed partial class FileChanges
{
    public static IReadOnlyList<PendingReview> ReviewPending(string journalRoot, string[] allowedRoots)
    {
        if (!Directory.Exists(journalRoot)) return Array.Empty<PendingReview>();
        SafePaths.NoLinks(journalRoot);
        return Directory.EnumerateFiles(journalRoot, "journal.json", SearchOption.AllDirectories)
            .Where(path => ReadJournal(path).State is "applying" or "recovering")
            .OrderBy(path => path, StringComparer.Ordinal).Select(path => ReviewOne(path, journalRoot, allowedRoots)).ToArray();
    }
    private static PendingReview ReviewOne(string path, string journalRoot, string[] allowedRoots)
    {
        string digest = "";
        try
        {
            if (!SafePaths.Under(path, journalRoot)) throw new IOException("작업 기록 폴더 밖입니다.");
            SafePaths.NoLinks(path); digest = SafePaths.Hash(path);
            var j = ReadJournal(path);
            if (j.State is not ("applying" or "recovering")) throw new IOException("중단 상태가 아닙니다.");
            if (j.Roots.Length == 0 || j.Roots.Any(r => !allowedRoots.Any(a => SafePaths.Same(a, r))))
                throw new IOException("해당 작업의 원래 폴더를 모두 열어야 합니다.");
            if (j.Files.Count == 0 || j.Files.Select(r => SafePaths.Normalize(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != j.Files.Count)
                throw new IOException("파일 목록이 비었거나 경로가 중복됩니다.");
            var directory = System.IO.Path.GetDirectoryName(path)!;
            foreach (var r in j.Files)
            {
                if (!System.IO.Path.IsPathFullyQualified(r.Path) || !j.Roots.Any(root => SafePaths.Under(r.Path, root)))
                    throw new IOException("기록의 파일 범위가 잘못되었습니다.");
                SafePaths.NoLinks(r.Path);
                if (Directory.Exists(r.Path)) throw new IOException("파일 경로에 폴더가 있습니다.");
                if (r.BeforeHash != null && !Regex.IsMatch(r.BeforeHash, "^[A-Fa-f0-9]{64}$")) throw new IOException("변경 전 해시가 잘못되었습니다.");
                if (CurrentHash(r.Path) != r.BeforeHash) throw new IOException("변경 전 상태와 다른 파일이 있어 자동 종료하지 않습니다: " + System.IO.Path.GetFileName(r.Path));
                if (r.BeforeHash != null)
                {
                    if (string.IsNullOrEmpty(r.Backup) || r.Backup != System.IO.Path.GetFileName(r.Backup)) throw new IOException("백업 이름이 잘못되었습니다.");
                    var backup = System.IO.Path.Combine(directory, r.Backup); SafePaths.NoLinks(backup);
                    if (!File.Exists(backup) || SafePaths.Hash(backup) != r.BeforeHash) throw new IOException("변경 전 백업을 검증하지 못했습니다.");
                }
                else if (r.Backup != null) throw new IOException("없었던 파일의 백업 기록이 모순됩니다.");
            }
            if (SafePaths.Hash(path) != digest) throw new IOException("검사 중 작업 기록이 바뀌었습니다.");
            return new(path, digest, true, $"{j.Description} · {j.Files.Count}개 파일이 변경 전 상태와 일치");
        }
        catch (Exception ex) { return new(path, digest, false, ex.Message); }
    }
    public static void ResolveUnchangedPending(PendingReview approved, string journalRoot, string[] allowedRoots)
    {
        if (!approved.CanResolve) throw new IOException("검증되지 않은 기록은 종료하지 않습니다.");
        var fresh = ReviewOne(approved.Path, journalRoot, allowedRoots);
        if (!fresh.CanResolve || fresh.JournalHash != approved.JournalHash) throw new IOException("검사 후 상태가 바뀌었습니다. 다시 확인하세요. " + fresh.Detail);
        var j = ReadJournal(approved.Path);
        // This is not forced recovery: no image, metadata or backup bytes are changed.
        j.State = "verified-restored";
        SaveJournal(approved.Path, j);
    }
}

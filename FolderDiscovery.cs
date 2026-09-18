using System.ComponentModel;
using System.IO;
namespace BoramRms.Lite;

public sealed class FolderChoice : INotifyPropertyChanged
{
    public string Path { get; }
    public string Title { get; }
    public string RelativePath { get; }
    private bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public FolderChoice(FolderContext context, string basePath)
    { Path = context.Root; Title = context.Title; RelativePath = System.IO.Path.GetRelativePath(basePath, Path); }
}
public sealed record FolderScan(IReadOnlyList<FolderChoice> Choices, IReadOnlyList<string> Warnings);
public static class FolderDiscovery
{
    // Only the user-chosen tree is scanned. Stop at an image/event root; do not
    // turn card/repair folders inside that workspace into separate workspaces.
    public static FolderScan Scan(string path, CancellationToken token = default)
    {
        var root = SafePaths.Normalize(path); SafePaths.NoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("선택한 폴더가 없습니다.");
        var results = new List<FolderChoice>(); var warnings = new List<string>();
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int visited = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 500 || results.Count >= 200) { warnings.Add("검색 한도에 도달했습니다. 누락 없이 고르려면 더 가까운 상위 폴더를 선택하세요."); break; }
            var (directory, depth) = pending.Dequeue();
            try
            {
                SafePaths.NoLinks(directory); var context = FolderContext.Resolve(directory);
                bool knownRoot = !SafePaths.Same(context.Root, directory) || System.IO.Path.GetFileName(directory) == "[원본]";
                bool hasImages = false; int filesChecked = 0;
                if (!knownRoot)
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++filesChecked > 2000) { warnings.Add(System.IO.Path.GetFileName(directory) + ": 파일이 많아 검색을 제한했습니다."); break; }
                        if (LocalData.Extensions.Contains(System.IO.Path.GetExtension(file))) { hasImages = true; break; }
                    }
                if (knownRoot || hasImages)
                {
                    if (seen.Add(context.Root)) results.Add(new(context, root));
                    continue;
                }
                if (depth >= 3) { warnings.Add(System.IO.Path.GetFileName(directory) + ": 3단계 아래는 검색하지 않았습니다."); continue; }
                int children = 0;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var name = System.IO.Path.GetFileName(child);
                    if (name.StartsWith('.') || name is "$RECYCLE.BIN" or "System Volume Information") continue;
                    if (++children > 500) { warnings.Add("하위 폴더가 많아 일부만 검색했습니다. 상위 폴더 범위를 좁혀 주세요."); break; }
                    pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add(System.IO.Path.GetFileName(directory) + ": 읽을 수 없어 제외했습니다."); }
        }
        if (results.Count == 0 && warnings.Count == 0) results.Add(new(FolderContext.Resolve(root), root));
        return new(results.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }
}

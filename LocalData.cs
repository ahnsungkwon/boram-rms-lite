using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace BoramRms.Lite;

public sealed class LocalState
{
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
}
public sealed class TextDocument
{
    public Encoding Encoding { get; private set; } = new UTF8Encoding(false, true);
    public byte[] Preamble { get; private set; } = Array.Empty<byte>();
    public string Newline { get; private set; } = "\r\n";
    public List<string> Lines { get; private set; } = new();
    public static TextDocument Decode(byte[]? bytes)
    {
        var d = new TextDocument(); if (bytes == null) return d;
        int skip = 0;
        if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { d.Preamble = bytes[..3]; skip = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { d.Encoding = Encoding.Unicode; d.Preamble = bytes[..2]; skip = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { d.Encoding = Encoding.BigEndianUnicode; d.Preamble = bytes[..2]; skip = 2; }
        string text;
        try { text = d.Encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { d.Encoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); text = d.Encoding.GetString(bytes); }
        d.Newline = text.Contains("\r\n") ? "\r\n" : "\n";
        d.Lines = text.Replace("\r\n", "\n").Split('\n').ToList(); return d;
    }
    public byte[] Encode() => Preamble.Concat(Encoding.GetBytes(string.Join(Newline, Lines))).ToArray();
    public static readonly string[] Sections = { "보완", "주말미등록", "오기입", "오등록", "추가", "입력전 취소", "입력후 취소", "입력전 변경", "입력후 변경" };
    public static string Normal(string s) => Regex.Replace(s.TrimStart('●', 'ㆍ', '•', '-', ' '), @"\s+", "").ToUpperInvariant();
    private static string LineKey(string line) => Normal(Regex.Replace(line.Split('\t')[0], @"[（(]\s*01[\d\s-]{8,13}\s*[）)]\s*$", ""));
    public List<(int Index, string Section, string Details)> Find(ImageItem item)
    {
        var found = new List<(int, string, string)>(); var section = "";
        for (int i = 0; i < Lines.Count; i++)
        {
            var text = Lines[i].Trim();
            if (text.StartsWith('●')) { section = Sections.FirstOrDefault(s => Normal(s) == Normal(text)) ?? ""; continue; }
            if (section.Length == 0 || text.Length == 0) continue;
            var key = LineKey(text); var detail = string.Join("\t", text.Split('\t').Skip(1));
            bool matches = key == Normal(item.Key) || (item.Name.Length > 0 && key == Normal(item.Name));
            if (!matches && section == "입력전 변경")
            {
                var m = Regex.Match(detail, @"(\d{1,2})\s*구좌\s*-->\s*(\d{1,2})\s*구좌");
                matches = m.Success && m.Groups[2].Value == item.Quota && key == Normal(m.Groups[1].Value + item.Name);
            }
            if (matches) found.Add((i, section, detail));
        }
        return found;
    }
    public bool HasNameOnlyReference(ImageItem item) => Find(item).Any(row => LineKey(Lines[row.Index]) == Normal(item.Name));
    public void Change(ImageItem item, string newKey, string? status, string details)
    {
        var rows = Find(item);
        if (status == null)
        {
            foreach (var row in rows)
            {
                var line = Lines[row.Index]; var tab = line.IndexOf('\t');
                var phone = Regex.Match(tab >= 0 ? line[..tab] : line, @"[（(]\s*01[\d\s-]{8,13}\s*[）)]\s*$").Value;
                var parsed = ImageItem.Parse(newKey + ".tmp");
                var nameOnly = LineKey(line) == Normal(item.Name);
                var key = nameOnly ? parsed.Name : newKey;
                var tail = tab >= 0 ? line[tab..] : "";
                if (row.Section == "입력전 변경")
                {
                    var history = Regex.Match(tail, @"(\d{1,2})\s*구좌\s*-->\s*(\d{1,2})\s*구좌");
                    if (history.Success)
                    {
                        key = nameOnly ? parsed.Name : history.Groups[1].Value + parsed.Name;
                        tail = tail[..history.Index] + history.Groups[1].Value + "구좌-->" + parsed.Quota + "구좌" + tail[(history.Index + history.Length)..];
                    }
                }
                Lines[row.Index] = key + phone + tail;
            }
            return;
        }
        foreach (var row in rows.OrderByDescending(r => r.Index)) Lines.RemoveAt(row.Index);
        if (status.Length == 0) return;
        var section = Sections.FirstOrDefault(s => Normal(s) == Normal(status));
        if (section == null) throw new ArgumentException("지원하지 않는 TXT 상태입니다: " + status);
        var index = Lines.FindIndex(l => l.Trim().StartsWith('●') && Normal(l) == Normal(section));
        if (index < 0) { if (Lines.Count > 0 && Lines[^1] != "") Lines.Add(""); Lines.Add("●" + section); index = Lines.Count - 1; }
        Lines.Insert(index + 1, newKey + (string.IsNullOrWhiteSpace(details) ? "" : "\t" + details.Replace('\r', ' ').Replace('\n', ' ')));
    }
}
public static class LocalData
{
    public const string OutputMarker = ".rmslite-output.json";
    public static readonly string[] QuotaFolders = { "1구좌", "2구좌", "3구좌", "4구좌", "5구좌이상" };
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif", ".webp" };
    public static string JournalRoot(FolderContext ctx) => Path.Combine(ctx.Metadata, "lite-journal");
    public static string QuotaFolder(string quota) => int.TryParse(quota, out var n) && n >= 1 ? n >= 5 ? "5구좌이상" : n + "구좌" : throw new ArgumentException("구좌수를 먼저 확인해 주세요.");
    public static IEnumerable<string> Enumerate(string root, CancellationToken cancellation = default)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested(); var dir = stack.Pop();
            SafePaths.NoLinks(dir);
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                cancellation.ThrowIfCancellationRequested();
                if (Extensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith('.')) { SafePaths.NoLinks(f); yield return f; }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name == "[참고자료]" || name.Equals("jpg", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(sub, OutputMarker))) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                stack.Push(sub);
            }
        }
    }
    private static JsonObject JsonObjectFrom(byte[]? data) => data == null ? new JsonObject() : JsonNode.Parse(data)?.AsObject() ?? throw new IOException("상태 JSON 형식이 잘못되었습니다.");
    public static Dictionary<string,LocalState> ReadStates(FolderContext ctx)
    {
        var p = Path.Combine(ctx.Metadata, "lite-states.json");
        SafePaths.NoLinks(p);
        var data = File.Exists(p) ? JsonSerializer.Deserialize<Dictionary<string,LocalState>>(File.ReadAllText(p), SettingsStore.Json) : null;
        return new Dictionary<string, LocalState>(data ?? new(), StringComparer.OrdinalIgnoreCase);
    }
    public static HashSet<string> ReadCards(FolderContext ctx)
    {
        var path = Path.Combine(ctx.Metadata, "account-types.json");
        SafePaths.NoLinks(path);
        var data = JsonObjectFrom(File.Exists(path) ? File.ReadAllBytes(path) : null);
        var node = data.FirstOrDefault(kv => kv.Key.Equals("CardPaths", StringComparison.OrdinalIgnoreCase)).Value;
        return new HashSet<string>(node?.AsArray().Select(n => n?.GetValue<string>() ?? "") ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }
    public static List<ImageItem> Load(FolderContext ctx, CancellationToken token = default)
    {
        var cards = ReadCards(ctx); var states = ReadStates(ctx);
        var hasText = ctx.TextPath != null && File.Exists(ctx.TextPath);
        if (hasText) SafePaths.NoLinks(ctx.TextPath!);
        var doc = TextDocument.Decode(hasText ? File.ReadAllBytes(ctx.TextPath!) : null);
        var items = new List<ImageItem>();
        foreach (var p in Enumerate(ctx.Root, token))
        {
            var file = new FileInfo(p); var parts = ImageItem.Parse(file.Name); var rel = Path.GetRelativePath(ctx.Root, p);
            var segments = rel.Split(Path.DirectorySeparatorChar);
            var state = states.GetValueOrDefault(rel);
            var item = new ImageItem { FullPath = p, RelativePath = rel, Quota = parts.Quota, Name = parts.Name, Length = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks,
                Card = cards.Contains(rel) || segments.Contains("카드"), Status = segments.FirstOrDefault(s => s is "보완" or "주말미등록" or "취소" or "추가") ?? "" };
            if (!hasText && state != null) { item.Status = state.Status; item.Details = state.Details; }
            var rows = doc.Find(item);
            if (rows.Count > 0) { item.Status = rows[0].Section; item.Details = string.Join(" / ", rows.Select(r => r.Details).Where(s => s.Length > 0)); }
            items.Add(item);
        }
        return items.OrderBy(i => int.TryParse(i.Quota, out var n) ? n : int.MaxValue).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).Select((i, index) => { i.Index = index + 1; return i; }).ToList();
    }
    public static void EnsureUnchanged(ImageItem item)
    {
        SafePaths.NoLinks(item.FullPath); var f = new FileInfo(item.FullPath);
        if (!f.Exists || f.Length != item.Length || f.LastWriteTimeUtc.Ticks != item.ModifiedTicks) throw new IOException("선택 파일이 외부에서 변경되었습니다. 새로고침 후 다시 선택하세요.");
    }
    public static string Rename(FolderContext ctx, ImageItem item, string input)
    {
        var name = SafePaths.ValidName(input);
        if (!SafePaths.Under(item.FullPath, ctx.Root)) throw new IOException("선택 폴더 밖의 이미지입니다.");
        EnsureUnchanged(item);
        // Unchanged input must not rewrite metadata or create a recovery journal.
        if (Path.GetFileNameWithoutExtension(item.FileName).Equals(name, StringComparison.Ordinal)) return item.FullPath;
        return Change(ctx, item, name, null, item.Details, item.Card);
    }
    public static string Change(FolderContext ctx, ImageItem item, string? newStem, string? status, string details, bool card)
    {
        EnsureUnchanged(item);
        if (!SafePaths.Under(item.FullPath, ctx.Root)) throw new IOException("선택한 폴더 밖의 이미지입니다.");
        if (newStem != null) newStem = SafePaths.ValidName(newStem);
        var change = new FileChanges(JournalRoot(ctx), ctx.EventRoot ?? ctx.Root);
        change.Expect(item.FullPath);
        var destinationDir = Path.GetDirectoryName(item.FullPath)!;
        if (status == null && card != item.Card)
        {
            var parts = Path.GetRelativePath(ctx.Root, destinationDir).Split(Path.DirectorySeparatorChar).Where(s => s != "." && s != "카드").ToList();
            if (card && !parts.Contains("보완")) parts.Add("카드");
            destinationDir = parts.Aggregate(ctx.Root, Path.Combine);
        }
        if (status != null)
        {
            var folder = status.Contains("취소") ? "취소" : status is "보완" or "주말미등록" ? status : "";
            destinationDir = Path.Combine(ctx.Root, folder);
            if (card && folder != "보완") destinationDir = Path.Combine(destinationDir, "카드");
        }
        var desired = Path.Combine(destinationDir, (newStem ?? Path.GetFileNameWithoutExtension(item.FileName)) + Path.GetExtension(item.FileName));
        var target = SafePaths.Same(desired, item.FullPath) ? item.FullPath : SafePaths.Unique(desired);
        if (ctx.EventRoot != null)
        {
            var current = Load(ctx);
            var destination = ImageItem.Parse(Path.GetFileName(desired));
            if (current.Count(i => TextDocument.Normal(i.Key) == TextDocument.Normal(item.Key)) > 1)
                throw new IOException("같은 구좌수·이름이 여러 폴더에 있어 TXT/가공본 연결을 확정할 수 없습니다. 중복 자료를 먼저 확인하세요.");
            if (current.Any(i => !SafePaths.Same(i.FullPath, item.FullPath) && TextDocument.Normal(i.Key) == TextDocument.Normal(destination.Quota + destination.Name)))
                throw new IOException("변경할 구좌수·이름의 신청서가 이미 있습니다. 확장자나 폴더가 달라도 먼저 중복 여부를 확인하세요.");
        }
        change.Move(item.FullPath, target);
        var newParts = ImageItem.Parse(Path.GetFileName(target));
        AddProcessedChange(change, ctx, item, target, newParts.Quota, status);
        AddDerivativeRename(change, ctx, item, target);
        var history = item.Status == "입력전 변경" ? Regex.Match(item.Details, @"(\d{1,2})\s*구좌\s*-->\s*\d{1,2}\s*구좌") : Match.Empty;
        var initialQuota = history.Success ? history.Groups[1].Value : item.Quota;
        if (status == "입력전 변경" || (status == null && history.Success))
            details = Regex.Replace(details, @"\d{1,2}\s*구좌\s*-->\s*\d{1,2}\s*구좌", initialQuota + "구좌-->" + newParts.Quota + "구좌");
        UpdateMetadata(change, ctx, item.RelativePath, Path.GetRelativePath(ctx.Root, target), new LocalState { Status = status ?? item.Status, Details = details }, card);
        if (ctx.TextPath != null)
        {
            var original = change.Read(ctx.TextPath);
            // Renaming with no managed record does not create an unrelated TXT file.
            if (status != null || original != null)
            {
                var doc = TextDocument.Decode(original);
                if (doc.HasNameOnlyReference(item) && Load(ctx).Count(i => TextDocument.Normal(i.Name) == TextDocument.Normal(item.Name)) > 1)
                    throw new IOException("이름만 기록된 TXT에 동명이인이 있어 연결을 확정할 수 없습니다. 구좌수를 포함한 식별 기록을 먼저 확인하세요.");
                doc.Change(item, status == "입력전 변경" ? initialQuota + newParts.Name : newParts.Quota + newParts.Name, status, details);
                var bytes = doc.Encode();
                if (original == null || !original.SequenceEqual(bytes)) change.Write(ctx.TextPath, bytes);
            }
        }
        change.Apply(status == null ? "리네임" : "상태 변경: " + status);
        return target;
    }
    public static void UpdateMetadata(FileChanges changes, FolderContext ctx, string oldRel, string newRel, LocalState state, bool card, IEnumerable<string>? additionalOld = null)
    {
        var path = Path.Combine(ctx.Metadata, "account-types.json");
        var data = JsonObjectFrom(changes.Read(path));
        var key = data.Select(p => p.Key).FirstOrDefault(k => k.Equals("CardPaths", StringComparison.OrdinalIgnoreCase)) ?? "CardPaths";
        var cards = new HashSet<string>(data[key]?.AsArray().Select(n => n!.GetValue<string>()) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        cards.Remove(oldRel); cards.Remove(newRel);
        foreach (var rel in additionalOld ?? Array.Empty<string>()) cards.Remove(rel);
        if (card && newRel.Length > 0) cards.Add(newRel.ToUpperInvariant());
        data[key] = new JsonArray(cards.OrderBy(s => s).Select(s => JsonValue.Create(s)).ToArray());
        if (!data.ContainsKey("Version")) data["Version"] = 1;
        changes.Write(path, Encoding.UTF8.GetBytes(data.ToJsonString(SettingsStore.Json)));
        var statePath = Path.Combine(ctx.Metadata, "lite-states.json");
        var bytes = changes.Read(statePath);
        var states = new Dictionary<string,LocalState>(bytes == null ? new() : JsonSerializer.Deserialize<Dictionary<string,LocalState>>(bytes, SettingsStore.Json) ?? new(), StringComparer.OrdinalIgnoreCase);
        states.Remove(oldRel);
        foreach (var rel in additionalOld ?? Array.Empty<string>()) states.Remove(rel);
        if (newRel.Length > 0) states[newRel] = state;
        changes.WriteJson(statePath, states);
    }
    private static void AddProcessedChange(FileChanges changes, FolderContext ctx, ImageItem old, string target, string quota, string? status)
    {
        if (ctx.ProcessedRoot == null || quota.Length == 0) return;
        var candidates = new List<string>();
        foreach (var name in QuotaFolders.Append("취소"))
        {
            var d = Path.Combine(ctx.ProcessedRoot, name);
            if (Directory.Exists(d)) candidates.AddRange(Enumerate(d).Where(p => Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileNameWithoutExtension(old.FileName), StringComparison.OrdinalIgnoreCase)));
        }
        if (candidates.Count > 1) throw new IOException("가공본이 여러 개라 자동 이동을 중단했습니다: " + old.FileName);
        if (candidates.Count == 0) return;
        var src = candidates[0];
        var effectiveStatus = status ?? old.Status;
        var dir = Path.Combine(ctx.ProcessedRoot, QuotaFolder(quota));
        if (effectiveStatus.Contains("취소")) dir = Path.Combine(dir, "취소");
        // Supplemental and weekend status does not itself move a processed file.
        if (status is "보완" or "주말미등록") dir = Path.GetDirectoryName(src)!;
        var dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(target) + Path.GetExtension(src));
        if (SafePaths.Same(src, dst)) return;
        if (File.Exists(dst)) throw new IOException("가공본 이름 충돌: " + dst);
        changes.Move(src, dst);
    }
    public static IEnumerable<(string Path, ResizeManifest Manifest)> OutputManifests(FolderContext ctx)
    {
        foreach (var dir in Directory.EnumerateDirectories(ctx.Root))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            var p = Path.Combine(dir, OutputMarker);
            if (!File.Exists(p)) continue;
            SafePaths.NoLinks(p);
            var m = JsonSerializer.Deserialize<ResizeManifest>(File.ReadAllText(p), SettingsStore.Json);
            if (m?.Kind == "BoramRMSLiteResize" && SafePaths.Same(m.SourceRoot, ctx.Root)) yield return (p, m);
        }
    }
    private static void AddDerivativeRename(FileChanges changes, FolderContext ctx, ImageItem old, string target)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (manifestPath, manifest) in OutputManifests(ctx))
        {
            var changed = false; changes.Expect(manifestPath);
            foreach (var entry in manifest.Items.Where(e => e.SourceRelative.Equals(old.RelativePath, StringComparison.OrdinalIgnoreCase) && e.Success))
            {
                var root = Path.GetDirectoryName(manifestPath)!; var src = Path.GetFullPath(Path.Combine(root, entry.OutputRelative));
                if (!SafePaths.Under(src, root) || !File.Exists(src)) continue;
                SafePaths.NoLinks(src);
                if (entry.OutputHash.Length > 0 && SafePaths.Hash(src) != entry.OutputHash) throw new IOException("리사이즈 사본이 외부에서 바뀌어 연결 변경을 중단했습니다: " + src);
                tracked.Add(SafePaths.Normalize(src));
                var dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(target) + Path.GetExtension(src));
                if (!SafePaths.Same(src, dst) && File.Exists(dst)) throw new IOException("리사이즈 사본 이름 충돌: " + dst);
                changes.Move(src, dst); entry.SourceRelative = Path.GetRelativePath(ctx.Root, target); entry.OutputRelative = Path.GetRelativePath(root, dst); changed = true;
            }
            if (changed) changes.WriteJson(manifestPath, manifest);
        }
        var legacy = Path.Combine(ctx.Root, "jpg", old.FileName);
        if (File.Exists(legacy) && !tracked.Contains(SafePaths.Normalize(legacy)))
        {
            var dst = Path.Combine(ctx.Root, "jpg", Path.GetFileName(target));
            if (!SafePaths.Same(legacy, dst)) changes.Move(legacy, dst);
        }
    }
}

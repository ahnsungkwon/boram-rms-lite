using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace BoramRms.Lite;
public sealed class SupplementPlan
{
    public required FolderContext Source { get; init; }
    public required FolderContext Target { get; init; }
    public required ImageItem Item { get; init; }
    public required List<ImageItem> Existing { get; init; }
    public required List<string> Processed { get; init; }
    public required List<string> Derivatives { get; init; }
    public required Dictionary<string,string> Expected { get; init; }
    public required string OriginalDestination { get; init; }
    public required string ProcessedDestination { get; init; }
    public string Summary => $"보완 원본: {Item.FullPath}\n\n대상 원본: {OriginalDestination}\n저해상도: {ProcessedDestination}\n\n기존 신청서 {Existing.Count}개와 가공본 {Processed.Count}개는 대상의 .boramrms 폴더에 백업한 뒤 교체합니다.\n보낸 자료의 리사이즈 사본 {Derivatives.Count}개는 출발 폴더의 .boramrms에 보관합니다.\n\n원본은 이동하고, 300KB 이하 JPEG 가공본을 새로 만듭니다. ERP 등록이나 보고서 처리는 하지 않습니다.";
}
public static class SupplementService
{
    private static string Identity(string name)
    {
        var p = ImageItem.Parse(name);
        return TextDocument.Normal(p.Quota + Regex.Replace(p.Name, @"\s*\(\d+\)$", ""));
    }
    public static SupplementPlan Plan(FolderContext source, FolderContext target, ImageItem item)
    {
        if (source.EventRoot != null || target.EventRoot == null || target.ProcessedRoot == null || SafePaths.Under(source.Root, target.Root) || SafePaths.Under(target.Root, source.Root))
            throw new IOException("독립 보완자료 폴더에서 별도의 강연회 탭으로만 대체 이동할 수 있습니다.");
        LocalData.EnsureUnchanged(item); _ = SafePaths.ValidName(item.Key);
        var existing = LocalData.Load(target).Where(i => Identity(i.FileName) == Identity(item.FileName)).ToList();
        var processed = new List<string>();
        foreach (var name in LocalData.QuotaFolders.Append("취소"))
        {
            var path = Path.Combine(target.ProcessedRoot, name);
            if (Directory.Exists(path)) processed.AddRange(LocalData.Enumerate(path).Where(p => Identity(Path.GetFileName(p)) == Identity(item.FileName)));
        }
        var derivatives = new List<string>(); var legacy = Path.Combine(source.Root, "jpg", item.FileName);
        if (File.Exists(legacy)) derivatives.Add(legacy);
        foreach (var (p, manifest) in LocalData.OutputManifests(source))
        {
            foreach (var entry in manifest.Items.Where(e => e.Success && e.SourceRelative.Equals(item.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                var outputRoot = Path.GetDirectoryName(p)!;
                var path = Path.GetFullPath(Path.Combine(outputRoot, entry.OutputRelative));
                if (SafePaths.Under(path, outputRoot) && File.Exists(path)) derivatives.Add(path);
            }
        }
        var originalDestination = Path.Combine(target.Root, "보완", item.FileName);
        var processedDestination = Path.Combine(target.ProcessedRoot, LocalData.QuotaFolder(item.Quota), Path.GetFileNameWithoutExtension(item.FileName) + ".jpg");
        if (File.Exists(originalDestination) && !existing.Any(i => SafePaths.Same(i.FullPath, originalDestination))) throw new IOException("대상 원본의 식별 정보가 일치하지 않습니다.");
        if (File.Exists(processedDestination) && !processed.Any(p => SafePaths.Same(p, processedDestination))) throw new IOException("대상 가공본의 식별 정보가 일치하지 않습니다.");
        var paths = existing.Select(i => i.FullPath).Concat(processed).Concat(derivatives).Append(item.FullPath).Distinct(StringComparer.OrdinalIgnoreCase);
        var expected = paths.ToDictionary(p => p, SafePaths.Hash, StringComparer.OrdinalIgnoreCase);
        return new SupplementPlan { Source = source, Target = target, Item = item, Existing = existing, Processed = processed, Derivatives = derivatives.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Expected = expected, OriginalDestination = originalDestination, ProcessedDestination = processedDestination };
    }
    public static void Apply(SupplementPlan plan)
    {
        foreach (var pair in plan.Expected) if (!File.Exists(pair.Key) || SafePaths.Hash(pair.Key) != pair.Value) throw new IOException("확인 이후 파일이 바뀌었습니다. 다시 확인하세요: " + pair.Key);
        FileChanges.CheckPending(LocalData.JournalRoot(plan.Source)); FileChanges.CheckPending(LocalData.JournalRoot(plan.Target));
        var operation = new FileChanges(LocalData.JournalRoot(plan.Target), plan.Source.Root, plan.Target.EventRoot!);
        foreach (var path in plan.Expected.Keys) operation.Expect(path);
        var resized = ImageProcessing.Compress(ImageProcessing.Load(plan.Item.FullPath), new ResizeOptions(), CancellationToken.None);
        var batch = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6];
        foreach (var old in plan.Existing.Select(i => i.FullPath).Concat(plan.Processed))
            operation.Move(old, Path.Combine(plan.Target.Metadata, "lite-replaced", batch, Path.GetRelativePath(plan.Target.EventRoot!, old)));
        foreach (var old in plan.Derivatives)
            operation.Move(old, Path.Combine(plan.Source.Metadata, "lite-supplement-sent", batch, Path.GetRelativePath(plan.Source.Root, old)));
        operation.Move(plan.Item.FullPath, plan.OriginalDestination);
        operation.Write(plan.ProcessedDestination, resized.Bytes);
        var card = plan.Item.Card || plan.Existing.Any(i => i.Card);
        var details = "보완자료 교체" + (plan.Item.Details.Length > 0 ? " · " + plan.Item.Details : "");
        LocalData.UpdateMetadata(operation, plan.Target, "", Path.GetRelativePath(plan.Target.Root, plan.OriginalDestination), new LocalState { Status = "보완", Details = details }, card, plan.Existing.Select(i => i.RelativePath));
        LocalData.UpdateMetadata(operation, plan.Source, plan.Item.RelativePath, "", new LocalState(), false);
        var doc = TextDocument.Decode(operation.Read(plan.Target.TextPath!));
        foreach (var old in plan.Existing) doc.Change(old, old.Key, "", "");
        doc.Change(plan.Item, plan.Item.Key, "보완", details); operation.Write(plan.Target.TextPath!, doc.Encode());
        operation.Apply("보완자료 대체 이동 · 기존 자료 백업");
    }
}

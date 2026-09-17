using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
namespace BoramRms.Lite;

public sealed class InstallFile
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}
public sealed class InstallManifest
{
    public string Product { get; set; } = UpdateIdentity.Product;
    public string Repository { get; set; } = UpdateIdentity.Repository;
    public string Version { get; set; } = "";
    public Dictionary<string, InstallFile> Files { get; set; } = new();
}
public sealed class UpdatePlan
{
    public string AppRoot { get; set; } = "";
    public string WorkRoot { get; set; } = "";
    public string Archive { get; set; } = "";
    public UpdateManifest Manifest { get; set; } = new();
    public string FromVersion { get; set; } = "";
    public int ParentPid { get; set; }
    public long ParentStartedTicks { get; set; }
}
public static class UpdatePackage
{
    public static string UpdatesRoot => Path.Combine(SettingsStore.DirectoryPath, "Updates");
    public static string SafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/') || relative.Length > 230) throw new InvalidDataException("잘못된 압축 경로입니다.");
        var parts = relative.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase))) throw new InvalidDataException("안전하지 않은 파일 이름입니다.");
        return Path.Combine(parts);
    }
    private static IEnumerable<string> TreeFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop(); SafePaths.NoLinks(dir);
            foreach (var file in Directory.EnumerateFiles(dir)) { SafePaths.NoLinks(file); yield return file; }
            foreach (var sub in Directory.EnumerateDirectories(dir)) { SafePaths.NoLinks(sub); pending.Push(sub); }
        }
    }
    public static InstallManifest ValidateInstallation(string directory, string? expectedVersion = null, bool requireRuntime = true)
    {
        SafePaths.NoLinks(directory);
        var marker = Path.Combine(directory, UpdateIdentity.Marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 2000000) throw new IOException("업데이트 가능한 Lite 전용 배포 폴더가 아닙니다. 새 배포 ZIP을 별도 폴더에 풀어 실행하세요.");
        SafePaths.NoLinks(marker);
        var data = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(marker), SettingsStore.Json) ?? throw new InvalidDataException("설치 정보가 비어 있습니다.");
        if (data.Product != UpdateIdentity.Product || data.Repository != UpdateIdentity.Repository || data.Files.Count is < 1 or > 3000) throw new InvalidDataException("다른 프로그램의 설치 폴더입니다.");
        _ = UpdateIdentity.ParseVersion(data.Version);
        if (expectedVersion != null && UpdateIdentity.ParseVersion(data.Version) != UpdateIdentity.ParseVersion(expectedVersion)) throw new InvalidDataException("설치 버전 정보 불일치");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in data.Files)
        {
            var relative = SafeRelative(entry.Key);
            if (!names.Add(relative) || relative.Equals(UpdateIdentity.Marker, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("설치 목록 중복");
            var path = Path.GetFullPath(Path.Combine(directory, relative)); SafePaths.NoLinks(path);
            if (!SafePaths.Under(path, directory) || !File.Exists(path) || new FileInfo(path).Length != entry.Value.Size || !Regex.IsMatch(entry.Value.Sha256, "^[a-fA-F0-9]{64}$") || !SafePaths.Hash(path).Equals(entry.Value.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("설치 파일 검증 실패: " + relative);
        }
        var actual = TreeFiles(directory).Where(p => !SafePaths.Same(p, marker)).Select(p => Path.GetRelativePath(directory, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.SetEquals(actual)) throw new IOException("앱 폴더에 배포본 외 파일이 있거나 파일이 누락되었습니다. 신청서·개인 문서를 다른 폴더에 보관한 뒤 업데이트하세요.");
        if (requireRuntime)
        {
            foreach (var required in new[] { UpdateIdentity.Executable, "BoramRms.Lite.dll", "BoramRms.Lite.runtimeconfig.json", "hostfxr.dll", "coreclr.dll", "PresentationFramework.dll" })
                if (!names.Contains(required)) throw new InvalidDataException("독립 실행 패키지 구성 누락: " + required);
        }
        return data;
    }
    public static void ExtractVerified(string archive, string stage, UpdateManifest release, CancellationToken token, bool requireRuntime = true)
    {
        release.Validate(); SafePaths.NoLinks(archive); SafePaths.NoLinks(stage);
        if (new FileInfo(archive).Length != release.Size || !SafePaths.Hash(archive).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("압축 파일 검증 실패");
        if (Directory.Exists(stage)) throw new IOException("이미 존재하는 폴더에는 압축을 풀지 않습니다.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 3200 || zip.Entries.Sum(e => e.Length) > 1200000000) throw new InvalidDataException("압축 해제 한도 초과");
        var entries = new List<(ZipArchiveEntry Entry, string Relative)>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(release.Root + "/", StringComparison.Ordinal)) throw new InvalidDataException("다른 최상위 폴더가 포함된 ZIP입니다.");
            var name = entry.FullName[(release.Root.Length + 1)..];
            if (name.Length == 0) continue;
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("링크 파일은 허용하지 않습니다.");
            var isDirectory = name.EndsWith('/'); var rel = SafeRelative(isDirectory ? name[..^1] : name);
            if (isDirectory) continue;
            if (!seen.Add(rel)) throw new InvalidDataException("압축 내 파일 이름이 중복됩니다.");
            entries.Add((entry, rel));
        }
        Directory.CreateDirectory(stage);
        foreach (var (entry, rel) in entries)
        {
            token.ThrowIfCancellationRequested(); var path = Path.Combine(stage, rel); SafePaths.NoLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[131072]; int read; long length = 0;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); length += read; if (length > entry.Length) throw new InvalidDataException("파일 해제 길이 초과"); output.Write(buffer, 0, read); }
            if (length != entry.Length) throw new InvalidDataException("압축 파일이 완전하지 않습니다."); output.Flush(true);
        }
        ValidateInstallation(stage, release.Version, requireRuntime);
    }
    public static string Swap(string target, string staged, string fromVersion, string toVersion, bool requireRuntime = true)
    {
        if (SafePaths.Under(staged, target) || SafePaths.Under(target, staged)) throw new IOException("설치 경로와 준비 경로가 겹칩니다.");
        ValidateInstallation(target, fromVersion, requireRuntime); ValidateInstallation(staged, toVersion, requireRuntime);
        if (UpdateIdentity.ParseVersion(toVersion) <= UpdateIdentity.ParseVersion(fromVersion)) throw new IOException("동일/이전 버전으로 교체하지 않습니다.");
        if (!SafePaths.Same(Path.GetDirectoryName(target)!, Path.GetDirectoryName(staged)!)) throw new IOException("준비 파일은 같은 설치 볼륨에 있어야 합니다.");
        var backup = target + ".backup-" + fromVersion + "-" + Guid.NewGuid().ToString("N")[..10];
        Directory.Move(target, backup);
        try { Directory.Move(staged, target); }
        catch { Directory.Move(backup, target); throw; }
        return backup;
    }
    public static async Task<UpdatePlan> PrepareAsync(GitHubUpdateClient client, UpdateRelease release, string appRoot, IProgress<double>? progress, CancellationToken token)
    {
        var installed = await Task.Run(() => ValidateInstallation(appRoot, UpdateIdentity.CurrentVersion), token);
        if (UpdateIdentity.ParseVersion(release.Manifest.Version) <= UpdateIdentity.ParseVersion(installed.Version)) throw new IOException("이미 최신 버전입니다.");
        var work = Path.Combine(UpdatesRoot, "job-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var archive = Path.Combine(work, release.Manifest.Package);
        await client.DownloadAsync(release, archive, progress, token);
        await Task.Run(() => ExtractVerified(archive, Path.Combine(work, "runner"), release.Manifest, token), token);
        using var process = Process.GetCurrentProcess();
        return new UpdatePlan { AppRoot = SafePaths.Normalize(appRoot), WorkRoot = work, Archive = archive, Manifest = release.Manifest, FromVersion = installed.Version, ParentPid = process.Id, ParentStartedTicks = process.StartTime.ToUniversalTime().Ticks };
    }
    public static void StartHelper(UpdatePlan plan)
    {
        if (!SafePaths.Under(plan.WorkRoot, UpdatesRoot) || SafePaths.Same(plan.WorkRoot, UpdatesRoot)) throw new IOException("업데이트 작업 경로 오류");
        var path = Path.Combine(plan.WorkRoot, "plan.json");
        FileChanges.AtomicWrite(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, SettingsStore.Json)));
        var runner = Path.Combine(plan.WorkRoot, "runner"); ValidateInstallation(runner, plan.Manifest.Version);
        var start = new ProcessStartInfo(Path.Combine(runner, UpdateIdentity.Executable)) { UseShellExecute = false, WorkingDirectory = runner };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("업데이트 도우미를 시작하지 못했습니다.");
    }
    public static async Task<int> ApplyHelperAsync(string planPath)
    {
        string? backup = null; UpdatePlan? plan = null;
        try
        {
            SafePaths.NoLinks(planPath);
            if (!SafePaths.Under(planPath, UpdatesRoot) || new FileInfo(planPath).Length > 100000) throw new IOException("허용되지 않은 설치 지시 경로입니다.");
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath), SettingsStore.Json) ?? throw new IOException("업데이트 지시 누락");
            plan.Manifest.Validate();
            if (!SafePaths.Same(planPath, Path.Combine(plan.WorkRoot, "plan.json")) || !SafePaths.Same(AppContext.BaseDirectory, Path.Combine(plan.WorkRoot, "runner")) || !SafePaths.Same(plan.Archive, Path.Combine(plan.WorkRoot, plan.Manifest.Package))) throw new IOException("업데이트 실행 경로 불일치");
            if (SafePaths.Under(plan.AppRoot, SettingsStore.DirectoryPath) || SafePaths.Under(SettingsStore.DirectoryPath, plan.AppRoot)) throw new IOException("설정 폴더를 앱 설치 경로로 사용할 수 없습니다.");
            try
            {
                using var parent = Process.GetProcessById(plan.ParentPid);
                if (!parent.HasExited)
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedTicks || parent.MainModule?.FileName is not string parentExe || !SafePaths.Same(parentExe, Path.Combine(plan.AppRoot, UpdateIdentity.Executable))) throw new IOException("종료를 기다릴 Lite 프로세스가 일치하지 않습니다.");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)); await parent.WaitForExitAsync(timeout.Token);
                }
            }
            catch (ArgumentException) { /* Parent already exited normally. */ }
            // Never kill other RMS/Lite processes. Exclusive file access blocks replacement if another copy is running.
            foreach (var process in Process.GetProcessesByName("BoramRms.Lite"))
            {
                using (process) { if (process.Id == Environment.ProcessId || process.HasExited) continue; try { if (process.MainModule?.FileName is string p && SafePaths.Same(p, Path.Combine(plan.AppRoot, UpdateIdentity.Executable))) throw new IOException("같은 설치 폴더의 다른 Lite 창을 닫고 다시 업데이트하세요."); } catch (System.ComponentModel.Win32Exception) { throw new IOException("실행 중인 Lite를 확인하지 못해 교체를 중단했습니다."); } }
            }
            var stage = Path.Combine(Path.GetDirectoryName(plan.AppRoot)!, ".rmslite-stage-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() => ExtractVerified(plan.Archive, stage, plan.Manifest, CancellationToken.None));
            backup = await Task.Run(() => Swap(plan.AppRoot, stage, plan.FromVersion, plan.Manifest.Version));
            var start = new ProcessStartInfo(Path.Combine(plan.AppRoot, UpdateIdentity.Executable)) { UseShellExecute = false, WorkingDirectory = plan.AppRoot };
            start.ArgumentList.Add("--updated");
            var health = Path.Combine(plan.WorkRoot, "startup-ok"); start.ArgumentList.Add("--health-file"); start.ArgumentList.Add(health);
            using var restarted = Process.Start(start) ?? throw new IOException("새 버전을 시작하지 못했습니다.");
            var limit = DateTime.UtcNow.AddSeconds(25);
            while (!File.Exists(health) && !restarted.HasExited && DateTime.UtcNow < limit) await Task.Delay(200);
            if (!File.Exists(health))
            {
                if (!restarted.HasExited) { restarted.CloseMainWindow(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await restarted.WaitForExitAsync(timeout.Token); }
                throw new IOException("새 버전의 시작 확인에 실패했습니다.");
            }
            FileChanges.AtomicWrite(Path.Combine(plan.WorkRoot, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new { status = "updated", version = plan.Manifest.Version, backup }, SettingsStore.Json));
            return 0;
        }
        catch (Exception ex)
        {
            string recovery = "기존 앱과 신청서 자료는 자동 삭제하지 않았습니다.";
            if (backup != null && plan != null)
            {
                try
                {
                    var failed = plan.AppRoot + ".failed-" + Guid.NewGuid().ToString("N")[..8];
                    if (Directory.Exists(plan.AppRoot)) Directory.Move(plan.AppRoot, failed);
                    Directory.Move(backup, plan.AppRoot);
                    using var old = Process.Start(new ProcessStartInfo(Path.Combine(plan.AppRoot, UpdateIdentity.Executable)) { UseShellExecute = false, WorkingDirectory = plan.AppRoot });
                    recovery = "이전 버전으로 복구했습니다. 실패한 배포본도 별도로 보존했습니다.";
                }
                catch { recovery = "자동 복구를 완료하지 못했습니다. 이전 버전 백업: " + backup; }
            }
            try { if (plan != null) FileChanges.AtomicWrite(Path.Combine(plan.WorkRoot, "error.txt"), Encoding.UTF8.GetBytes(ex.Message + "\n" + recovery)); } catch { }
            MessageBox.Show(ex.Message + "\n\n" + recovery, "보람 RMS Lite 업데이트", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
}

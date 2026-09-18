using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BoramRms.Setup
{
    public sealed class PayloadInfo
    {
        public string Product { get; set; }
        public string Repository { get; set; }
        public string Version { get; set; }
        public string Package { get; set; }
        public string Root { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class FileEntry { public long Size { get; set; } public string Sha256 { get; set; } }
    public sealed class AppManifest
    {
        public string Product { get; set; }
        public string Repository { get; set; }
        public string Version { get; set; }
        public Dictionary<string, FileEntry> Files { get; set; }
    }
    public sealed class InstallResult { public string Directory { get; set; } public bool AlreadyInstalled { get; set; } public int FileCount { get; set; } }
    public static class InstallCore
    {
        public const string Product = "BoramRms.Lite", Repository = "ahnsungkwon/boram-rms-lite", Executable = "BoramRms.Lite.exe", Marker = ".lite-install.json";
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2000000 };
        public static string BaseDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BoramRMSLite");
        public static string AppDirectory => Path.Combine(BaseDirectory, "App");
        public static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException("설치 파일 구성 누락: " + name);
        public static PayloadInfo Info()
        {
            PayloadInfo p; using (var r = new StreamReader(Resource("payload.json"))) p = Json.Deserialize<PayloadInfo>(r.ReadToEnd());
            if (p.Product != Product || p.Repository != Repository || p.Root != "BoramRMS_Lite" || !Regex.IsMatch(p.Version ?? "", @"^\d+\.\d+\.\d+$") || !Regex.IsMatch(p.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || p.Size < 1000 || p.Size > 350000000)
                throw new InvalidDataException("설치 패키지 정보가 올바르지 않습니다.");
            return p;
        }
        public static string Hash(Stream stream) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        public static string Hash(string path) { using (var s = File.OpenRead(path)) return Hash(s); }
        public static void NoLinks(string path)
        {
            var full = Path.GetFullPath(path);
            for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결 폴더에는 설치하지 않습니다: " + p);
            }
        }
        public static string SafeRelative(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 230 || name.IndexOf('\\') >= 0 || name.StartsWith("/")) throw new InvalidDataException("안전하지 않은 압축 경로입니다.");
            var parts = name.Split('/');
            if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("안전하지 않은 파일 이름입니다.");
            return Path.Combine(parts);
        }
        public static AppManifest Validate(string directory)
        {
            NoLinks(directory); var path = Path.Combine(directory, Marker); NoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 2000000) throw new IOException("기존 폴더가 Lite 전용 설치 폴더가 아닙니다. 덮어쓰지 않았습니다.");
            var m = Json.Deserialize<AppManifest>(File.ReadAllText(path));
            if (m == null || m.Product != Product || m.Repository != Repository || m.Files == null || m.Files.Count < 6 || m.Files.Count > 3000 || !Regex.IsMatch(m.Version ?? "", @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("설치 정보가 올바르지 않습니다.");
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in m.Files)
            {
                var rel = SafeRelative(item.Key); if (!expected.Add(rel) || rel.Equals(Marker, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("중복된 설치 파일입니다.");
                var f = Path.Combine(directory, rel); NoLinks(f);
                if (item.Value == null || !File.Exists(f) || new FileInfo(f).Length != item.Value.Size || !Hash(f).Equals(item.Value.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 파일 검사 실패: " + rel + ". 기존 파일은 변경하지 않았습니다.");
            }
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                var d = pending.Pop(); NoLinks(d);
                foreach (var f in Directory.EnumerateFiles(d)) { NoLinks(f); if (!f.Equals(path, StringComparison.OrdinalIgnoreCase)) actual.Add(f.Substring(directory.TrimEnd(Path.DirectorySeparatorChar).Length + 1)); }
                foreach (var sub in Directory.EnumerateDirectories(d)) pending.Push(sub);
            }
            if (!expected.SetEquals(actual)) throw new IOException("앱 폴더에 배포본 이외의 파일이 있습니다. 신청서·개인 파일은 다른 곳에 보관하세요. 덮어쓰지 않았습니다.");
            foreach (var required in new[] { Executable, "BoramRms.Lite.dll", "BoramRms.Lite.runtimeconfig.json", "hostfxr.dll", "coreclr.dll", "PresentationFramework.dll" })
                if (!expected.Contains(required)) throw new InvalidDataException("실행 환경 구성 누락: " + required);
            return m;
        }
        public static void ValidatePayload(Stream input, PayloadInfo info)
        {
            if (input.Length != info.Size || !Hash(input).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("설치 패키지가 손상됐습니다. 공식 배포 파일을 다시 받으세요.");
            input.Position = 0;
        }
        public static InstallResult Install(string target, IProgress<string> progress)
        {
            target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar); NoLinks(target);
            using (var stream = Resource("payload.zip"))
            {
                var info = Info(); progress?.Report("1/3 설치 패키지 무결성 확인…"); ValidatePayload(stream, info);
                if (Directory.Exists(target)) { var old = Validate(target); return new InstallResult { Directory = target, AlreadyInstalled = true, FileCount = old.Files.Count }; }
                if (File.Exists(target)) throw new IOException("설치 위치에 같은 이름의 파일이 있습니다. 덮어쓰지 않았습니다.");
                var parent = Path.GetDirectoryName(target); NoLinks(parent); Directory.CreateDirectory(parent);
                var drive = new DriveInfo(Path.GetPathRoot(parent));
                if (drive.AvailableFreeSpace < 1200000000) throw new IOException("설치 드라이브에 최소 1.2GB 여유 공간이 필요합니다.");
                var stage = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N")); NoLinks(stage);
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    if (zip.Entries.Count > 3200 || zip.Entries.Sum(e => e.Length) > 1200000000) throw new InvalidDataException("압축 크기 한도 초과");
                    var entries = new List<KeyValuePair<ZipArchiveEntry, string>>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.StartsWith(info.Root + "/", StringComparison.Ordinal)) throw new InvalidDataException("다른 제품이 포함된 압축입니다.");
                        var n = e.FullName.Substring(info.Root.Length + 1);
                        if (n.Length == 0) continue;
                        if ((e.ExternalAttributes >> 16 & 0xF000) == 0xA000 || (e.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("링크 파일은 허용하지 않습니다.");
                        if (n.EndsWith("/")) { SafeRelative(n.TrimEnd('/')); continue; }
                        var rel = SafeRelative(n); if (!seen.Add(rel)) throw new InvalidDataException("압축 내 중복 파일입니다.");
                        var ext = Path.GetExtension(rel).ToLowerInvariant();
                        if (new[] {".ttf", ".otf", ".woff", ".woff2", ".dpapi", ".key", ".pfx", ".pem"}.Contains(ext) || Path.GetFileName(rel).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("허용하지 않는 배포 파일입니다.");
                        entries.Add(new KeyValuePair<ZipArchiveEntry, string>(e, rel));
                    }
                    Directory.CreateDirectory(stage);
                    int count = 0;
                    foreach (var pair in entries)
                    {
                        var path = Path.Combine(stage, pair.Value); NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var src = pair.Key.Open()) using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[131072]; int read; long size = 0;
                            while ((read = src.Read(buffer, 0, buffer.Length)) > 0) { size += read; if (size > pair.Key.Length) throw new InvalidDataException("압축 길이 오류"); dst.Write(buffer, 0, read); }
                            if (size != pair.Key.Length) throw new InvalidDataException("압축 파일 누락"); dst.Flush(true);
                        }
                        if (++count % 25 == 0) progress?.Report("2/3 앱과 실행 환경 설치… " + count + "/" + entries.Count);
                    }
                    progress?.Report("3/3 설치 파일별 검증…"); var installed = Validate(stage);
                    if (installed.Version != info.Version) throw new InvalidDataException("패키지 버전 불일치");
                    NoLinks(target); Directory.Move(stage, target);
                    return new InstallResult { Directory = target, FileCount = installed.Files.Count };
                }
            }
        }
        // Kept OUTSIDE App so the existing updater can validate App's exact file set.
        public static string WriteGuide(string baseDir)
        {
            NoLinks(baseDir); Directory.CreateDirectory(baseDir); var guide = Path.Combine(baseDir, "처음설치안내.html"); NoLinks(guide);
            if (!File.Exists(guide)) using (var s = Resource("guide.html")) using (var f = new FileStream(guide, FileMode.CreateNew)) s.CopyTo(f);
            return guide;
        }
        public static string Shortcut(string folder, string target)
        {
            NoLinks(folder); Directory.CreateDirectory(folder); var name = "보람 RMS Lite.lnk"; var path = Path.Combine(folder, name); NoLinks(path);
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type == null) throw new IOException("바로가기 생성 기능을 사용할 수 없습니다.");
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(type);
                for (int n = 1; File.Exists(path); n++)
                {
                    dynamic old = ((dynamic)shell).CreateShortcut(path);
                    var same = string.Equals((string)old.TargetPath, target, StringComparison.OrdinalIgnoreCase); Marshal.FinalReleaseComObject(old);
                    if (same) return path;
                    path = Path.Combine(folder, "보람 RMS Lite (새 설치 " + n + ").lnk"); NoLinks(path);
                }
                link = ((dynamic)shell).CreateShortcut(path);
                ((dynamic)link).TargetPath = target; ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(target);
                ((dynamic)link).IconLocation = target + ",0"; ((dynamic)link).Description = "보람 RMS Lite · 독립 신청서 작업"; ((dynamic)link).Save();
                return path;
            }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
    }
}

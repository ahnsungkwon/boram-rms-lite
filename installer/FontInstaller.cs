using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BoramRms.Setup
{
    public sealed class FontPayload { public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(); public byte[] License; public long DownloadedBytes; }
    public static class FontInstaller
    {
        public const string Version = "1.3.9";
        public const string DownloadUrl = "https://github.com/orioncactus/pretendard/releases/download/v1.3.9/Pretendard-1.3.9.zip";
        // Official release ZIP digest corroborated by Homebrew's maintained font-pretendard cask.
        public const string ExpectedHash = "04be351a74d6bf7d60c480a3087e51d185485d35a52023142af1df19eb8c428a";
        public const string RegistryPath = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
        public static readonly string[] Weights = { "Regular", "Medium", "SemiBold", "Bold" };
        public static bool AlreadyAvailable()
        {
            using (var fonts = new InstalledFontCollection()) return fonts.Families.Any(f => f.Name.Equals("Pretendard", StringComparison.OrdinalIgnoreCase));
        }
        public static bool SafeAddress(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "objects-origin.githubusercontent.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        public static FontPayload Download(IProgress<string> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var uri = new Uri(DownloadUrl); byte[] archive = null;
            for (int hop = 0; hop < 5; hop++)
            {
                if (!SafeAddress(uri)) throw new InvalidDataException("허용하지 않는 서체 다운로드 주소입니다.");
                var request = (HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect = false;
                request.Timeout = 45000; request.ReadWriteTimeout = 45000; request.UserAgent = "BoramRMS-Lite-Setup/1.0";
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var code = (int)response.StatusCode;
                    if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                    { var location = response.Headers["Location"]; if (location == null) throw new IOException("서체 다운로드 경로가 없습니다."); uri = new Uri(uri, location); continue; }
                    if (code != 200 || response.ContentLength > 64000000) throw new IOException("서체 다운로드 응답이 올바르지 않습니다.");
                    using (var input = response.GetResponseStream()) using (var memory = new MemoryStream())
                    {
                        var buffer = new byte[131072]; int read; long last = 0;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (memory.Length + read > 64000000) throw new IOException("서체 다운로드 크기 초과"); memory.Write(buffer, 0, read);
                            if (memory.Length - last >= 3000000) { last = memory.Length; progress?.Report("선택 서체 공식 다운로드… " + (last / 1000000) + "MB"); }
                        }
                        archive = memory.ToArray();
                    }
                    break;
                }
            }
            if (archive == null) throw new IOException("서체 다운로드 연결 한도를 초과했습니다.");
            using (var m = new MemoryStream(archive)) if (!InstallCore.Hash(m).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("공식 서체 파일 해시가 일치하지 않습니다. 설치하지 않았습니다.");
            var result = new FontPayload { DownloadedBytes = archive.Length };
            using (var memory = new MemoryStream(archive)) using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                foreach (var weight in Weights)
                {
                    var name = "Pretendard-" + weight + ".otf"; var entry = zip.GetEntry("public/static/" + name);
                    if (entry == null || entry.Length < 1000 || entry.Length > 12000000) throw new InvalidDataException("공식 서체 구성 누락: " + name);
                    var bytes = ReadEntry(entry, 12000000);
                    if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "OTTO") throw new InvalidDataException("OpenType 서체 형식 오류");
                    result.Files.Add(name, bytes);
                }
                var license = zip.Entries.FirstOrDefault(e => e.FullName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("OFL.txt", StringComparison.OrdinalIgnoreCase));
                if (license == null) throw new InvalidDataException("서체 라이선스 파일이 없습니다.");
                result.License = ReadEntry(license, 100000);
                if (!Encoding.UTF8.GetString(result.License).Contains("SIL OPEN FONT LICENSE")) throw new InvalidDataException("서체 라이선스를 확인할 수 없습니다.");
            }
            return result;
        }
        private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
        {
            if (entry.Length > limit) throw new InvalidDataException("파일 크기 초과");
            using (var input = entry.Open()) using (var output = new MemoryStream())
            { var b = new byte[65536]; int n; while ((n = input.Read(b, 0, b.Length)) > 0) { if (output.Length + n > limit) throw new InvalidDataException("파일 크기 초과"); output.Write(b, 0, n); } return output.ToArray(); }
        }
        public static string InstallForCurrentUser(IProgress<string> progress)
        {
            if (AlreadyAvailable()) return "Pretendard가 이미 있어 기존 서체를 그대로 사용합니다.";
            var payload = Download(progress);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts", "BoramRMSLite-Pretendard-" + Version);
            InstallCore.NoLinks(folder);
            // Preflight every target before writing. Never replace a different installed font.
            foreach (var p in payload.Files)
            {
                var path = Path.Combine(folder, p.Key); InstallCore.NoLinks(path);
                if (File.Exists(path)) using (var s = new MemoryStream(p.Value)) if (InstallCore.Hash(path) != InstallCore.Hash(s)) throw new IOException("다른 서체 파일이 있어 덮어쓰지 않았습니다: " + p.Key);
            }
            using (var user = Registry.CurrentUser.OpenSubKey(RegistryPath)) using (var system = Registry.LocalMachine.OpenSubKey(RegistryPath))
                foreach (var weight in Weights)
                {
                    var name = "Pretendard " + weight + " (OpenType)";
                    var old = user?.GetValue(name) as string; var sys = system?.GetValue(name);
                    if (sys != null || (old != null && !old.Equals(Path.Combine(folder, "Pretendard-" + weight + ".otf"), StringComparison.OrdinalIgnoreCase)))
                        return "기존 Pretendard 등록을 발견해 덮어쓰지 않았습니다. 글꼴 설정에서 확인해 주세요.";
                }
            Directory.CreateDirectory(folder);
            var licensePath = Path.Combine(folder, "LICENSE.txt"); InstallCore.NoLinks(licensePath);
            if (!File.Exists(licensePath)) WriteNew(licensePath, payload.License);
            foreach (var p in payload.Files) { var path = Path.Combine(folder, p.Key); if (!File.Exists(path)) WriteNew(path, p.Value); }
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                foreach (var weight in Weights)
                {
                    var path = Path.Combine(folder, "Pretendard-" + weight + ".otf");
                    key.SetValue("Pretendard " + weight + " (OpenType)", path, RegistryValueKind.String);
                    if (AddFontResourceEx(path, 0, IntPtr.Zero) == 0) throw new IOException("서체 파일은 준비됐지만 Windows 등록을 확인하지 못했습니다. 맑은 고딕으로 사용하고 Windows 글꼴 설정을 확인해 주세요.");
                }
            IntPtr result; SendMessageTimeout(new IntPtr(0xffff), 0x001D, IntPtr.Zero, IntPtr.Zero, 2, 1000, out result);
            return "Pretendard 4개 굵기를 현재 Windows 사용자에게 설치했습니다. 새로 실행하는 앱부터 적용됩니다.";
        }
        private static void WriteNew(string path, byte[] bytes) { using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); } }
        // Verification only: private in-memory handles; no font files or registry writes.
        public static bool VerifyInMemory(byte[] bytes)
        {
            var memory = Marshal.AllocHGlobal(bytes.Length); IntPtr handle = IntPtr.Zero;
            try { Marshal.Copy(bytes, 0, memory, bytes.Length); uint count = 0; handle = AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref count); return handle != IntPtr.Zero && count > 0; }
            finally { if (handle != IntPtr.Zero) RemoveFontMemResourceEx(handle); Marshal.FreeHGlobal(memory); }
        }
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint count);
        [DllImport("gdi32.dll")] private static extern bool RemoveFontMemResourceEx(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    }
}

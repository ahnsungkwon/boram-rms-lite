using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace BoramRms.Lite;

public static class UpdateIdentity
{
    public const string Product = "BoramRms.Lite";
    public const string Repository = "ahnsungkwon/boram-rms-lite";
    public const string Executable = "BoramRms.Lite.exe";
    public const string Marker = ".lite-install.json";
    public const string Api = "https://api.github.com/repos/" + Repository;
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?\d{1,5}\.\d{1,5}\.\d{1,5}$")) throw new InvalidDataException("정식 버전 형식이 아닙니다.");
        return Version.Parse(text.TrimStart('v'));
    }
}

public static class UpdateCredential
{
    private static string TokenPath => Path.Combine(SettingsStore.DirectoryPath, "github-read-token.dpapi");
    public static bool HasStoredToken => File.Exists(TokenPath);
    public static void Save(string token)
    {
        token = token.Trim();
        if (token.Length < 20 || token.Length > 512 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("GitHub 토큰 형식을 확인하세요.");
        var raw = Encoding.UTF8.GetBytes(token);
        try { FileChanges.AtomicWrite(TokenPath, Protect(raw, true)); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public static void Forget() { if (File.Exists(TokenPath)) File.Delete(TokenPath); }
    public static async Task<string?> ResolveAsync(CancellationToken token)
    {
        if (HasStoredToken)
        {
            var raw = Protect(File.ReadAllBytes(TokenPath), false);
            try { return Encoding.UTF8.GetString(raw); } finally { CryptographicOperations.ZeroMemory(raw); }
        }
        // Optional reuse of the user's already authenticated GitHub CLI. No token is logged or exported.
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => Path.IsPathFullyQualified(p)).Select(p => Path.Combine(p, "gh.exe")));
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "auth", "token", "--hostname", "github.com" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process == null) return null;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); var value = (await stdout).Trim(); await stderr;
            return process.ExitCode == 0 && value.Length is >= 20 and <= 512 ? value : null;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); if (token.IsCancellationRequested) throw; return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] input, bool encrypt)
    {
        var blob = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) }; var output = new Blob();
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            var ok = encrypt ? CryptProtectData(ref blob, "Boram RMS Lite GitHub read access", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "GitHub 인증 암호화 정보를 처리하지 못했습니다. 인증 설정을 다시 확인하세요.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, blob.Data, input.Length); Marshal.FreeHGlobal(blob.Data);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
        }
    }
}

public sealed class UpdateManifest
{
    public string Product { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Version { get; set; } = "";
    public string Package { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Root { get; set; } = "BoramRMS_Lite";
    public void Validate()
    {
        if (Product != UpdateIdentity.Product || Repository != UpdateIdentity.Repository || Root != "BoramRMS_Lite") throw new InvalidDataException("Lite 전용 업데이트가 아닙니다.");
        _ = UpdateIdentity.ParseVersion(Version);
        if (!Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$") || Size is < 1000 or > 350000000 || Package != $"BoramRMS_Lite_{Version}_win-x64.zip") throw new InvalidDataException("업데이트 파일 정보가 잘못되었습니다.");
    }
}
public sealed record UpdateRelease(UpdateManifest Manifest, string AssetApi, string Notes, string PublishedAt)
{
    public bool IsNewer => UpdateIdentity.ParseVersion(Manifest.Version) > UpdateIdentity.ParseVersion(UpdateIdentity.CurrentVersion);
}
public sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _token;
    public GitHubUpdateClient(string? token)
    {
        _token = token;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(8) };
    }
    internal static bool SafeDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "objects-origin.githubusercontent.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    private async Task<HttpResponseMessage> GetAsync(string address, bool binary, CancellationToken token)
    {
        var uri = new Uri(address);
        if (uri.Host != "api.github.com" || !uri.AbsolutePath.StartsWith("/repos/" + UpdateIdentity.Repository + "/", StringComparison.Ordinal)) throw new InvalidDataException("다른 저장소 주소는 허용하지 않습니다.");
        for (int redirects = 0; redirects < 5; redirects++)
        {
            if (!SafeDownloadUri(uri)) throw new InvalidDataException("허용하지 않는 다운로드 주소입니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("BoramRMS-Lite/" + UpdateIdentity.CurrentVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
            if (uri.Host == "api.github.com")
            {
                if (!uri.AbsolutePath.StartsWith("/repos/" + UpdateIdentity.Repository + "/", StringComparison.Ordinal)) throw new InvalidDataException("다른 저장소로의 API 이동을 거절했습니다.");
                if (!string.IsNullOrWhiteSpace(_token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new IOException("GitHub 다운로드 경로가 없습니다.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode; response.Dispose();
                if (code is 401 or 403 or 404) throw new IOException($"GitHub 접근 확인 필요 (HTTP {code}). 비공개 저장소 읽기 권한·토큰 만료·배포 버전 유무를 확인하세요. 신청서 작업은 계속 사용할 수 있습니다.");
                throw new IOException($"GitHub 업데이트 서버 응답 오류 (HTTP {code}).");
            }
            return response;
        }
        throw new IOException("다운로드 리디렉션 한도를 초과했습니다.");
    }
    private async Task<byte[]> SmallFileAsync(string address, bool binary, CancellationToken token)
    {
        using var response = await GetAsync(address, binary, token); using var stream = await response.Content.ReadAsStreamAsync(token); using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) { if (memory.Length + count > 2000000) throw new InvalidDataException("업데이트 정보가 너무 큽니다."); memory.Write(buffer, 0, count); }
        return memory.ToArray();
    }
    public async Task<UpdateRelease> LatestAsync(CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await SmallFileAsync(UpdateIdentity.Api + "/releases/latest", false, token)); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new IOException("정식 배포 버전이 아닙니다.");
        var tag = root.GetProperty("tag_name").GetString()!; _ = UpdateIdentity.ParseVersion(tag);
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var meta = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == "update-manifest.json");
        if (meta.ValueKind == JsonValueKind.Undefined) throw new IOException("이 배포에는 자동 업데이트 정보가 없습니다.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await SmallFileAsync(meta.GetProperty("url").GetString()!, true, token), SettingsStore.Json) ?? throw new IOException("업데이트 정보가 비어 있습니다.");
        manifest.Validate();
        if (UpdateIdentity.ParseVersion(tag) != UpdateIdentity.ParseVersion(manifest.Version)) throw new InvalidDataException("배포 태그와 파일 버전이 다릅니다.");
        var asset = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == manifest.Package);
        if (asset.ValueKind == JsonValueKind.Undefined || asset.GetProperty("size").GetInt64() != manifest.Size) throw new InvalidDataException("배포 ZIP의 이름/크기가 맞지 않습니다.");
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() is { Length: > 0 } value && !value.Equals("sha256:" + manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub 자산 해시와 배포 정보가 일치하지 않습니다.");
        return new UpdateRelease(manifest, asset.GetProperty("url").GetString()!, root.GetProperty("body").GetString() ?? "", root.GetProperty("published_at").GetString() ?? "");
    }
    public async Task DownloadAsync(UpdateRelease release, string path, IProgress<double>? progress, CancellationToken token)
    {
        release.Manifest.Validate(); using var response = await GetAsync(release.AssetApi, true, token);
        using var input = await response.Content.ReadAsStreamAsync(token);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            var buffer = new byte[131072]; int count; long total = 0;
            while ((count = await input.ReadAsync(buffer, token)) > 0) { total += count; if (total > release.Manifest.Size) throw new InvalidDataException("다운로드 크기 초과"); await output.WriteAsync(buffer.AsMemory(0, count), token); progress?.Report(100.0 * total / release.Manifest.Size); }
            await output.FlushAsync(token);
            if (total != release.Manifest.Size) throw new InvalidDataException("다운로드가 완전하지 않습니다.");
        }
        if (!SafePaths.Hash(path).Equals(release.Manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("다운로드 SHA-256 검증 실패. 설치하지 않습니다.");
    }
    public void Dispose() => _http.Dispose();
}

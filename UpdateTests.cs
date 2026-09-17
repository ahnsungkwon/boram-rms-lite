using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
namespace BoramRms.Lite;
public static class UpdateTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return; } throw new Exception("거부되어야 할 작업이 허용됐습니다."); }
    private static void Fixture(string directory, string version)
    {
        Directory.CreateDirectory(directory); var bytes = new byte[8192]; new Random(41).NextBytes(bytes); File.WriteAllBytes(Path.Combine(directory,"app-test.bin"),bytes);
        var data = new InstallManifest { Version=version, Files=new() { ["app-test.bin"]=new InstallFile { Size=bytes.Length,Sha256=SafePaths.Hash(Path.Combine(directory,"app-test.bin")) } } };
        File.WriteAllText(Path.Combine(directory,UpdateIdentity.Marker),JsonSerializer.Serialize(data,SettingsStore.Json));
    }
    private static UpdateManifest ZipFixture(string root,string input,string name,string version,string? extra=null)
    {
        var path=Path.Combine(root,name+".zip");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        {
            foreach(var file in Directory.GetFiles(input)) zip.CreateEntryFromFile(file,"BoramRMS_Lite/"+Path.GetFileName(file));
            if(extra!=null) { var entry=zip.CreateEntry(extra); using var writer=new StreamWriter(entry.Open()); writer.Write("untrusted"); }
        }
        return new UpdateManifest { Product=UpdateIdentity.Product,Repository=UpdateIdentity.Repository,Version=version,Package=$"BoramRMS_Lite_{version}_win-x64.zip",Size=new FileInfo(path).Length,Sha256=SafePaths.Hash(path) };
    }
    public static void Run(string run,Action<string,Action> check)
    {
        var root=Path.Combine(run,"updater"); Directory.CreateDirectory(root);
        check("33 업데이트 정식 버전 비교",()=> { Assert(UpdateIdentity.ParseVersion("v0.10.0")>UpdateIdentity.ParseVersion("0.9.0"),"숫자 버전 비교"); Reject(()=>UpdateIdentity.ParseVersion("v1.0.0-test")); Reject(()=>UpdateIdentity.ParseVersion("../../app")); });
        check("34 HTTPS 및 GitHub 전용 주소 제한",()=> { Assert(GitHubUpdateClient.SafeDownloadUri(new Uri("https://release-assets.githubusercontent.com/file")),"GitHub CDN"); foreach(var uri in new[]{"http://api.github.com/a","https://evil.example/a","https://github.com.evil.example/","https://x@github.com/","https://github.com:444/"}) Assert(!GitHubUpdateClient.SafeDownloadUri(new Uri(uri)),"허용되지 않은 URL"); });
        check("35 압축 경로 이탈·ADS·예약 이름 거절",()=> { foreach(var name in new[]{"../outside","a/../b","/absolute","C:/data","a:payload","CON.txt","a./b","folder//file","a\\b"}) Reject(()=>UpdatePackage.SafeRelative(name)); Assert(UpdatePackage.SafeRelative("ko/PresentationFramework.resources.dll").Length>0,"정상 파일명"); });
        check("36 Windows 사용자 암호화 왕복",()=> { var raw=Encoding.UTF8.GetBytes("SYNTHETIC-ONLY-NOT-A-REAL-CREDENTIAL"); var encrypted=UpdateCredential.Protect(raw,true); Assert(!encrypted.SequenceEqual(raw),"암호화 누락"); Assert(UpdateCredential.Protect(encrypted,false).SequenceEqual(raw),"암호화 복호화"); });
        check("37 설치 파일 해시 검증 및 임의 문서 보호",()=> { var d=Path.Combine(root,"hash"); Fixture(d,"1.0.0"); UpdatePackage.ValidateInstallation(d,"1.0.0",false); File.WriteAllText(Path.Combine(d,"personal-document.txt"),"DO NOT MOVE"); Reject(()=>UpdatePackage.ValidateInstallation(d,"1.0.0",false)); Assert(File.Exists(Path.Combine(d,"personal-document.txt")),"사용자 파일 손실"); });
        check("38 검증된 ZIP 해제와 파일별 검사",()=> { var d=Path.Combine(root,"zip-valid"); Fixture(d,"1.0.0"); var m=ZipFixture(root,d,"valid","1.0.0"); UpdatePackage.ExtractVerified(Path.Combine(root,"valid.zip"),Path.Combine(root,"extracted"),m,CancellationToken.None,false); UpdatePackage.ValidateInstallation(Path.Combine(root,"extracted"),"1.0.0",false); });
        check("39 ZIP 경로 이탈 사전 차단",()=> { var d=Path.Combine(root,"zip-slip"); Fixture(d,"1.0.0"); var m=ZipFixture(root,d,"slip","1.0.0","BoramRMS_Lite/../escaped.txt"); Reject(()=>UpdatePackage.ExtractVerified(Path.Combine(root,"slip.zip"),Path.Combine(root,"slip-stage"),m,CancellationToken.None,false)); Assert(!File.Exists(Path.Combine(root,"escaped.txt")),"압축 경로 이탈"); });
        check("40 다운로드 해시 불일치 설치 차단",()=> { var d=Path.Combine(root,"bad-digest"); Fixture(d,"1.0.0"); var m=ZipFixture(root,d,"digest","1.0.0"); m.Sha256=new string('0',64); Reject(()=>UpdatePackage.ExtractVerified(Path.Combine(root,"digest.zip"),Path.Combine(root,"digest-stage"),m,CancellationToken.None,false)); Assert(!Directory.Exists(Path.Combine(root,"digest-stage")),"오류 ZIP 해제"); });
        check("41 이전 앱 백업 및 새 버전 폴더 교체",()=> { var target=Path.Combine(root,"old-app"); var stage=Path.Combine(root,"new-app"); Fixture(target,"1.0.0"); Fixture(stage,"1.1.0"); var backup=UpdatePackage.Swap(target,stage,"1.0.0","1.1.0",false); Assert(Directory.Exists(backup),"백업 누락"); UpdatePackage.ValidateInstallation(target,"1.1.0",false); UpdatePackage.ValidateInstallation(backup,"1.0.0",false); });
        check("42 다운그레이드·동일 버전 설치 차단",()=> { var target=Path.Combine(root,"downgrade-old"); var stage=Path.Combine(root,"downgrade-new"); Fixture(target,"1.1.0"); Fixture(stage,"1.0.0"); Reject(()=>UpdatePackage.Swap(target,stage,"1.1.0","1.0.0",false)); UpdatePackage.ValidateInstallation(target,"1.1.0",false); });
        check("43 다른 제품/저장소 업데이트 거절",()=> { var m=new UpdateManifest { Product="BoramRms.Native",Repository=UpdateIdentity.Repository,Version="1.0.0",Package="BoramRMS_Lite_1.0.0_win-x64.zip",Size=2000,Sha256=new string('a',64) }; Reject(m.Validate); });
        check("44 업데이트 창 렌더링 및 메인 버튼 연결",()=> { var w=new UpdateWindow(testMode:true); SelfTest.Render(w,Path.Combine(run,"update-preview.png"),630,610); w.Close(); var main=new MainWindow(testMode:true); Assert(main.FindName("UpdateButton") is Button,"업데이트 버튼 누락"); main.Close(); });
    }
    public static async Task<int> ProbeAsync(string outputDirectory)
    {
        var result=Path.Combine(outputDirectory,"UPDATE_PROBE.json"); Directory.CreateDirectory(outputDirectory);
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));
            using var client=new GitHubUpdateClient(await UpdateCredential.ResolveAsync(timeout.Token));
            var release=await client.LatestAsync(timeout.Token);
            var job=Path.Combine(outputDirectory,"probe-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
            var archive=Path.Combine(job,release.Manifest.Package); await client.DownloadAsync(release,archive,null,timeout.Token);
            var stage=Path.Combine(job,"verified"); await Task.Run(()=>UpdatePackage.ExtractVerified(archive,stage,release.Manifest,timeout.Token),timeout.Token);
            var files=UpdatePackage.ValidateInstallation(stage,release.Manifest.Version).Files.Count;
            File.WriteAllText(result,JsonSerializer.Serialize(new { success=true,repository=UpdateIdentity.Repository,current=UpdateIdentity.CurrentVersion,latest=release.Manifest.Version,release.IsNewer,downloadedBytes=new FileInfo(archive).Length,sha256=SafePaths.Hash(archive),verifiedFiles=files,stage,installed=false },SettingsStore.Json)); return 0;
        }
        catch(Exception ex) { File.WriteAllText(result,JsonSerializer.Serialize(new { success=false,error=ex.Message },SettingsStore.Json)); return 1; }
    }
}

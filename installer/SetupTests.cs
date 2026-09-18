using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BoramRms.Setup
{
    public static class SetupTests
    {
        private static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        private static void Refused(Action action) { bool failed = false; try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException) { failed = true; } Assert(failed, "위험한 대상이 거절되지 않음"); }
        public static int Run(string parent)
        {
            InstallCore.NoLinks(parent); Directory.CreateDirectory(parent);
            var run = Path.Combine(parent, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8)); Directory.CreateDirectory(run);
            File.WriteAllText(Path.Combine(parent, "latest-run.txt"), run, new UTF8Encoding(false));
            var results = new List<object>(); int passed = 0, failed = 0, appFiles = 0, appTests = 0;
            Action<string, Action> check = (name, action) => { try { action(); passed++; results.Add(new {name, passed=true}); } catch (Exception e) { failed++; results.Add(new {name, passed=false, error=e.ToString()}); } };
            var app = Path.Combine(run, "새 사용자", "Programs", "BoramRMSLite", "App");
            var info = InstallCore.Info();
            check("S01 내장 배포 버전·ZIP SHA-256 확인", () => { using (var s = InstallCore.Resource("payload.zip")) InstallCore.ValidatePayload(s, info); });
            check("S02 서체·토큰·Python 설치 프로그램 미포함", () =>
            {
                Assert(Assembly.GetExecutingAssembly().GetManifestResourceNames().OrderBy(n=>n).SequenceEqual(new[] {"brand.ico","guide.html","payload.json","payload.zip"}), "예상 외 내장 자원");
                using (var s = InstallCore.Resource("payload.zip")) using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                    Assert(!zip.Entries.Any(e => new[] {".ttf",".otf",".woff",".woff2",".dpapi",".key",".pfx"}.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()) || Path.GetFileName(e.FullName).StartsWith("python",StringComparison.OrdinalIgnoreCase)), "금지 파일 포함");
            });
            check("S03 새 사용자 경로에 앱·런타임 설치 및 전체 해시 검수", () =>
            {
                var r = InstallCore.Install(app, null); var m = InstallCore.Validate(app);
                Assert(!r.AlreadyInstalled && m.Version == info.Version && m.Files.Count == r.FileCount, "설치 결과 불일치"); appFiles = m.Files.Count;
            });
            check("S04 반복 실행은 기존 버전·파일·시각 보존", () =>
            {
                var before = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToDictionary(p=>p, p=>InstallCore.Hash(p)+File.GetLastWriteTimeUtc(p).Ticks);
                var r = InstallCore.Install(app, null); Assert(r.AlreadyInstalled && before.All(p=>p.Value==InstallCore.Hash(p.Key)+File.GetLastWriteTimeUtc(p.Key).Ticks), "재실행이 기존 파일 변경");
            });
            check("S05 관련 없는 기존 폴더 덮어쓰기 거절", () =>
            {
                var p=Path.Combine(run,"personal"); Directory.CreateDirectory(p); var f=Path.Combine(p,"중요문서.txt"); File.WriteAllText(f,"keep");
                Refused(()=>InstallCore.Install(p,null)); Assert(File.ReadAllText(f)=="keep" && Directory.GetFiles(p).Length==1,"개인 폴더 변경");
            });
            check("S06 잘못된 ZIP 해시 거절", () =>
            {
                using (var s = new MemoryStream(new byte[24])) Refused(()=>InstallCore.ValidatePayload(s,info));
            });
            check("S07 경로이탈·ADS·예약이름 거절", () =>
            {
                foreach(var name in new[]{"../x","/x","C:/x","a\\b","a//b","x:ads","con.txt","a/../b","file.","file "}) Refused(()=>InstallCore.SafeRelative(name));
                Assert(InstallCore.SafeRelative("ko/문서.txt")==Path.Combine("ko","문서.txt"),"정상 한글 경로 거절");
            });
            check("S08 안내 파일은 App 밖에 저장해 업데이트 구성 유지", () =>
            {
                var guide=InstallCore.WriteGuide(Path.GetDirectoryName(app)); Assert(File.Exists(guide) && !guide.StartsWith(app+Path.DirectorySeparatorChar),"안내 위치 오류"); InstallCore.Validate(app);
            });
            check("S09 시험 바탕화면에 실행파일·작업폴더·아이콘 바로가기 생성", () =>
            {
                var target=Path.Combine(app,InstallCore.Executable); var shortcut=InstallCore.Shortcut(Path.Combine(run,"Desktop"),target);
                dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic link=shell.CreateShortcut(shortcut);
                try { Assert(string.Equals((string)link.TargetPath,target,StringComparison.OrdinalIgnoreCase) && string.Equals((string)link.WorkingDirectory,app,StringComparison.OrdinalIgnoreCase) && ((string)link.IconLocation).Contains(InstallCore.Executable),"바로가기 대상 오류"); }
                finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
            });
            check("S10 바로가기 재생성 시 중복·덮어쓰기 방지", () =>
            {
                var folder=Path.Combine(run,"Desktop"); var first=InstallCore.Shortcut(folder,Path.Combine(app,InstallCore.Executable)); var hash=InstallCore.Hash(first);
                Assert(InstallCore.Shortcut(folder,Path.Combine(app,InstallCore.Executable))==first,"동일 바로가기 중복");
                var other=InstallCore.Shortcut(folder,Path.Combine(run,"Other.exe")); Assert(other!=first && InstallCore.Hash(first)==hash,"기존 바로가기 덮어쓰기");
            });
            check("S11 시험 시작 메뉴 바로가기 생성", () => { Assert(File.Exists(InstallCore.Shortcut(Path.Combine(run,"Start Menu","Programs","Boram RMS Lite"),Path.Combine(app,InstallCore.Executable))),"시작 메뉴 생성 실패"); });
            check("S12 서체는 HTTPS 공식 배포 호스트만 허용", () =>
            {
                Assert(FontInstaller.SafeAddress(new Uri(FontInstaller.DownloadUrl)),"공식 주소 거절");
                foreach(var uri in new[]{"http://github.com/x","https://github.com.evil.example/x","https://user@github.com/x","https://github.com:8080/x"}) Assert(!FontInstaller.SafeAddress(new Uri(uri)),"서체 주소 검증 누락");
            });
            check("S13 설치 화면 실렌더링·선택 서체 기본 미선택", () =>
            {
                using(var form=new SetupForm()){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-16000,-16000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
                    var all=Controls(form).ToList(); Assert(all.OfType<CheckBox>().Single(b=>b.Text.StartsWith("Pretendard")).Checked==false,"서체 강제 선택");
                    using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(run,"setup-preview.png"),ImageFormat.Png);} form.Close();}
            });
            check("S14 설치 앱을 별도 dotnet 명령 없이 실행하여 회귀시험", () =>
            {
                var proof=Path.Combine(run,"app-proof"); var psi=new ProcessStartInfo(Path.Combine(app,InstallCore.Executable),"--self-test \""+proof+"\""){WorkingDirectory=app,UseShellExecute=false,CreateNoWindow=true};
                psi.EnvironmentVariables["DOTNET_ROOT"]=Path.Combine(run,"no-global-dotnet"); psi.EnvironmentVariables["DOTNET_ROOT_X64"]=Path.Combine(run,"no-global-dotnet"); psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"]="0";
                using(var process=Process.Start(psi)){Assert(process!=null,"앱 시작 실패"); Assert(process.WaitForExit(180000),"앱 시험 시간 초과");Assert(process.ExitCode==0,"앱 시험 실패");}
                var latest=File.ReadAllText(Path.Combine(proof,"latest-run.txt")).Trim(); var summary=File.ReadAllText(Path.Combine(latest,"SUMMARY.txt"));
                var expected=File.ReadAllText(Path.Combine(app,"TEST_SUMMARY.txt"));
                Assert(summary.Split('\n').Take(2).SequenceEqual(expected.Split('\n').Take(2)) && summary.Split('\n')[1]=="FAIL 0" && summary.Contains("PASS K08"),"앱·패널 분리 회귀시험 실패");
                appTests=int.Parse(summary.Split('\n')[0].Substring(5)); Assert(appTests>=137,"필수 앱 시험 누락");
                File.WriteAllText(Path.Combine(run,"APP_TEST_SUMMARY.txt"),summary);
            });
            check("S15 App에 임의 파일 추가 시 검사 거절", () => { var extra=Path.Combine(app,"추가자료.txt");File.WriteAllText(extra,"not a release file");Refused(()=>InstallCore.Validate(app)); });
            File.WriteAllText(Path.Combine(run,"RESULT.json"),InstallCore.Json.Serialize(new {passed,failed,version=info.Version,appFiles,appTests,realProfileChanged=false,realFontsInstalled=false,results}),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(run,"SUMMARY.txt"),"PASS "+passed+"\nFAIL "+failed+"\n"+string.Join("\n",results.Select(r=>InstallCore.Json.Serialize(r))),new UTF8Encoding(false));
            return failed==0?0:1;
        }
        private static IEnumerable<Control> Controls(Control c) { foreach(Control child in c.Controls){yield return child;foreach(var nested in Controls(child))yield return nested;} }
        public static int FontProbe(string parent)
        {
            InstallCore.NoLinks(parent); Directory.CreateDirectory(parent);
            try
            {
                var p=FontInstaller.Download(null);var files=new List<object>();
                foreach(var item in p.Files){var valid=FontInstaller.VerifyInMemory(item.Value);if(!valid)throw new IOException("Windows 메모리 서체 로딩 실패: "+item.Key);using(var m=new MemoryStream(item.Value))files.Add(new{name=item.Key,size=item.Value.Length,sha256=InstallCore.Hash(m),privateMemoryLoad=valid});}
                File.WriteAllText(Path.Combine(parent,"FONT_PROBE.json"),InstallCore.Json.Serialize(new{success=true,version=FontInstaller.Version,downloadedBytes=p.DownloadedBytes,sha256=FontInstaller.ExpectedHash,licenseVerified=true,files,installed=false,registryChanged=false}),new UTF8Encoding(false));return 0;
            }
            catch(Exception e){File.WriteAllText(Path.Combine(parent,"FONT_PROBE.json"),InstallCore.Json.Serialize(new{success=false,error=e.ToString(),installed=false}),new UTF8Encoding(false));return 1;}
        }
    }
}

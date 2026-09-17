using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;
public static class SelfTest
{
    private sealed record TestResult(string Name, bool Passed, string Detail);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("예상한 보호 오류가 발생하지 않았습니다."); }
    public static async Task<int> RunAsync(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var run = Path.Combine(outputRoot, "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")); Directory.CreateDirectory(run);
        var oldSettings = SettingsStore.DirectoryPath; SettingsStore.DirectoryPath = Path.Combine(run, "isolated-settings");
        var results = new List<TestResult>();
        void Save() { File.WriteAllText(Path.Combine(run, "test-results.json"), JsonSerializer.Serialize(results, SettingsStore.Json)); File.WriteAllText(Path.Combine(outputRoot, "latest-run.txt"), run); }
        void Check(string name, Action test) { try { test(); results.Add(new(name, true, "통과")); } catch (Exception ex) { results.Add(new(name, false, ex.ToString())); } Save(); }
        async Task CheckAsync(string name, Func<Task> test) { try { await test(); results.Add(new(name, true, "통과")); } catch (Exception ex) { results.Add(new(name, false, ex.ToString())); } Save(); }
        FolderContext Fixture(string name, bool eventFolder = false)
        {
            var path = Path.Combine(run, name);
            Directory.CreateDirectory(eventFolder ? Path.Combine(path, "신청서", "[원본]") : path);
            return FolderContext.Resolve(path);
        }
        try
        {
            Check("01 일반 폴더/행사 폴더 경계", () =>
            {
                var normal = Fixture("plain"); var ev = Fixture("event", true);
                Assert(normal.EventRoot == null && normal.TextPath == null, "일반 폴더의 부모 TXT를 사용하면 안 됩니다.");
                Assert(ev.EventRoot != null && ev.ProcessedRoot != null && FolderContext.Resolve(ev.Root).Root == ev.Root, "행사 원본 경로 인식 실패");
                Throws(() => FolderContext.Resolve(Path.Combine(run, "missing")));
            });
            Check("02 파일명 검증/정규화", () =>
            {
                Assert(SafePaths.ValidName(" 3 testname ") == "3TESTNAME", "이름 정규화");
                foreach (var s in new[] { "0홍길동", "100홍길동", "12", "3../가", "홍길동", "3홍길동." }) Throws(() => SafePaths.ValidName(s));
                Assert(SafePaths.ValidName("12가상인물") == "12가상인물", "두 자리 구좌");
            });
            Check("03 목록 파생본/참고자료 제외 및 읽기 무변경", () =>
            {
                var c = Fixture("catalog"); AddImage(c.Root, "1가상가.png"); AddImage(c.Root, "보완/2가상나.png"); AddImage(c.Root, "jpg/1가상가.png"); AddImage(c.Root, "보완/[참고자료]/참고.png");
                var output = Path.Combine(c.Root, "resized"); AddImage(output, "copy.png"); File.WriteAllText(Path.Combine(output, LocalData.OutputMarker), "{}");
                var before = Snapshot(c.Root); var list = LocalData.Load(c);
                Assert(list.Count == 2 && list.Any(i => i.Status == "보완"), "원본/보완만 목록에 포함해야 합니다.");
                Assert(SameSnapshot(before, Snapshot(c.Root)), "읽기 과정에서 파일이 변경됨");
                Assert(list.All(i => i.Registration == "등록 여부 확인 안 됨"), "ERP 미확인 상태 표시");
            });
            Check("04 리네임 원본/가공본/카드/TXT 동기화 및 되돌리기", () =>
            {
                var c = Fixture("rename-event", true); AddImage(c.Root, "카드/3가상가.png"); AddImage(c.ProcessedRoot!, "3구좌/3가상가.jpg");
                File.WriteAllText(c.TextPath!, "머리말 보존\r\n●보기\r\n다른 내용\r\n●보완\r\n3가상가\t주소보완\r\n", new UTF8Encoding(true));
                var oldText = File.ReadAllBytes(c.TextPath!); var oldItem = LocalData.Load(c).Single(); var oldHash = SafePaths.Hash(oldItem.FullPath);
                var renamed = LocalData.Rename(c, oldItem, "2가상나");
                Assert(File.Exists(renamed) && !File.Exists(oldItem.FullPath) && SafePaths.Hash(renamed) == oldHash, "원본 이동/내용");
                Assert(File.Exists(Path.Combine(c.ProcessedRoot!, "2구좌", "2가상나.jpg")), "가공본 구좌 동기화");
                var after = LocalData.Load(c).Single(); Assert(after.Card && after.Status == "보완", "카드/상태 유지");
                var text = File.ReadAllBytes(c.TextPath!); Assert(text.Take(3).SequenceEqual(new byte[] { 239,187,191 }), "BOM 유지");
                Assert(File.ReadAllText(c.TextPath!).Contains("2가상나\t주소보완") && File.ReadAllText(c.TextPath!).Contains("머리말 보존\r\n"), "TXT 이름/줄바꿈 보존");
                FileChanges.Undo(FileChanges.LatestCompleted(LocalData.JournalRoot(c))!);
                Assert(File.Exists(oldItem.FullPath) && File.ReadAllBytes(c.TextPath!).SequenceEqual(oldText), "되돌리기");
            });
            Check("05 파일명 중복 덮어쓰기 방지", () =>
            {
                var c = Fixture("rename-collision"); AddImage(c.Root, "1가상가.png"); AddImage(c.Root, "1가상나.png");
                var hash = SafePaths.Hash(Path.Combine(c.Root, "1가상나.png"));
                var renamed = LocalData.Rename(c, LocalData.Load(c).Single(i => i.Name == "가상가"), "1가상나");
                Assert(Path.GetFileName(renamed) == "1가상나 (2).png" && SafePaths.Hash(Path.Combine(c.Root, "1가상나.png")) == hash, "중복 보호 실패");
            });
            Check("06 가공본 충돌 시 원본 변경 전 중단", () =>
            {
                var c = Fixture("processed-collision", true); AddImage(c.Root, "1가상가.png"); AddImage(c.ProcessedRoot!, "1구좌/1가상가.jpg"); AddImage(c.ProcessedRoot!, "2구좌/2가상나.jpg");
                var before = Snapshot(c.EventRoot!); Throws(() => LocalData.Rename(c, LocalData.Load(c).Single(), "2가상나"));
                Assert(SameSnapshot(before, Snapshot(c.EventRoot!)), "충돌 시 원본 변경 발생");
            });
            Check("07 카드/CMS 전환과 상태 제거", () =>
            {
                var c = Fixture("card-state", true); AddImage(c.Root, "1가상가.png");
                LocalData.Change(c, LocalData.Load(c).Single(), null, null, "", true);
                Assert(LocalData.Load(c).Single().Card && File.Exists(Path.Combine(c.Root,"카드","1가상가.png")), "카드 이동");
                LocalData.Change(c, LocalData.Load(c).Single(), null, null, "", false);
                Assert(!LocalData.Load(c).Single().Card && File.Exists(Path.Combine(c.Root,"1가상가.png")), "CMS 복귀");
                LocalData.Change(c, LocalData.Load(c).Single(), null, "보완", "주소보완", true);
                Assert(LocalData.Load(c).Single().Card && LocalData.Load(c).Single().Status == "보완", "보완/카드 복합 상태");
                LocalData.Change(c, LocalData.Load(c).Single(), null, "", "", false);
                Assert(LocalData.Load(c).Single().Status == "" && !LocalData.Load(c).Single().Card, "상태 제거");
            });
            Check("08 입력 전/후 구좌 변경 구분", () =>
            {
                var c = Fixture("quota-change", true); AddImage(c.Root,"3가상가.png"); AddImage(c.ProcessedRoot!, "3구좌/3가상가.jpg");
                LocalData.Change(c, LocalData.Load(c).Single(), "2가상가", "입력전 변경", "3구좌-->2구좌", false);
                var item = LocalData.Load(c).Single(); Assert(item.Quota == "2" && item.Status == "입력전 변경", "입력전 변경 추적");
                Assert(File.ReadAllText(c.TextPath!).Contains("3가상가\t3구좌-->2구좌"), "원래 구좌 기록 보존");
                LocalData.Change(c, item, null, "입력후 변경", "2구좌-->1구좌", false);
                Assert(LocalData.Load(c).Single().Quota == "2" && File.Exists(Path.Combine(c.ProcessedRoot!, "2구좌", "2가상가.jpg")), "입력후 변경은 원본 구좌 유지");
            });
            Check("09 부분 실패 자동 복구", () =>
            {
                var c = Fixture("rollback"); var a = Path.Combine(c.Root,"a.txt"); var b = Path.Combine(c.Root,"b.txt"); var blocker = Path.Combine(c.Root,"blocker"); File.WriteAllText(a,"A"); File.WriteAllText(b,"B"); File.WriteAllText(blocker,"FILE");
                var op = new FileChanges(LocalData.JournalRoot(c), c.Root); op.Move(a, Path.Combine(c.Root,"a-new.txt")); op.Move(b, Path.Combine(blocker,"b-new.txt"));
                Throws(() => op.Apply("의도적 두 번째 이동 실패"));
                Assert(File.ReadAllText(a) == "A" && File.ReadAllText(b) == "B" && !File.Exists(Path.Combine(c.Root,"a-new.txt")), "부분 실패 복구 실패");
            });
            Check("10 외부 수정 감지 및 되돌리기 덮어쓰기 거절", () =>
            {
                var c = Fixture("external-change"); AddImage(c.Root,"1가상가.png"); var item = LocalData.Load(c).Single();
                var target = LocalData.Rename(c,item,"2가상나"); File.AppendAllText(target,"external");
                var hash = SafePaths.Hash(target); Throws(() => FileChanges.Undo(FileChanges.LatestCompleted(LocalData.JournalRoot(c))!));
                Assert(SafePaths.Hash(target) == hash, "외부 수정을 덮어씀");
            });
            Check("11 일반 폴더의 부모 TXT 보호", () =>
            {
                var c = Fixture("plain-txt"); var parentTxt = Path.Combine(run,"unrelated.txt"); File.WriteAllText(parentTxt,"DO NOT CHANGE"); AddImage(c.Root,"1가상가.png");
                LocalData.Change(c,LocalData.Load(c).Single(),null,"보완","메모",false);
                Assert(File.ReadAllText(parentTxt) == "DO NOT CHANGE" && Directory.GetFiles(c.Root,"*.txt").Length == 0, "관련 없는 TXT 변경");
            });
            Check("12 용량/픽셀/비율 제한 및 리사이즈 원본 SHA 보존", () =>
            {
                var c = Fixture("resize"); AddNoise(c.Root,"1가상가.png",1600,2400); var item = LocalData.Load(c).Single(); var before = SafePaths.Hash(item.FullPath);
                var result = ImageProcessing.ResizeBatch(c,new[] {item},Path.Combine(c.Root,"output"),new ResizeOptions(),null,CancellationToken.None);
                var entry = result.Items.Single(); Assert(entry.Success, entry.Error);
                Assert(entry.Bytes <= 300*1024 && entry.Width <= 1040 && Math.Abs((double)entry.Width/entry.Height - 2.0/3) < .003, "용량/픽셀/비율");
                Assert(SafePaths.Hash(item.FullPath) == before && LocalData.Load(c).Count == 1, "원본 변경/파생본 재수집");
            });
            Check("13 JPEG 흰 배경/PNG 투명도/확대 금지", () =>
            {
                var c = Fixture("alpha"); var pixels = new byte[100*80*4]; var src = BitmapSource.Create(100,80,96,96,PixelFormats.Bgra32,null,pixels,400); src.Freeze();
                var jpg = ImageProcessing.Compress(src,new ResizeOptions(),CancellationToken.None); var path = Path.Combine(c.Root,"white.jpg"); File.WriteAllBytes(path,jpg.Bytes);
                var image = new FormatConvertedBitmap(ImageProcessing.Load(path),PixelFormats.Bgra32,null,0); var pixel = new byte[4]; image.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);
                Assert(pixel[0] > 245 && pixel[1] > 245 && pixel[2] > 245 && jpg.Width == 100 && jpg.Height == 80, "JPEG 배경 또는 확대 오류");
                var png = ImageProcessing.Compress(src,new ResizeOptions { Png=true },CancellationToken.None); path=Path.Combine(c.Root,"alpha.png"); File.WriteAllBytes(path,png.Bytes);
                image = new FormatConvertedBitmap(ImageProcessing.Load(path),PixelFormats.Bgra32,null,0); image.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0); Assert(pixel[3] == 0,"PNG 투명도 손실");
            });
            Check("14 jpg/png 동명 출력 충돌 보호", () =>
            {
                var c=Fixture("resize-collision"); AddImage(c.Root,"1가상가.png"); AddImage(c.Root,"1가상가.jpg");
                var r=ImageProcessing.ResizeBatch(c,LocalData.Load(c),Path.Combine(c.Root,"output"),new ResizeOptions(),null,CancellationToken.None);
                Assert(r.Items.All(i=>i.Success) && r.Items.Select(i=>i.OutputRelative).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2,"형식 변환 충돌");
            });
            Check("15 손상 파일 분리/취소/출력 경로 보호", () =>
            {
                var c=Fixture("resize-errors"); AddImage(c.Root,"1가상가.png"); File.WriteAllText(Path.Combine(c.Root,"2손상.png"),"NOT AN IMAGE"); var inputs=LocalData.Load(c);
                var r=ImageProcessing.ResizeBatch(c,inputs,Path.Combine(c.Root,"out"),new ResizeOptions(),null,CancellationToken.None);
                Assert(r.Items.Count(i=>i.Success)==1 && r.Items.Count(i=>!i.Success)==1,"파일별 오류 분리");
                Throws(()=>ImageProcessing.ResizeBatch(c,inputs,c.Root,new ResizeOptions(),null,CancellationToken.None));
                var cancelled=Path.Combine(c.Root,"cancelled"); using var cancel=new CancellationTokenSource(); cancel.Cancel(); Throws(()=>ImageProcessing.ResizeBatch(c,inputs,cancelled,new ResizeOptions(),null,cancel.Token));
                Assert(!Directory.Exists(cancelled),"사전 취소 후 출력 생성");
                Throws(()=>ImageProcessing.ResizeBatch(c,inputs,Path.Combine(c.Root,"out"),new ResizeOptions(),null,CancellationToken.None));
            });
            Check("16 EXIF 방향 및 여러 페이지 이미지 보호", () =>
            {
                var c=Fixture("orientation"); var pixels=new byte[120*80*3]; Array.Fill(pixels,(byte)180); var src=BitmapSource.Create(120,80,96,96,PixelFormats.Bgr24,null,pixels,360); src.Freeze();
                var metadata=new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}",(ushort)6); var enc=new JpegBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src,null,metadata,null));
                var path=Path.Combine(c.Root,"rotated.jpg"); using(var fs=File.Create(path)) enc.Save(fs);
                var loaded=ImageProcessing.Load(path); Assert(loaded.PixelWidth==80 && loaded.PixelHeight==120,"EXIF 방향 처리");
                var multi=new TiffBitmapEncoder(); multi.Frames.Add(BitmapFrame.Create(src)); multi.Frames.Add(BitmapFrame.Create(src)); path=Path.Combine(c.Root,"multi.tiff"); using(var fs=File.Create(path)) multi.Save(fs); Throws(()=>ImageProcessing.Load(path));
            });
            Check("17 원본 회전 백업 및 복구", () =>
            {
                var c=Fixture("rotate-original"); AddImage(c.Root,"1가상가.png",400,600); var item=LocalData.Load(c).Single(); var hash=SafePaths.Hash(item.FullPath);
                ImageProcessing.Rotate(c,item,true); var rotated=ImageProcessing.Load(item.FullPath); Assert(rotated.PixelWidth==600 && rotated.PixelHeight==400,"원본 회전");
                FileChanges.Undo(FileChanges.LatestCompleted(LocalData.JournalRoot(c))!); Assert(SafePaths.Hash(item.FullPath)==hash,"회전 원본 복구");
            });
            Check("18 리네임 이후 리사이즈 사본 추적", () =>
            {
                var c=Fixture("derivative-rename"); AddImage(c.Root,"1가상가.png"); var item=LocalData.Load(c).Single(); var output=Path.Combine(c.Root,"output");
                ImageProcessing.ResizeBatch(c,new[]{item},output,new ResizeOptions(),null,CancellationToken.None); LocalData.Rename(c,item,"2가상나");
                Assert(File.Exists(Path.Combine(output,"2가상나.jpg")) && !File.Exists(Path.Combine(output,"1가상가.jpg")),"사본 이름 동기화");
                var manifest=LocalData.OutputManifests(c).Single().Manifest; Assert(manifest.Items.Single().SourceRelative=="2가상나.png","사본 연결 갱신");
            });
            Check("19 보완 대체 이동과 대상/출발 동시 복구", () =>
            {
                var source=Fixture("supplement-source"); var target=Fixture("supplement-target",true);
                AddImage(source.Root,"2가상가.png",500,700); AddImage(target.Root,"카드/2가상가.jpg",400,600); AddImage(target.ProcessedRoot!,"2구좌/2가상가.jpg",100,150);
                var before=Snapshot(source.Root); var targetBefore=Snapshot(target.EventRoot!);
                var plan=SupplementService.Plan(source,target,LocalData.Load(source).Single()); SupplementService.Apply(plan);
                Assert(!File.Exists(plan.Item.FullPath) && File.Exists(plan.OriginalDestination) && new FileInfo(plan.ProcessedDestination).Length<=300*1024,"대체 이동 결과");
                Assert(LocalData.Load(target).Count==1 && LocalData.Load(target).Single().Card,"대체 후 중복/카드");
                FileChanges.Undo(FileChanges.LatestCompleted(LocalData.JournalRoot(target))!);
                Assert(SameOriginals(before,Snapshot(source.Root)) && SameOriginals(targetBefore,Snapshot(target.EventRoot!)),"대체 이동 원본 복구");
            });
            Check("20 전화번호 매칭 런타임·데이터 필드 제거", () =>
            {
                Assert(typeof(MainWindow).Assembly.GetType("BoramRms.Lite.PhoneResults")==null,"매칭 실행 코드 잔존");
                Assert(typeof(ImageItem).GetProperty("PhoneIssue")==null,"매칭 상태 필드 잔존");
            });
            Check("21 폴더 동시 쓰기 잠금", () =>
            {
                var c=Fixture("lock"); using var lease=new FolderLease(c.Root); Assert(lease.Writable,"첫 잠금"); bool second=true;
                var thread=new Thread(()=>{ using var other=new FolderLease(c.Root); second=other.Writable; }); thread.Start(); thread.Join(); Assert(!second,"두 번째 작업자가 쓰기 잠금을 얻음");
            });
            Check("22 Lite 전용 설정 저장/복원", () =>
            {
                SettingsStore.Save(new SavedSettings { Width=1400, Folders=new List<string>{run} });
                Assert(SettingsStore.Load().Width==1400 && SettingsStore.DirectoryPath.StartsWith(run),"설정 격리");
            });
            await CheckAsync("23 메인 화면 로드/중복탭/검색/이미지 선택/분리 복귀", async () =>
            {
                var c=Fixture("ui-synthetic"); AddImage(c.Root,"1가상가.png"); AddImage(c.Root,"2가상나.png"); AddImage(c.Root,"보완/3가상다.png");
                var window=new MainWindow(testMode:true) { ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.Manual, Left=-16000, Top=-16000 };
                window.Show();
                try
                {
                    await window.OpenFolderAsync(c.Root); await window.LastPreviewTask;
                    await window.OpenFolderAsync(c.Root); await window.LastPreviewTask;
                    Assert(window.Tabs.Count==1 && window.AllItems.Count==3,"중복 탭/목록");
                    var list=(ListBox)window.FindName("ImageList"); list.SelectedIndex=1; await window.LastPreviewTask;
                    Assert(((Image)window.FindName("PreviewImage")).Source!=null,"이미지 로드");
                    var search=(TextBox)window.FindName("SearchText"); search.Text="가상나"; await window.LastPreviewTask; Assert(list.Items.Count==1,"검색 필터"); search.Text=""; await window.LastPreviewTask;
                    var host=(ContentControl)window.FindName("RightHost"); var content=host.Content; window.ToggleDock("list",host,"신청서 목록",340); window.ToggleDock("list",host,"신청서 목록",340); Assert(ReferenceEquals(host.Content,content),"분리창 복귀");
                    window.Width=1520; window.Height=960; Render(window,Path.Combine(run,"main-preview.png"),1504,921);
                }
                finally { window.Close(); }
            });
            await CheckAsync("24 일반 분리창 표시 및 트레이싱 제거", async () =>
            {
                var panel=new PanelWindow("검증용 이미지",new Border { Background=Brushes.White },500) { Left=-16000, Top=-16000, WindowStartupLocation=WindowStartupLocation.Manual };
                panel.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                try { Assert(panel.Opacity==1 && !panel.AllowsTransparency && panel.WindowStyle==WindowStyle.SingleBorderWindow,"일반 창 표시"); Assert(typeof(PanelWindow).GetMethod("SetTracing")==null,"트레이싱 구현 잔존"); panel.ReleaseContent(); }
                finally { panel.Close(); }
            });
            await CheckAsync("25 활성 탭 닫기/마지막 탭 종료/파일 보존", async () =>
            {
                var first=Fixture("tab-first"); var second=Fixture("tab-second"); AddImage(first.Root,"1가상가.png"); AddImage(second.Root,"2가상나.png");
                var firstHash=Snapshot(first.Root); var secondHash=Snapshot(second.Root);
                var w=new MainWindow(testMode:true) { ShowInTaskbar=false, Left=-16000, Top=-16000, WindowStartupLocation=WindowStartupLocation.Manual }; w.Show();
                try
                {
                    await w.OpenFolderAsync(first.Root); await w.LastPreviewTask;
                    await w.OpenFolderAsync(second.Root); await w.LastPreviewTask;
                    await w.CloseTabAsync(w.Tabs.Single(t=>SafePaths.Same(t.Context.Root,second.Root))); await w.LastPreviewTask;
                    Assert(w.Tabs.Count==1 && w.AllItems.Single().Name=="가상가","활성 탭 종료 후 남은 탭 표시");
                    await w.CloseTabAsync(w.Tabs.Single()); await w.LastPreviewTask;
                    Assert(w.Tabs.Count==0 && w.AllItems.Count==0 && ((Image)w.FindName("PreviewImage")).Source==null,"마지막 탭 종료 초기화");
                    Assert(SameSnapshot(firstHash,Snapshot(first.Root)) && SameSnapshot(secondHash,Snapshot(second.Root)),"탭 닫기가 파일을 변경함");
                }
                finally { w.Close(); }
            });
            await CheckAsync("26 UI 저장+다음/상태 입력 묵시적 손실 방지", async () =>
            {
                var c=Fixture("ui-save"); AddImage(c.Root,"1가상가.png"); AddImage(c.Root,"2가상나.png");
                var w=new MainWindow(testMode:true) { ShowInTaskbar=false, Left=-16000, Top=-16000, WindowStartupLocation=WindowStartupLocation.Manual }; w.Show();
                try
                {
                    await w.OpenFolderAsync(c.Root); await w.LastPreviewTask;
                    var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                    var old=(ImageItem)list.SelectedItem; var remaining=w.AllItems.Single(i=>i.FullPath!=old.FullPath).FullPath;
                    var input=(TextBox)w.FindName("CombinedTextBox"); input.Text="3가상라";
                    Assert(w.HasPendingRename,"리네임 편집 상태 추적"); await w.SaveRenameAsync(true); await w.LastPreviewTask;
                    Assert(File.Exists(Path.Combine(c.Root,"3가상라.png")) && !File.Exists(old.FullPath),"화면 리네임 저장");
                    Assert(((ImageItem)list.SelectedItem).FullPath==remaining,"저장+다음 선택");
                    var memo=(TextBox)w.FindName("StatusMemo"); memo.Text="보존할 메모"; input.Text="4가상마";
                    Assert(w.HasPendingStatus && w.HasPendingRename,"상태/리네임 미저장 추적"); var before=Snapshot(c.Root);
                    await w.SaveRenameAsync(false);
                    Assert(memo.Text=="보존할 메모" && SameSnapshot(before,Snapshot(c.Root)),"확인 없이 다른 입력이 버려지거나 저장됨");
                    await w.ReloadAsync(remaining); await w.LastPreviewTask;
                }
                finally { w.Close(); }
            });
            Check("27 이름만 있는 CP949 TXT 호환/동명이인 변경 중단", () =>
            {
                var c=Fixture("name-only",true); AddImage(c.Root,"1가상가.png");
                var enc=Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
                File.WriteAllBytes(c.TextPath!,enc.GetBytes("표제 보존\r\n●보완\r\n가상가\t주소보완\r\n"));
                Assert(LocalData.Load(c).Single().Status=="보완","이름만 기록된 상태 읽기");
                LocalData.Rename(c,LocalData.Load(c).Single(),"1가상나");
                var text=enc.GetString(File.ReadAllBytes(c.TextPath!)); Assert(text.Contains("가상나\t주소보완") && text.StartsWith("표제 보존\r\n"),"원래 인코딩/이름만 기록 보존");
                AddImage(c.Root,"2가상나.png"); var before=Snapshot(c.EventRoot!);
                Throws(()=>LocalData.Rename(c,LocalData.Load(c).Single(i=>i.Quota=="1"),"3가상다"));
                Assert(SameSnapshot(before,Snapshot(c.EventRoot!)),"동명이인 기록을 임의로 변경함");
            });
            Check("28 확장자가 다른 행사 신청서의 동일 식별 충돌", () =>
            {
                var c=Fixture("identity-collision",true); AddImage(c.Root,"1가상가.png"); AddImage(c.Root,"카드/2가상나.jpg"); var before=Snapshot(c.EventRoot!);
                Throws(()=>LocalData.Rename(c,LocalData.Load(c).Single(i=>i.Name=="가상가"),"2가상나"));
                Assert(SameSnapshot(before,Snapshot(c.EventRoot!)),"다른 확장자 동명 자료 충돌을 놓침");
            });
            Check("29 반복 구좌 변경 뒤 이름 변경의 최초 구좌 이력 유지", () =>
            {
                var c=Fixture("quota-history",true); AddImage(c.Root,"4가상가.png"); AddImage(c.ProcessedRoot!,"4구좌/4가상가.jpg");
                LocalData.Change(c,LocalData.Load(c).Single(),"3가상가","입력전 변경","4구좌-->3구좌",false);
                LocalData.Change(c,LocalData.Load(c).Single(),"2가상가","입력전 변경","3구좌-->2구좌",false);
                Assert(File.ReadAllText(c.TextPath!).Contains("4가상가\t4구좌-->2구좌"),"최초 구좌 이력 손실");
                LocalData.Rename(c,LocalData.Load(c).Single(),"1가상나");
                Assert(File.ReadAllText(c.TextPath!).Contains("4가상나\t4구좌-->1구좌") && LocalData.Load(c).Single().Status=="입력전 변경","변경 이력의 이름/최종 구좌 연결");
            });
            Check("30 jpg 결과 폴더 중복 이동 방지/수정된 사본 보호", () =>
            {
                var c=Fixture("jpg-tracking"); AddImage(c.Root,"1가상가.jpg");
                var output=Path.Combine(c.Root,"jpg"); ImageProcessing.ResizeBatch(c,LocalData.Load(c),output,new ResizeOptions(),null,CancellationToken.None);
                LocalData.Rename(c,LocalData.Load(c).Single(),"2가상나"); Assert(File.Exists(Path.Combine(output,"2가상나.jpg")),"jpg 결과 폴더에 대해 이동이 중복 실행됨");
                File.AppendAllText(Path.Combine(output,"2가상나.jpg"),"external change"); var before=Snapshot(c.Root);
                Throws(()=>LocalData.Rename(c,LocalData.Load(c).Single(),"3가상다")); Assert(SameSnapshot(before,Snapshot(c.Root)),"외부 수정 사본을 변경함");
            });
            Check("31 진행 중 취소와 결과 파일 개수 보존", () =>
            {
                var c=Fixture("cancel-during"); AddImage(c.Root,"1가상가.png"); AddImage(c.Root,"2가상나.png"); var before=Snapshot(c.Root);
                using var stop=new CancellationTokenSource(); var progress=new ImmediateProgress<ResizeEntry>(_=>stop.Cancel());
                var r=ImageProcessing.ResizeBatch(c,LocalData.Load(c),Path.Combine(c.Root,"out"),new ResizeOptions(),progress,stop.Token);
                Assert(r.Cancelled && r.Items.Count(i=>i.Success)==1,"취소 이후 추가 성공 기록"); Assert(before.All(p=>SafePaths.Hash(p.Key)==p.Value),"취소 과정에서 원본 변경");
            });
            Check("32 복구 대상 범위 검증/드라이브 경계", () =>
            {
                var c=Fixture("undo-scope"); AddImage(c.Root,"1가상가.png"); LocalData.Rename(c,LocalData.Load(c).Single(),"2가상나");
                var journal=FileChanges.LatestCompleted(LocalData.JournalRoot(c))!; var before=Snapshot(c.Root);
                Throws(()=>FileChanges.Undo(journal,allowedRoots:new[]{Path.Combine(run,"other")})); Assert(SameSnapshot(before,Snapshot(c.Root)),"허용하지 않은 복구 범위");
                Assert(SafePaths.Under(c.Root,Path.GetPathRoot(c.Root)!) && !SafePaths.Under(c.Root+"-other",c.Root),"드라이브/폴더 경계 판정");
            });
            UpdateTests.Run(run, Check);
            await UiRevisionTests.RunAsync(run, Check, CheckAsync);
            await BrandInputTests.RunAsync(run, Check, CheckAsync);
        }
        catch (Exception ex) { results.Add(new("테스트 실행기 오류",false,ex.ToString())); }
        finally { SettingsStore.DirectoryPath=oldSettings; Save(); }
        var passed=results.Count(r=>r.Passed); var failed=results.Count-passed;
        File.WriteAllText(Path.Combine(run,"SUMMARY.txt"),$"PASS {passed}\nFAIL {failed}\n"+string.Join("\n",results.Select(r=>$"{(r.Passed?"PASS":"FAIL")} {r.Name}: {r.Detail}")));
        return failed==0 ? 0 : 1;
    }
    public static void AddImage(string root,string relative,int width=800,int height=1120)
    {
        var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var visual=new DrawingVisual(); using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
            var scale=width/800.0; dc.PushTransform(new ScaleTransform(scale,scale));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(234,242,227)),null,new Rect(40,40,720,100));
            DrawText(dc,"신청서 · 합성 테스트 자료",28,65,68,Brushes.DarkGreen);
            DrawText(dc,"실제 고객 정보가 아닌 앱 검증용 문서입니다.",16,65,110,Brushes.DimGray);
            DrawText(dc,"이름   "+Path.GetFileNameWithoutExtension(relative),22,65,195,Brushes.Black);
            DrawText(dc,"개인정보 / 연락처 / 계좌번호 : 기재하지 않음",17,65,255,Brushes.Black);
            for(int i=0;i<7;i++) { dc.DrawRectangle(null,new Pen(Brushes.Gray,1),new Rect(60,320+i*75,680,65)); DrawText(dc,$"확인 항목 {i+1}    □ 확인    □ 보완 필요",18,78,339+i*75,Brushes.Black); }
            DrawText(dc,"보람 RMS Lite — UI 검증 전용",17,65,950,Brushes.Gray); dc.Pop();
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        File.WriteAllBytes(path,ImageProcessing.Encode(bitmap,Path.GetExtension(path)));
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static void DrawText(DrawingContext dc,string text,double size,double x,double y,Brush brush) => dc.DrawText(new FormattedText(text,CultureInfo.GetCultureInfo("ko-KR"),FlowDirection.LeftToRight,new Typeface("Malgun Gothic"),size,brush,1),new Point(x,y));
    private static void AddNoise(string root,string name,int width,int height)
    {
        Directory.CreateDirectory(root); var data=new byte[width*height*3]; new Random(1729).NextBytes(data);
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr24,null,data,width*3); image.Freeze(); File.WriteAllBytes(Path.Combine(root,name),ImageProcessing.Encode(image,".png"));
    }
    private static Dictionary<string,string> Snapshot(string root) => Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,SafePaths.Hash,StringComparer.OrdinalIgnoreCase);
    private static bool SameSnapshot(Dictionary<string,string> a,Dictionary<string,string> b) => a.Count==b.Count && a.All(p=>b.GetValueOrDefault(p.Key)==p.Value);
    private static bool SameOriginals(Dictionary<string,string> a,Dictionary<string,string> b) => a.All(p=>b.GetValueOrDefault(p.Key)==p.Value) && b.Keys.Where(p=>!p.Contains(Path.DirectorySeparatorChar+".boramrms"+Path.DirectorySeparatorChar)).All(a.ContainsKey);
    public static void Render(Window window,string path,int width,int height)
    {
        var element=(FrameworkElement)window.Content; element.Measure(new Size(width,height)); element.Arrange(new Rect(0,0,width,height)); element.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(element); File.WriteAllBytes(path,ImageProcessing.Encode(bitmap,".png"));
    }
}

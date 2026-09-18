using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
namespace BoramRms.Lite;

public static class FunctionalFixTests
{
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("보호 오류가 발생하지 않음"); }
    private static FolderContext Folder(string run, string name, bool legacy = false)
    {
        var root = Path.Combine(run, name);
        Directory.CreateDirectory(legacy ? Path.Combine(root, "신청서", "[원본]") : root);
        return FolderContext.Resolve(root);
    }
    private static async Task InWindow(FolderContext ctx, Func<MainWindow, Task> action)
    {
        var w = new MainWindow(testMode: true) { Left = -16000, Top = -16000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(ctx.Root); await w.LastPreviewTask; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await action(w); }
        finally { await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static void AssertFit(MainWindow w)
    {
        var image = (Image)w.FindName("PreviewImage"); var stage = (FrameworkElement)w.FindName("ImageStage");
        var scale = (ScaleTransform)w.FindName("ImageScale"); var pan = (TranslateTransform)w.FindName("ImageTranslate");
        Assert(scale.ScaleX == 1 && pan.X == 0 && pan.Y == 0, "리사이즈 후 확대/위치가 남음");
        var bounds = image.TransformToAncestor(stage).TransformBounds(new Rect(image.RenderSize));
        Assert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= stage.ActualWidth + 1 && bounds.Bottom <= stage.ActualHeight + 1, "축소 창에서 이미지 경계가 잘림: " + bounds);
    }
    private static string Interrupted(FolderContext c)
    {
        var dir = Path.Combine(LocalData.JournalRoot(c), "synthetic-interrupted"); Directory.CreateDirectory(dir);
        var j = new ChangeJournal { Description = "리네임", State = "recovering", Roots = new[] { c.EventRoot ?? c.Root } };
        foreach (var name in new[] { "account-types.json", "lite-states.json" })
        {
            var file = Path.Combine(c.Metadata, name); File.WriteAllText(file, "{}");
            var backup = j.Files.Count.ToString("0000") + ".bak"; File.Copy(file, Path.Combine(dir, backup));
            j.Files.Add(new FileSnapshot { Path = file, BeforeHash = SafePaths.Hash(file), AfterHash = SafePaths.Hash(file), Backup = backup, ModifiedUtc = File.GetLastWriteTimeUtc(file) });
        }
        var journal = Path.Combine(dir, "journal.json"); File.WriteAllText(journal, JsonSerializer.Serialize(j, SettingsStore.Json)); return journal;
    }
    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        var root = Path.Combine(run, "functional-fixes"); Directory.CreateDirectory(root);
        await checkAsync("64 창 축소·확대 시 이미지 전체 경계 표시", async () =>
        {
            var c = Folder(root, "resize-window"); SelfTest.AddImage(c.Root, "2가상가.png", 1050,1400);
            await InWindow(c, async w =>
            {
                w.Width = 1440; w.Height = 960; w.UpdateLayout(); w.SetViewport(2.48,270,160);
                w.Width = 1080; w.Height = 740; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); AssertFit(w);
                SelfTest.Render(w, Path.Combine(run, "small-window-fit.png"),1064,701);
                w.Width = 1440; w.Height = 960; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); AssertFit(w);
            });
        });
        await checkAsync("65 패널 폭·작업 기록 높이 변경 및 다음 이미지 보기 유지", async () =>
        {
            var c = Folder(root, "resize-panels"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png");
            await InWindow(c, async w =>
            {
                w.SetViewport(2.2,100,90); ((ColumnDefinition)w.FindName("LeftColumn")).Width = new GridLength(390);
                w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); AssertFit(w);
                var expander = w.FindName("WorkLogExpander") as Expander;
                Assert(expander != null, "작업 기록 펼치기 없음"); expander!.IsExpanded = true;
                w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); AssertFit(w);
                w.SetViewport(2.2,21,-60); w.MoveImage(1); await w.LastPreviewTask;
                Assert(((ScaleTransform)w.FindName("ImageScale")).ScaleX == 2.2 && ((TranslateTransform)w.FindName("ImageTranslate")).Y == -60,"다음 이동에서 보기 초기화됨");
            });
        });
        await checkAsync("66 숫자 후입력: 실제 PreviewTextInput 커서와 두 자리 구좌", async () =>
        {
            var c = Folder(root, "quota-typing"); SelfTest.AddImage(c.Root,"1가상가.png");
            await InWindow(c, w =>
            {
                var input = (TextBox)w.FindName("CombinedTextBox"); input.Text = "가상인물"; input.CaretIndex = input.Text.Length;
                var composition = new TextComposition(InputManager.Current, input, "3");
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                Assert(input.SelectionStart == 0 && input.SelectionLength == 0,"끝의 숫자를 앞으로 보내지 않음");
                input.SelectedText = "3"; Assert(input.Text == "3가상인물","구좌 앞 배치 실패");
                input.CaretIndex = input.Text.Length; MainWindow.PrepareQuotaInsertion(input); input.SelectedText = "2";
                Assert(input.Text == "32가상인물","두 자리 구좌 누적 실패");
                input.SelectAll(); var length = input.SelectionLength; MainWindow.PrepareQuotaInsertion(input);
                Assert(input.SelectionLength == length && input.SelectionStart == 0,"전체 선택 교체 손상"); return Task.CompletedTask;
            });
        });
        await checkAsync("67 이름 뒤 숫자 붙여넣기 미리보기·실제 리네임", async () =>
        {
            var c=Folder(root,"quota-paste"); SelfTest.AddImage(c.Root,"1가상가.png");
            await InWindow(c,async w=>
            {
                ((TextBox)w.FindName("CombinedTextBox")).Text="가상나12";
                Assert(((TextBlock)w.FindName("NewFileText")).Text=="12가상나.png","미리보기 정규화 실패");
                await w.SaveRenameAsync(false); await w.LastPreviewTask; Assert(File.Exists(Path.Combine(c.Root,"12가상나.png")),"붙여넣기 저장 실패");
            });
            foreach(var invalid in new[]{"가상가0","가상가100","12"}) Reject(()=>SafePaths.ValidName(invalid));
        });
        check("68 행사·보완·일반 폴더 삭제 허용 및 선택하지 않은 사본 제외",()=>
        {
            foreach(var legacy in new[]{true,false})
            {
                var c=Folder(root,"recycle-scope-"+legacy,legacy); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"보완/2가상나.png"); SelfTest.AddImage(c.Root,"jpg/1가상가.png");
                var items=LocalData.Load(c); var plan=RecycleSelection.Prepare(c,items.Append(items[0]));
                Assert(plan.Count==2&&!plan.Any(p=>p.Path.Contains(Path.DirectorySeparatorChar+"jpg"+Path.DirectorySeparatorChar)),"동반 삭제/중복 대상");
                var outDir=Path.Combine(root,"simulated-bin-"+legacy); Directory.CreateDirectory(outDir);
                var result=RecycleSelection.Execute(c,plan,p=>File.Move(p,Path.Combine(outDir,Path.GetFileName(p))));
                Assert(result.Done==2&&result.Errors.Count==0&&File.Exists(Path.Combine(c.Root,"jpg","1가상가.png")),"선택만 이동 규칙 오류");
            }
        });
        check("69 휴지통 처리 외부 수정·범위 밖 파일·읽기 전용 보호",()=>
        {
            var c=Folder(root,"recycle-guard"); var other=Folder(root,"recycle-other"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(other.Root,"1가상나.png");
            var item=LocalData.Load(c).Single(); var plan=RecycleSelection.Prepare(c,new[]{item}); File.AppendAllText(item.FullPath,"changed");
            int calls=0; var result=RecycleSelection.Execute(c,plan,_=>calls++); Assert(calls==0&&result.Errors.Count==1,"외부 수정 파일 이동 시도");
            Reject(()=>RecycleSelection.Prepare(c,LocalData.Load(other)));
            File.SetAttributes(item.FullPath,FileAttributes.ReadOnly);
            try { Reject(()=>RecycleSelection.Prepare(c,LocalData.Load(c))); } finally { File.SetAttributes(item.FullPath,FileAttributes.Normal); }
        });
        check("70 휴지통 취소 시 성공 오표시·후속 파일 처리 방지",()=>
        {
            var c=Folder(root,"recycle-cancel"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png");
            var plan=RecycleSelection.Prepare(c,LocalData.Load(c)); var result=RecycleSelection.Execute(c,plan,_=>throw new OperationCanceledException());
            Assert(result.Cancelled&&result.Done==0&&Directory.GetFiles(c.Root).Length==2,"취소 처리 실패");
        });
        check("71 같은 이름 저장은 파일/메타데이터/저널 무변경",()=>
        {
            var c=Folder(root,"noop-rename",true); SelfTest.AddImage(c.Root,"1가상가.png");
            var item=LocalData.Load(c).Single(); var hash=SafePaths.Hash(item.FullPath); var time=File.GetLastWriteTimeUtc(item.FullPath);
            var target=LocalData.Rename(c,item,"1가상가"); Assert(target==item.FullPath&&!Directory.Exists(c.Metadata)&&hash==SafePaths.Hash(target)&&time==File.GetLastWriteTimeUtc(target),"동일 이름이 기록을 생성함");
        });
        check("72 동일 내용 쓰기 건너뜀 및 읽기 전용 원본 보존",()=>
        {
            var c=Folder(root,"noop-write"); var path=Path.Combine(c.Root,"state.json"); File.WriteAllText(path,"{}"); var time=File.GetLastWriteTimeUtc(path); File.SetAttributes(path,FileAttributes.ReadOnly);
            try { var op=new FileChanges(LocalData.JournalRoot(c),c.Root); op.Write(path,Encoding.UTF8.GetBytes("{}")); Assert(op.Apply("동일 내용")==""&&!Directory.Exists(LocalData.JournalRoot(c))&&time==File.GetLastWriteTimeUtc(path),"중복 쓰기 발생"); }
            finally { File.SetAttributes(path,FileAttributes.Normal); }
        });
        check("73 recovering 기록 실제 파일·백업 일치 후 기록만 종료",()=>
        {
            var c=Folder(root,"recovery-unchanged",true); var journal=Interrupted(c); var roots=new[]{c.EventRoot!};
            var files=Directory.GetFiles(c.Metadata,"*",SearchOption.AllDirectories).Where(p=>p!=journal).ToDictionary(p=>p,SafePaths.Hash);
            var oldJournal=SafePaths.Hash(journal); var reviews=FileChanges.ReviewPending(LocalData.JournalRoot(c),roots);
            Assert(reviews.Single().CanResolve&&oldJournal==SafePaths.Hash(journal),"읽기 점검이 기록을 수정함");
            FileChanges.ResolveUnchangedPending(reviews.Single(),LocalData.JournalRoot(c),roots); FileChanges.CheckPending(LocalData.JournalRoot(c));
            Assert(files.All(p=>SafePaths.Hash(p.Key)==p.Value)&&File.Exists(journal),"실제 파일 또는 백업 변경/기록 삭제");
        });
        check("74 중단 기록의 변경 파일·손상 백업은 강제 완료 금지",()=>
        {
            foreach(var corruptBackup in new[]{false,true})
            {
                var c=Folder(root,"recovery-block-"+corruptBackup); var journal=Interrupted(c); var roots=new[]{c.Root}; var sha=SafePaths.Hash(journal);
                var path=corruptBackup?Path.Combine(Path.GetDirectoryName(journal)!,"0000.bak"):Path.Combine(c.Metadata,"account-types.json"); File.AppendAllText(path,"external");
                var review=FileChanges.ReviewPending(LocalData.JournalRoot(c),roots).Single(); Assert(!review.CanResolve,"손상/변경 기록 종료 허용"); Reject(()=>FileChanges.ResolveUnchangedPending(review,LocalData.JournalRoot(c),roots)); Assert(sha==SafePaths.Hash(journal),"실패 시 기록 변경");
            }
        });
        check("75 검증 후 기록 변경 및 다른 폴더 접근 차단",()=>
        {
            var c=Folder(root,"recovery-stale"); var journal=Interrupted(c); var roots=new[]{c.Root};
            Assert(!FileChanges.ReviewPending(LocalData.JournalRoot(c),new[]{root}).Single().CanResolve,"범위 밖 복구 승인");
            var review=FileChanges.ReviewPending(LocalData.JournalRoot(c),roots).Single(); File.AppendAllText(journal," ");
            Reject(()=>FileChanges.ResolveUnchangedPending(review,LocalData.JournalRoot(c),roots));
        });
        check("76 미해결 기록이 있으면 휴지통 이동도 차단",()=>
        {
            var c=Folder(root,"recycle-pending"); SelfTest.AddImage(c.Root,"1가상가.png"); Interrupted(c);
            Reject(()=>RecycleSelection.Prepare(c,LocalData.Load(c))); Assert(File.Exists(Path.Combine(c.Root,"1가상가.png")),"중단 기록 무시");
        });
        await checkAsync("77 Windows 휴지통 실제 호출: 합성 이미지 1개만",async()=>
        {
            var c=Folder(root,"windows-recycle",true); SelfTest.AddImage(c.Root,"1SYNTHETIC_RECYCLE_TEST.png"); SelfTest.AddImage(c.Root,"2KEEP_TEST.png");
            var keep=Path.Combine(c.Root,"2KEEP_TEST.png"); var hash=SafePaths.Hash(keep);
            var item=LocalData.Load(c).Single(i=>i.Quota=="1"); var plan=RecycleSelection.Prepare(c,new[]{item});
            var result=await RecycleSelection.RunAsync(c,plan,IntPtr.Zero);
            Assert(result.Done==1&&!result.Cancelled&&result.Errors.Count==0&&!File.Exists(item.FullPath),"네이티브 휴지통 오류: "+string.Join(";",result.Errors));
            Assert(SafePaths.Hash(keep)==hash,"선택하지 않은 파일 변경");
        });
    }
}

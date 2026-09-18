using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
namespace BoramRms.Lite;

public static class LiteWorkflowTests
{
    private sealed record Result(string Name, bool Passed, string Detail);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("실패해야 하는 작업이 실행되었습니다."); }
    private static FolderContext Folder(string root, string name, bool old = false)
    {
        var dir = Path.Combine(root, name); Directory.CreateDirectory(old ? Path.Combine(dir,"신청서","[원본]") : dir);
        return FolderContext.Resolve(dir);
    }
    private static string OldPending(FolderContext c)
    {
        var dir=Path.Combine(c.Metadata,"lite-journal","synthetic-old-pending"); Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"journal.json"); File.WriteAllText(path,"{\"Description\":\"리네임\",\"State\":\"recovering\",\"Files\":[]}"); return path;
    }
    private static async Task Window(FolderContext c, Func<MainWindow,Task> action)
    {
        var w=new MainWindow(testMode:true) { Left=-16000,Top=-16000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual };
        w.Show(); try { await w.OpenFolderAsync(c.Root); await w.LastPreviewTask; w.UpdateLayout(); await action(w); }
        finally { await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static void CheckBox(MainWindow w,string name,bool value)
    {
        var box=(CheckBox)w.FindName(name); box.IsChecked=value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var run=Path.Combine(output,"run_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")); Directory.CreateDirectory(run);
        var previous=SettingsStore.DirectoryPath; SettingsStore.DirectoryPath=Path.Combine(run,"isolated-settings");
        var results=new List<Result>();
        void Save() { File.WriteAllText(Path.Combine(run,"test-results.json"),JsonSerializer.Serialize(results,SettingsStore.Json)); File.WriteAllText(Path.Combine(output,"latest-run.txt"),run); }
        void Check(string name,Action action) { try { action(); results.Add(new(name,true,"통과")); } catch(Exception ex) { results.Add(new(name,false,ex.ToString())); } Save(); }
        async Task CheckAsync(string name,Func<Task> action) { try { await action(); results.Add(new(name,true,"통과")); } catch(Exception ex) { results.Add(new(name,false,ex.ToString())); } Save(); }
        try
        {
            Check("W01 과거 recovering 기록 보존 + 상태 저장 성공",()=>
            {
                var c=Folder(run,"pending",true); SelfTest.AddImage(c.Root,"1가상가.png"); var journal=OldPending(c); var before=SafePaths.Hash(journal);
                var item=LiteWorkspace.Load(c).Single(); var imageHash=SafePaths.Hash(item.FullPath);
                LiteWorkspace.Save(c,item,null,new("보완","주소보완",true)); var after=LiteWorkspace.Load(c).Single();
                Assert(after.Status=="보완"&&after.Card&&after.FullPath==item.FullPath,"상태 저장 또는 위치 유지 실패");
                Assert(SafePaths.Hash(journal)==before&&SafePaths.Hash(item.FullPath)==imageHash,"이전 기록 또는 원본 변경");
            });
            Check("W02 리네임+상태: 현재 파일만 변경, TXT·가공본·옛 메타데이터 보존",()=>
            {
                var c=Folder(run,"isolated",true); SelfTest.AddImage(c.Root,"카드/1가상가.png"); SelfTest.AddImage(c.ProcessedRoot!,"1구좌/1가상가.png");
                Directory.CreateDirectory(c.Metadata); File.WriteAllText(c.TextPath!,"KEEP LEGACY TXT"); File.WriteAllText(Path.Combine(c.Metadata,"account-types.json"),"{\"CardPaths\":[\"카드\\\\1가상가.png\"]}");
                var sentinel=new[]{c.TextPath!,Path.Combine(c.ProcessedRoot!,"1구좌","1가상가.png"),Path.Combine(c.Metadata,"account-types.json")}.ToDictionary(p=>p,SafePaths.Hash);
                var item=LiteWorkspace.Load(c).Single(); var hash=SafePaths.Hash(item.FullPath); var save=LiteWorkspace.Save(c,item,"2가상나",new("입력전 취소","메모",false));
                Assert(Path.GetDirectoryName(save.Path)==Path.GetDirectoryName(item.FullPath)&&SafePaths.Hash(save.Path)==hash,"리네임이 폴더 또는 이미지 내용 변경");
                Assert(sentinel.All(p=>SafePaths.Hash(p.Key)==p.Value)&&LiteWorkspace.Load(c).Single().Status=="입력전 취소","기존 연동이 남음");
            });
            Check("W03 손상된 과거 journal.json도 신규 작업을 차단하지 않음",()=>
            {
                var c=Folder(run,"broken-old"); SelfTest.AddImage(c.Root,"신청서.png"); var path=OldPending(c); File.WriteAllText(path,"NOT JSON");
                LiteWorkspace.Save(c,LiteWorkspace.Load(c).Single(),null,new("추가","숫자 없는 파일도 상태 저장",false));
                Assert(LiteWorkspace.Load(c).Single().Status=="추가"&&File.ReadAllText(path)=="NOT JSON","레거시 기록 의존");
            });
            Check("W04 반복 상태 저장·해제와 이전 기록 백업",()=>
            {
                var c=Folder(run,"repeat"); SelfTest.AddImage(c.Root,"카드/1가상가.png");
                foreach(var state in new[]{new LiteState("보완","주소",true),new LiteState("추가","메모",false),new LiteState("","",false)})
                { LiteWorkspace.Save(c,LiteWorkspace.Load(c).Single(),null,state); Assert(LiteState.From(LiteWorkspace.Load(c).Single())==state,"반복 저장 실패"); }
                Assert(File.Exists(LiteWorkspace.StatePath(LiteWorkspace.Load(c).Single().FullPath)+".previous"),"이전 상태 백업 없음");
            });
            Check("W05 오래된 상태 스냅샷은 덮어쓰지 않음",()=>
            {
                var c=Folder(run,"stale"); SelfTest.AddImage(c.Root,"1가상가.png"); var old=LiteWorkspace.Load(c).Single();
                LiteWorkspace.Save(c,old,null,new("보완","주소",false)); Reject(()=>LiteWorkspace.Save(c,old,null,new("추가","",false)));
                Assert(LiteWorkspace.Load(c).Single().Status=="보완","동시 상태 덮어쓰기");
            });
            Check("W06 손상된 새 상태는 해당 이미지만 안내, 다른 이미지 저장 정상",()=>
            {
                var c=Folder(run,"corrupt"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png");
                var a=LiteWorkspace.Load(c).First(); Directory.CreateDirectory(LiteWorkspace.StoreDirectory(a.FullPath)); File.WriteAllText(LiteWorkspace.StatePath(a.FullPath),"BROKEN-STATE");
                var items=LiteWorkspace.Load(c); Assert(items[0].StateNotice.Length>0,"손상 상태 안내 누락");
                LiteWorkspace.Save(c,items[1],null,new("추가","",false)); LiteWorkspace.Save(c,items[0],null,new("보완","확인",true));
                Assert(File.ReadAllText(LiteWorkspace.StatePath(a.FullPath)+".previous")=="BROKEN-STATE"&&LiteWorkspace.Load(c).First().Status=="보완","손상 원본 미보존 또는 새 저장 실패");
            });
            Check("W07 파일명 충돌: 양쪽 이미지 보존",()=>
            {
                var c=Folder(run,"collision"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png");
                var items=LiteWorkspace.Load(c); var hashes=items.ToDictionary(i=>i.FullPath,i=>SafePaths.Hash(i.FullPath));
                Reject(()=>LiteWorkspace.Save(c,items[0],"2가상나",new("추가","",false))); Assert(hashes.All(p=>SafePaths.Hash(p.Key)==p.Value),"충돌 덮어쓰기");
            });
            Check("W08 외부 이미지 변경과 읽기 전용은 해당 저장만 취소",()=>
            {
                var c=Folder(run,"image-guard"); SelfTest.AddImage(c.Root,"1가상가.png"); var item=LiteWorkspace.Load(c).Single(); File.AppendAllText(item.FullPath,"external");
                Reject(()=>LiteWorkspace.Save(c,item,"2가상가",LiteState.From(item)));
                item=LiteWorkspace.Load(c).Single(); File.SetAttributes(item.FullPath,FileAttributes.ReadOnly);
                try { Reject(()=>LiteWorkspace.Save(c,item,"2가상가",LiteState.From(item))); } finally { File.SetAttributes(item.FullPath,FileAttributes.Normal); }
            });
            Check("W09 이름·상태 한 단계 되돌리기",()=>
            {
                var c=Folder(run,"undo"); SelfTest.AddImage(c.Root,"1가상가.png"); var item=LiteWorkspace.Load(c).Single(); var h=SafePaths.Hash(item.FullPath);
                var result=LiteWorkspace.Save(c,item,"3가상다",new("보완","메모",true)); var path=LiteWorkspace.Undo(c,result.Edit!);
                Assert(path==item.FullPath&&SafePaths.Hash(path)==h&&LiteState.From(LiteWorkspace.Load(c).Single())==LiteState.From(item),"한 단계 복구 실패");
            });
            Check("W10 이후 변경을 되돌리기로 덮어쓰지 않음",()=>
            {
                var c=Folder(run,"undo-guard"); SelfTest.AddImage(c.Root,"1가상가.png"); var r=LiteWorkspace.Save(c,LiteWorkspace.Load(c).Single(),"2가상나",new("추가","",false));
                File.AppendAllText(r.Path,"external"); Reject(()=>LiteWorkspace.Undo(c,r.Edit!)); Assert(File.ReadAllBytes(r.Path).Length>0,"외부 파일 삭제");
            });
            Check("W11 중단 리네임: 이동 전·후 상태를 파일 존재/해시로 구분",()=>
            {
                var c=Folder(run,"interrupted-rename"); SelfTest.AddImage(c.Root,"1가상가.png"); var old=LiteWorkspace.Load(c).Single(); var target=Path.Combine(c.Root,"2가상나.png");
                Directory.CreateDirectory(LiteWorkspace.StoreDirectory(target)); File.WriteAllText(LiteWorkspace.StatePath(target),JsonSerializer.Serialize(new LiteRecord{FileName=Path.GetFileName(target),RenameFrom=old.FileName,ImageHash=SafePaths.Hash(old.FullPath),Status="추가",Card=true},SettingsStore.Json));
                Assert(LiteWorkspace.Load(c).Single().FullPath==old.FullPath,"아직 이동하지 않은 이미지 변경"); File.Move(old.FullPath,target);
                var loaded=LiteWorkspace.Load(c).Single(); Assert(loaded.Status=="추가"&&loaded.Card&&loaded.StateNotice.Length==0,"중단 후 상태 연결 실패");
            });
            Check("W12 변경 없는 저장은 기록을 만들지 않음",()=>
            {
                var c=Folder(run,"noop"); SelfTest.AddImage(c.Root,"1가상가.png"); var item=LiteWorkspace.Load(c).Single();
                Assert(LiteWorkspace.Save(c,item,null,LiteState.From(item)).Edit==null&&!Directory.Exists(LiteWorkspace.StoreDirectory(item.FullPath)),"불필요한 저장");
            });
            Check("W13 회전·복구는 새 백업만 사용, 옛 저널 무변경",()=>
            {
                var c=Folder(run,"rotate"); SelfTest.AddImage(c.Root,"1가상가.png",800,1120); var journal=OldPending(c); var old=LiteWorkspace.Load(c).Single(); var hash=SafePaths.Hash(old.FullPath);
                var edit=LiteWorkspace.Rotate(c,old,true); Assert(ImageProcessing.Load(old.FullPath).PixelWidth==1120,"회전 실패"); LiteWorkspace.Undo(c,edit);
                Assert(SafePaths.Hash(old.FullPath)==hash&&File.ReadAllText(journal).Contains("recovering"),"회전 복원 또는 옛 기록 보존 실패");
            });
            await CheckAsync("W14 과거 미완료 기록이 있어도 체크만으로 카드 상태 즉시 반영",async()=>
            {
                var c=Folder(run,"ui-state",true); SelfTest.AddImage(c.Root,"1가상가.png"); var journal=OldPending(c); var h=SafePaths.Hash(journal);
                await Window(c,async w=> { CheckBox(w,"CardCheck",true); await w.LastAutoSaveTask; await w.LastPreviewTask;
                    Assert(w.AllItems.Single().Card&&!w.HasPendingStatus&&SafePaths.Hash(journal)==h,"상태 버튼 저장 실패"); });
            });
            await CheckAsync("W15 이름+상태 수정 후 저장하고 다음: 하나의 사용자 동작",async()=>
            {
                var c=Folder(run,"ui-combined"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png"); OldPending(c);
                await Window(c,async w=> { var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                    ((TextBox)w.FindName("CombinedTextBox")).Text="가상다3"; CheckBox(w,"CardCheck",true);
                    ((Button)w.FindName("SaveNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastSaveTask; await w.LastPreviewTask;
                    Assert(LiteWorkspace.Load(c).Single(i=>i.Quota=="3").Card&&((ImageItem)list.SelectedItem).Name=="가상나"&&!w.HasPendingRename&&!w.HasPendingStatus,"통합 저장/다음 실패"); });
            });
            await CheckAsync("W16 Space: 상태만 수정해도 저장하고 다음",async()=>
            {
                var c=Folder(run,"ui-space"); SelfTest.AddImage(c.Root,"1가상가.png"); SelfTest.AddImage(c.Root,"2가상나.png");
                await Window(c,async w=> { CheckBox(w,"CardCheck",true); await w.AdvanceWithSpaceAsync(); await w.LastPreviewTask; Assert(((ListBox)w.FindName("ImageList")).SelectedIndex==1&&LiteWorkspace.Load(c).First().Card,"상태 Space 차단"); });
            });
            await CheckAsync("W17 실패한 저장은 이름·상태 입력과 현재 선택을 보존",async()=>
            {
                var c=Folder(run,"ui-failure"); SelfTest.AddImage(c.Root,"1가상가.png");
                await Window(c,async w=> { ((TextBox)w.FindName("CombinedTextBox")).Text="잘못된이름"; CheckBox(w,"CardCheck",true); await w.SaveDraftAsync(true);
                    Assert(w.HasPendingRename&&!w.HasPendingStatus&&LiteWorkspace.Load(c).Single().Card&&((TextBox)w.FindName("CombinedTextBox")).Text=="잘못된이름"&&((FrameworkElement)w.FindName("WorkArea")).IsEnabled,"이름 저장 실패 시 입력 손실 또는 독립 체크 저장 실패"); });
            });
            await CheckAsync("W18 새 창에서 상태 복원, 과거 되돌리기와 복구 버튼 없음",async()=>
            {
                var c=Folder(run,"ui-reopen"); SelfTest.AddImage(c.Root,"1가상가.png"); LiteWorkspace.Save(c,LiteWorkspace.Load(c).Single(),null,new("추가","메모",true)); OldPending(c);
                await Window(c,async w=> { Assert(w.AllItems.Single().Card&&w.FindName("RecoveryReviewButton")==null,"상태 복원/복구 UI 정리"); await w.UndoDraftAsync(); Assert(w.AllItems.Single().Status=="추가","이전 실행 변경을 잘못 복구"); });
            });
            Check("W19 오래된 저널과 무관한 선택 삭제 계획, 실제 파일은 확인 전 보존",()=>
            {
                var c=Folder(run,"delete-old",true); SelfTest.AddImage(c.Root,"1가상가.png"); OldPending(c); var item=LiteWorkspace.Load(c).Single();
                Assert(RecycleSelection.Prepare(c,new[]{item}).Count==1&&File.Exists(item.FullPath),"삭제 폴더 제한 또는 확인 전 삭제");
            });
            Check("W20 과거 저널이 남아 있어도 직접 압축 실행 경로 진입",()=>
            {
                var c=Folder(run,"compress-old"); SelfTest.AddImage(c.Root,"1가상가.png"); var j=OldPending(c); var h=SafePaths.Hash(j);
                var r=InPlaceCompression.Run(c,LiteWorkspace.Load(c),null,CancellationToken.None); Assert(r.Entries.Count==1&&r.Entries[0].Error.Length==0&&SafePaths.Hash(j)==h,"압축의 옛 기록 차단");
            });
            await AutoStatusTests.RunAsync(run, Check, CheckAsync);
            await BlankNameTests.RunAsync(run, CheckAsync);
            // Existing image/keyboard/branding/updater checks remain relevant.
            UpdateTests.Run(run,Check); await UiRevisionTests.RunAsync(run,Check,CheckAsync); await BrandInputTests.RunAsync(run,Check,CheckAsync);
            // The unreleased 0.3.2 manual journal-recovery checks are intentionally not part of 0.4's workflow.
            bool Active(string name) => int.TryParse(name[..2],out var n)&&(n is >=64 and <=70||n==77);
            await FunctionalFixTests.RunAsync(run,(n,a)=>{if(Active(n)) Check(n,a);},(n,a)=>Active(n)?CheckAsync(n,a):Task.CompletedTask);
        }
        catch(Exception ex) { results.Add(new("실행기",false,ex.ToString())); }
        finally { SettingsStore.DirectoryPath=previous; Save(); }
        var failures=results.Count(r=>!r.Passed);
        File.WriteAllText(Path.Combine(run,"SUMMARY.txt"),$"PASS {results.Count-failures}\nFAIL {failures}\n"+string.Join("\n",results.Select(r=>$"{(r.Passed?"PASS":"FAIL")} {r.Name}: {r.Detail}")));
        return failures==0?0:1;
    }
}

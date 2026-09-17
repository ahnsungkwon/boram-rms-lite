using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public static class UiRevisionTests
{
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { var rejected=false; try { action(); } catch { rejected=true; } Assert(rejected,"안전하지 않은 작업이 허용됨"); }
    private sealed class ProgressNow<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static FolderContext Folder(string root,string name)
    { var path=Path.Combine(root,name); Directory.CreateDirectory(path); return FolderContext.Resolve(path); }
    private static async Task InWindow(FolderContext ctx,Func<MainWindow,Task> test)
    {
        var w=new MainWindow(testMode:true) { Left=-16000,Top=-16000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual };
        w.Show();
        try { await w.OpenFolderAsync(ctx.Root); await w.LastPreviewTask; w.UpdateLayout(); await test(w); }
        finally { await w.ReloadAsync(); await w.LastPreviewTask; w.Close(); }
    }
    private static async Task Space(MainWindow window,UIElement source)
    {
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Space) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        Assert(args.Handled,"앱 내부 Space 이벤트가 처리되지 않음");
        await window.LastShortcutTask; await window.LastPreviewTask;
    }
    private static void Noise(string path,bool alpha=false)
    {
        const int width=1200,height=1800;
        var channels=alpha?4:3; var data=new byte[width*height*channels]; new Random(283).NextBytes(data);
        if(alpha) for(var i=3;i<data.Length;i+=4) data[i]=128;
        var image=BitmapSource.Create(width,height,96,96,alpha?PixelFormats.Bgra32:PixelFormats.Bgr24,null,data,width*channels); image.Freeze();
        File.WriteAllBytes(path,ImageProcessing.Encode(image,Path.GetExtension(path),98));
    }
    public static async Task RunAsync(string run,Action<string,Action> check,Func<string,Func<Task>,Task> checkAsync)
    {
        var root=Path.Combine(run,"ui-revision"); Directory.CreateDirectory(root);
        await checkAsync("45 이미지 영역 Space 실제 이벤트: 변경 없이 다음",async()=>
        {
            var ctx=Folder(root,"space-view"); SelfTest.AddImage(ctx.Root,"1가상가.png"); SelfTest.AddImage(ctx.Root,"2가상나.png");
            var before=Directory.GetFiles(ctx.Root).ToDictionary(p=>p,SafePaths.Hash);
            await InWindow(ctx,async w=>
            {
                var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                await Space(w,(UIElement)w.FindName("ImageStage")); Assert(list.SelectedIndex==1,"Space 다음 이미지 이동 실패");
                Assert(before.All(p=>SafePaths.Hash(p.Key)==p.Value)&&Directory.GetFiles(ctx.Root,"*",SearchOption.AllDirectories).Length==before.Count,"단순 탐색이 파일을 변경함");
            });
        });
        await checkAsync("46 이름 입력 Space 이벤트: 한글 파일명 저장+다음",async()=>
        {
            var ctx=Folder(root,"space-rename"); SelfTest.AddImage(ctx.Root,"1가상가.png"); SelfTest.AddImage(ctx.Root,"2가상나.png");
            await InWindow(ctx,async w=>
            {
                var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                var input=(TextBox)w.FindName("CombinedTextBox"); input.Text="3가상다"; await Space(w,input);
                Assert(File.Exists(Path.Combine(ctx.Root,"3가상다.png")),"한글 입력 저장 실패");
                Assert(((ImageItem)list.SelectedItem).Name=="가상나","저장 후 다음 선택 실패");
            });
        });
        await checkAsync("47 검색·메모 공백 및 체크박스 키 조작 보호",async()=>
        {
            var ctx=Folder(root,"input-scope"); SelfTest.AddImage(ctx.Root,"1가상가.png");
            await InWindow(ctx,w=>
            {
                foreach(var name in new[]{"SearchText","RepairMemo","StatusMemo","CardCheck","StatusFilter"}) Assert(!w.CanHandleSpace((DependencyObject)w.FindName(name)),"다른 입력의 Space를 가로챔: "+name);
                Assert(w.CanHandleSpace((DependencyObject)w.FindName("CombinedTextBox")),"이름 입력 Space 누락"); return Task.CompletedTask;
            });
        });
        await checkAsync("48 다음·이전·해상도 변경 시 확대율/위치/종이 너비 유지",async()=>
        {
            var ctx=Folder(root,"viewport"); SelfTest.AddImage(ctx.Root,"1가상가.png",800,1120); SelfTest.AddImage(ctx.Root,"2가상나.png",1600,2100);
            await InWindow(ctx,async w=>
            {
                var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                w.SetViewport(2.25,73,-181); var width=((Image)w.FindName("PreviewImage")).Width;
                w.MoveImage(1); await w.LastPreviewTask;
                var scale=(ScaleTransform)w.FindName("ImageScale"); var offset=(TranslateTransform)w.FindName("ImageTranslate");
                Assert(scale.ScaleX==2.25&&offset.X==73&&offset.Y==-181,"다음 이동 시 카메라 초기화");
                Assert(((Image)w.FindName("PreviewImage")).Width==width,"다른 해상도 이미지가 자동 맞춤됨");
                w.MoveImage(-1); await w.LastPreviewTask; Assert(scale.ScaleX==2.25&&offset.Y==-181,"이전 이동 시 초기화");
            });
        });
        await checkAsync("49 좌우 클릭 영역 이벤트와 경계 이동 방지",async()=>
        {
            var ctx=Folder(root,"rails"); SelfTest.AddImage(ctx.Root,"1가상가.png"); SelfTest.AddImage(ctx.Root,"2가상나.png");
            await InWindow(ctx,async w=>
            {
                var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask;
                var prev=(Button)w.FindName("PreviousImageRail"); var next=(Button)w.FindName("NextImageRail");
                Assert(!prev.IsEnabled&&next.IsEnabled,"첫 이미지 버튼 상태"); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastPreviewTask;
                Assert(list.SelectedIndex==1&&!next.IsEnabled&&prev.IsEnabled,"다음 영역 클릭 실패"); prev.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await w.LastPreviewTask; Assert(list.SelectedIndex==0,"이전 영역 클릭 실패");
            });
        });
        await checkAsync("50 연속 Space 중복 저장·두 장 건너뜀 방지",async()=>
        {
            var ctx=Folder(root,"repeat-space"); for(var n=1;n<=3;n++) SelfTest.AddImage(ctx.Root,n+"가상자료.png");
            await InWindow(ctx,async w=> { var list=(ListBox)w.FindName("ImageList"); list.SelectedIndex=0; await w.LastPreviewTask; await Task.WhenAll(w.AdvanceWithSpaceAsync(),w.AdvanceWithSpaceAsync()); await w.LastPreviewTask; Assert(list.SelectedIndex==1,"중복 키 요청으로 두 장 이동"); });
        });
        await checkAsync("51 탭별 확대·위치 보존",async()=>
        {
            var a=Folder(root,"camera-a"); var b=Folder(root,"camera-b"); SelfTest.AddImage(a.Root,"1가상가.png"); SelfTest.AddImage(b.Root,"2가상나.png");
            await InWindow(a,async w=>
            { w.SetViewport(3,42,-84); await w.OpenFolderAsync(b.Root); await w.LastPreviewTask; w.SetViewport(1.5,-20,30); await w.OpenFolderAsync(a.Root); await w.LastPreviewTask; Assert(((ScaleTransform)w.FindName("ImageScale")).ScaleX==3&&((TranslateTransform)w.FindName("ImageTranslate")).Y==-84,"탭별 카메라 혼합"); });
        });
        check("52 JPEG 직접 압축: 300000바이트 미만·동일 경로·백업 없음",()=>
        {
            var c=Folder(root,"inplace-jpeg"); var p=Path.Combine(c.Root,"1가상가.jpg"); Noise(p); var before=SafePaths.Hash(p);
            var result=InPlaceCompression.CompressOne(c,LocalData.Load(c).Single(),CancellationToken.None);
            Assert(result.Changed&&new FileInfo(p).Length<300000&&before!=SafePaths.Hash(p),"원본 직접 압축 실패");
            Assert(Directory.GetFiles(c.Root,"*",SearchOption.AllDirectories).Length==1&&Directory.GetDirectories(c.Root).Length==0,"불필요한 사본/백업 생성");
            Assert(ImageProcessing.Load(p).PixelWidth>0,"유효하지 않은 JPEG");
        });
        check("53 PNG 직접 압축: PNG 형식·투명도 유지",()=>
        {
            var c=Folder(root,"inplace-png"); var p=Path.Combine(c.Root,"1가상가.png"); Noise(p,true);
            var result=InPlaceCompression.CompressOne(c,LocalData.Load(c).Single(),CancellationToken.None); var bytes=File.ReadAllBytes(p);
            Assert(result.Changed&&bytes.Length<300000&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"PNG 형식/용량 오류");
            var image=new FormatConvertedBitmap(ImageProcessing.Load(p),PixelFormats.Bgra32,null,0); var pixel=new byte[4]; image.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0); Assert(pixel[3]<255,"투명도 소실");
        });
        check("54 이미 작은 원본의 해시·수정시각 보존",()=>
        { var c=Folder(root,"inplace-small"); SelfTest.AddImage(c.Root,"1가상가.png"); var item=LocalData.Load(c).Single(); var sha=SafePaths.Hash(item.FullPath); var result=InPlaceCompression.CompressOne(c,item,CancellationToken.None); Assert(result.Skipped&&!result.Changed&&sha==SafePaths.Hash(item.FullPath)&&new FileInfo(item.FullPath).LastWriteTimeUtc.Ticks==item.ModifiedTicks,"작은 파일 재압축"); });
        check("55 손상 파일·외부 수정 파일의 원본 유지",()=>
        { var c=Folder(root,"inplace-invalid"); var p=Path.Combine(c.Root,"1손상.png"); File.WriteAllBytes(p,new byte[350000]); var sha=SafePaths.Hash(p); Reject(()=>InPlaceCompression.CompressOne(c,LocalData.Load(c).Single(),CancellationToken.None)); Assert(SafePaths.Hash(p)==sha,"손상파일 덮어씀"); SelfTest.AddImage(c.Root,"2가상나.png"); var item=LocalData.Load(c).Single(i=>i.Quota=="2"); File.AppendAllText(item.FullPath,"external"); sha=SafePaths.Hash(item.FullPath); Reject(()=>InPlaceCompression.CompressOne(c,item,CancellationToken.None)); Assert(sha==SafePaths.Hash(item.FullPath),"외부 수정 덮어씀"); });
        check("56 취소 이후 미처리 원본과 파일 개수 유지",()=>
        {
            var c=Folder(root,"inplace-cancel"); Noise(Path.Combine(c.Root,"1가상가.jpg")); Noise(Path.Combine(c.Root,"2가상나.jpg")); var items=LocalData.Load(c); var untouched=SafePaths.Hash(items[1].FullPath);
            using var token=new CancellationTokenSource(); var progress=new ProgressNow<CompressionEntry>(_=>token.Cancel()); var result=InPlaceCompression.Run(c,items,progress,token.Token);
            Assert(result.Cancelled&&result.Entries.Count==1&&result.Entries[0].Changed,"취소 후 추가 처리"); Assert(SafePaths.Hash(items[1].FullPath)==untouched&&Directory.GetFiles(c.Root).Length==2,"미처리 파일 변경");
        });
        check("57 읽기 전용 원본 보호",()=>
        {
            var c=Folder(root,"inplace-readonly"); SelfTest.AddImage(c.Root,"1가상가.png"); var item=LocalData.Load(c).Single(); var sha=SafePaths.Hash(item.FullPath); File.SetAttributes(item.FullPath,FileAttributes.ReadOnly);
            try { Reject(()=>InPlaceCompression.CompressOne(c,item,CancellationToken.None)); Assert(SafePaths.Hash(item.FullPath)==sha,"읽기 전용 변경"); } finally { File.SetAttributes(item.FullPath,FileAttributes.Normal); }
        });
        await checkAsync("58 제거 기능·단일 압축 버튼·좁은 창 렌더링",async()=>
        {
            var c=Folder(root,"ui-final"); for(var i=1;i<=10;i++) SelfTest.AddImage(c.Root,(i%5+1)+"가상자료"+i.ToString("00")+".png");
            await InWindow(c,async w=>
            {
                Assert(w.FindName("SupplementTarget")==null&&w.FindName("StopTraceButton")==null&&w.FindName("CompressOriginalsButton") is Button,"요청 삭제/통합 누락");
                w.Width=1440; w.Height=980; w.UpdateLayout(); await w.LastPreviewTask; SelfTest.Render(w,Path.Combine(run,"main-preview.png"),1424,941);
                w.Width=1100; w.Height=760; w.UpdateLayout(); SelfTest.Render(w,Path.Combine(run,"compact-preview.png"),1084,721);
                Assert(((FrameworkElement)w.FindName("ImageStage")).ActualWidth>180,"작은 창에서 중앙 영역 붕괴");
            });
        });
    }
}

using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BoramRms.Lite;

public static class PreviewPerformanceTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static BitmapSource Pixels()
    {
        var source = BitmapSource.Create(64, 96, 96, 96, PixelFormats.Bgr24, null,
            new byte[64 * 96 * 3], 64 * 3);
        source.Freeze();
        return source;
    }

    public static async Task RunAsync(string root, Action<string, Action> check,
        Func<string, Func<Task>, Task> checkAsync)
    {
        var directory = Path.Combine(root, "preview-performance");
        Directory.CreateDirectory(directory);
        string Fixture(string name)
        {
            var path = Path.Combine(directory, name + ".synthetic");
            File.WriteAllText(path, "synthetic-preview-fixture");
            return path;
        }

        await checkAsync("PF01 24회 요청은 4회 디코딩만 수행 + 동시 디코딩 2개 제한", async () =>
        {
            var paths = Enumerable.Range(0, 4).Select(i => Fixture("shared-" + i)).ToArray();
            var decoded = 0; var active = 0; var peak = 0;
            var loader = new PreviewImageLoaderService((_, _) =>
            {
                Interlocked.Increment(ref decoded);
                var current = Interlocked.Increment(ref active);
                int previous;
                do { previous = Volatile.Read(ref peak); }
                while (previous < current && Interlocked.CompareExchange(ref peak, current, previous) != previous);
                try { return Pixels(); }
                finally { Interlocked.Decrement(ref active); }
            });
            var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => loader.LoadAsync(paths[i % 4])));
            Assert(decoded == 4 && peak <= 2 && results.All(s => s.IsFrozen), "디코딩 공유/동시 작업 제한 실패");
            for (var i = 0; i < 80; i++) await loader.LoadAsync(paths[i % 4]);
            Assert(decoded == 4, "앞뒤 전환에서 캐시 이미지 재디코딩");
            foreach (var group in Enumerable.Range(0, 4))
                Assert(ReferenceEquals(results[group], results[group + 4]), "동일 경로 픽셀 공유 실패");
        });

        await checkAsync("PF02 파일명 변경은 픽셀 재사용, 회전·수정·초기화는 다시 로드", async () =>
        {
            var path = Fixture("changes"); var moved = Path.Combine(directory, "renamed.synthetic");
            var decoded = 0;
            var loader = new PreviewImageLoaderService((_, _) => { decoded++; return Pixels(); });
            var initial = await loader.LoadAsync(path);
            File.Move(path, moved); loader.Transfer(path, moved);
            Assert(ReferenceEquals(initial, await loader.LoadAsync(moved)) && decoded == 1, "리네임 캐시 전이 실패");
            loader.Invalidate(moved); await loader.LoadAsync(moved);
            Assert(decoded == 2, "같은 경로 회전 무효화 실패");
            File.SetLastWriteTimeUtc(moved, File.GetLastWriteTimeUtc(moved).AddSeconds(2)); await loader.LoadAsync(moved);
            Assert(decoded == 3, "수정시각 변경 감지 실패");
            var time = File.GetLastWriteTimeUtc(moved); File.AppendAllText(moved, "changed"); File.SetLastWriteTimeUtc(moved, time);
            await loader.LoadAsync(moved); Assert(decoded == 4, "수정시각이 같은 파일 길이 변경 감지 실패");
            loader.Clear(); await loader.LoadAsync(moved); Assert(decoded == 5, "종료/초기화 이후 캐시 잔류");
        });

        await checkAsync("PF03 캐시 개수·메모리 예산 상한 준수", async () =>
        {
            var a = Fixture("bound-a"); var b = Fixture("bound-b"); var decoded = 0;
            var entries = new PreviewImageLoaderService((_, _) => { decoded++; return Pixels(); }, maximumEntries: 1);
            await entries.LoadAsync(a); await entries.LoadAsync(b); await entries.LoadAsync(a);
            Assert(decoded == 3, "LRU 개수 제한 실패");
            var bytes = new PreviewImageLoaderService((_, _) => { decoded++; return Pixels(); }, maximumBytes: 1024);
            await bytes.LoadAsync(a); await bytes.LoadAsync(a);
            Assert(decoded == 5, "단일 이미지가 캐시 바이트 예산 초과");
        });

        await checkAsync("PF04 선택 취소가 다른 공유 디코딩 요청을 취소하지 않음", async () =>
        {
            var path = Fixture("waiter"); using var started = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
            var decoded = 0;
            var loader = new PreviewImageLoaderService((_, _) =>
            {
                Interlocked.Increment(ref decoded); started.Set();
                if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("공유 요청 테스트 대기 초과");
                return Pixels();
            });
            var cancelled = loader.LoadAsync(path, cancellation.Token);
            Assert(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "디코딩 미시작");
            var survivor = loader.LoadAsync(path); cancellation.Cancel(); proceed.Set();
            try { await cancelled; throw new InvalidOperationException("취소된 선택 완료됨"); }
            catch (OperationCanceledException) { }
            await survivor; Assert(decoded == 1, "다른 공유 대기자까지 취소 또는 중복 디코딩");
        });

        await checkAsync("PF05 취소된 이웃 미리읽기는 대기열에서 디코딩하지 않음", async () =>
        {
            var a = Fixture("queue-a"); var b = Fixture("queue-b"); var c = Fixture("queue-c");
            using var started = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource(); var decoded = 0;
            var loader = new PreviewImageLoaderService((_, _) =>
            {
                if (Interlocked.Increment(ref decoded) == 1)
                {
                    started.Set();
                    if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("대기열 테스트 시간 초과");
                }
                return Pixels();
            }, maximumConcurrentDecodes: 1);
            var first = loader.LoadAsync(a);
            Assert(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "첫 디코딩 미시작");
            var abandoned = loader.LoadAsync(b, cancellation.Token);
            using var registrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (loader.PendingLoadCount < 2) await Task.Delay(1, registrationTimeout.Token);
            cancellation.Cancel();
            try { await abandoned; throw new InvalidOperationException("취소된 미리읽기 완료됨"); }
            catch (OperationCanceledException) { }
            proceed.Set(); await first; await loader.LoadAsync(c);
            Assert(decoded == 2, "취소한 이웃 이미지를 불필요하게 디코딩");
        });

        await checkAsync("PF06 초기화 직전 시작한 디코딩은 새 캐시에 남지 않음", async () =>
        {
            var path = Fixture("clear-inflight"); using var started = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim(); var decoded = 0;
            var loader = new PreviewImageLoaderService((_, _) =>
            {
                if (Interlocked.Increment(ref decoded) == 1)
                {
                    started.Set();
                    if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("초기화 테스트 대기 초과");
                }
                return Pixels();
            });
            var old = loader.LoadAsync(path);
            Assert(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "디코딩 미시작");
            loader.Clear(); proceed.Set(); await old; await loader.LoadAsync(path);
            Assert(decoded == 2, "초기화 전 디코딩이 다시 캐시에 저장됨");
        });

        await checkAsync("PF07 미리보기 3200px 가독성 유지 + 원본 참조 없는 픽셀 버퍼", async () =>
        {
            SelfTest.AddImage(directory, "large-preview.png", 2400, 3600);
            var path = Path.Combine(directory, "large-preview.png");
            var preview = await new PreviewImageLoaderService().LoadAsync(path);
            Assert(Math.Max(preview.PixelWidth, preview.PixelHeight) == 3200 && preview.IsFrozen,
                "기존 3200px 미리보기 해상도 변경");
            Assert(preview is not TransformedBitmap && preview is not BitmapFrame,
                "축소 픽셀이 전체 해상도 원본 객체를 계속 보유함");
        });

        await checkAsync("PF08 앞뒤 전환은 같은 비트맵·진행 UI 재사용, 구좌별 위치 정확", async () =>
        {
            var folder = Path.Combine(directory, "summary-ui");
            Directory.CreateDirectory(folder);
            var context = FolderContext.Resolve(folder);
            SelfTest.AddImage(context.Root, "1가상가.png"); SelfTest.AddImage(context.Root, "2가상나.png"); SelfTest.AddImage(context.Root, "8가상다.png");
            var window = new MainWindow(testMode: true);
            try
            {
                await window.OpenFolderAsync(context.Root); await window.LastPreviewTask;
                var panels = (Panel)window.FindName("QuotaProgress"); var controls = panels.Children.Cast<object>().ToArray();
                var image = (Image)window.FindName("PreviewImage"); var first = image.Source;
                await window.NavigateImageAsync(1); await window.LastPreviewTask;
                Assert(controls.SequenceEqual(panels.Children.Cast<object>()), "이동할 때 진행 UI를 매번 재생성");
                Assert(((TextBlock)((StackPanel)panels.Children[1]).Children[0]).Text == "2구좌 1/1", "2구좌 위치 집계 오류");
                await window.NavigateImageAsync(-1); await window.LastPreviewTask;
                Assert(ReferenceEquals(first, image.Source), "앞뒤 이동에서 같은 이미지 재디코딩");
                Assert(((TextBlock)((StackPanel)panels.Children[5]).Children[0]).Text == "6+구좌 0/1", "6구좌 이상 묶음 집계 오류");
            }
            finally { window.Close(); }
        });
    }
}

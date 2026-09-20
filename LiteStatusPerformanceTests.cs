using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace BoramRms.Lite;

public static class LiteStatusPerformanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private static void Assert(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("컨트롤 없음: " + name);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static TextBlock BoundText(DependencyObject row, string property) =>
        Descendants<TextBlock>(row).Single(text =>
            BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == property);

    private static void Click(CheckBox box, bool value)
    {
        box.IsChecked = value;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task InWindow(string root, Func<MainWindow, FolderContext, Task> action)
    {
        Directory.CreateDirectory(root);
        for (var quota = 1; quota <= 3; quota++) SelfTest.AddImage(root, quota + "가상성능.png");
        var context = FolderContext.Resolve(root);
        var window = new MainWindow(testMode: true)
        {
            Left = -16000,
            Top = -16000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        try
        {
            await window.OpenFolderAsync(context.Root).WaitAsync(Timeout);
            await window.LastPreviewTask.WaitAsync(Timeout);
            window.UpdateLayout();
            await action(window, context).WaitAsync(Timeout);
        }
        finally
        {
            await window.FlushStatusSavesAsync().WaitAsync(Timeout);
            if (window.IsVisible)
            {
                await window.ReloadAsync().WaitAsync(Timeout);
                await window.LastPreviewTask.WaitAsync(Timeout);
                window.Close();
            }
        }
    }

    public static async Task RunAsync(string run, Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync)
    {
        check("SP01 목록 색상은 고정 브러시를 재사용하고 변경 불가", () =>
        {
            var first = ImageItem.Brush("#557F45");
            Assert(first.IsFrozen, "목록 색상이 Freeze되지 않음");
            for (var n = 0; n < 1000; n++)
                Assert(ReferenceEquals(first, ImageItem.Brush("#557F45")), "동일 색상 조회마다 브러시를 재생성함");
            var a = new ImageItem { Quota = "1" };
            var b = new ImageItem { Quota = "1" };
            Assert(ReferenceEquals(a.QuotaBrush, b.QuotaBrush), "신청서마다 구좌 브러시를 재생성함");
            Assert(a.StatusBackground.IsFrozen && a.StatusForeground.IsFrozen, "상태 브러시가 변경 가능함");
            var original = a.StatusBackground;
            a.Card = true;
            Assert(!ReferenceEquals(original, a.StatusBackground), "카드 상태 변경에도 이전 배경색 사용");
            b.Card = true;
            Assert(ReferenceEquals(a.StatusBackground, b.StatusBackground), "카드 배경색 캐시를 공유하지 않음");
        });

        check("SP02 항목 변경 통지는 파생 파일명·상태·배경색 바인딩을 갱신", () =>
        {
            var item = new ImageItem { FullPath = Path.Combine(run, "1가상원래.png"), Quota = "1", Name = "가상원래" };
            var file = new TextBlock();
            var status = new TextBlock();
            var background = new Border();
            BindingOperations.SetBinding(file, TextBlock.TextProperty, new Binding(nameof(ImageItem.FileName)) { Source = item });
            BindingOperations.SetBinding(status, TextBlock.TextProperty, new Binding(nameof(ImageItem.StatusLabel)) { Source = item });
            BindingOperations.SetBinding(background, Border.BackgroundProperty, new Binding(nameof(ImageItem.StatusBackground)) { Source = item });
            var notifications = 0;
            ((INotifyPropertyChanged)item).PropertyChanged += (_, _) => notifications++;
            item.FullPath = Path.Combine(run, "2가상변경.png");
            item.Quota = "2"; item.Name = "가상변경";
            item.Card = true; item.Status = "보완";
            item.NotifyChanged();
            Assert(notifications > 0, "바인딩 변경 통지 누락");
            Assert(file.Text == "2가상변경.png" && status.Text == "[카드] 보완", "파생 텍스트가 갱신되지 않음");
            Assert(ReferenceEquals(background.Background, item.StatusBackground), "상태 배경 바인딩 미갱신");
        });

        await checkAsync("SP03 카드·보완 저장은 목록 초기화 없이 행·미리보기·다중 선택 유지", () =>
            InWindow(Path.Combine(run, "status-performance", "checks"), async (window, context) =>
            {
                var list = Control<ListBox>(window, "ImageList");
                var current = (ImageItem)list.SelectedItem;
                var second = (ImageItem)list.Items[1];
                list.SelectedItems.Add(second);
                window.UpdateLayout();
                var row = list.ItemContainerGenerator.ContainerFromItem(current);
                Assert(row != null, "현재 신청서 행이 렌더링되지 않음");
                var status = BoundText(row!, nameof(ImageItem.StatusLabel));
                var preview = Control<Image>(window, "PreviewImage").Source;
                var source = list.ItemsSource;
                var resets = 0;
                var changes = (INotifyCollectionChanged)list.Items;
                NotifyCollectionChangedEventHandler onChange = (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
                changes.CollectionChanged += onChange;
                try
                {
                    Click(Control<CheckBox>(window, "CardCheck"), true);
                    Assert(await window.LastAutoSaveTask, "카드 저장 실패");
                    var repair = Control<UniformGrid>(window, "RepairReasons").Children.OfType<CheckBox>()
                        .Single(box => box.Content.ToString() == "주소보완");
                    Click(repair, true);
                    Assert(await window.LastAutoSaveTask, "보완 저장 실패");
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    window.UpdateLayout();
                    Assert(resets == 0 && ReferenceEquals(source, list.ItemsSource), "체크마다 전체 목록을 초기화함");
                    Assert(ReferenceEquals(row, list.ItemContainerGenerator.ContainerFromItem(current)), "체크 저장이 현재 행을 재생성함");
                    Assert(status.Text == "[카드] 보완" && ReferenceEquals(list.SelectedItem, current), "현재 행 상태 또는 선택 미갱신");
                    Assert(list.SelectedItems.Count == 2 && list.SelectedItems.Contains(second), "다중 선택 손실");
                    Assert(ReferenceEquals(preview, Control<Image>(window, "PreviewImage").Source), "상태 변경만으로 이미지를 다시 읽음");
                    Assert(Control<TextBlock>(window, "LocalSummary").Text == "CMS 2 · 카드 1 · 보완 1 · 취소 0", "상태 요약 미갱신");
                    Assert(Control<TextBlock>(window, "ImageDetails").Text.Contains("[카드] 보완"), "선택 이미지 상태 표시 미갱신");
                    var saved = LiteWorkspace.Load(context);
                    Assert(saved.Single(item => item.Quota == "1").Card && saved.Single(item => item.Quota == "1").Status.Contains("보완"), "현재 신청서 상태 미저장");
                    Assert(!saved.Single(item => item.Quota == "2").Card && saved.Single(item => item.Quota == "2").Status == "", "다중 선택된 다른 신청서 상태 변경");
                }
                finally { changes.CollectionChanged -= onChange; }
            }));

        await checkAsync("SP04 이름 저장도 행 초기화 없이 새 파일명과 요약을 반영", () =>
            InWindow(Path.Combine(run, "status-performance", "rename"), async (window, context) =>
            {
                var list = Control<ListBox>(window, "ImageList");
                var current = (ImageItem)list.SelectedItem;
                var oldPath = current.FullPath;
                var row = list.ItemContainerGenerator.ContainerFromItem(current);
                Assert(row != null, "현재 신청서 행이 렌더링되지 않음");
                var file = BoundText(row!, nameof(ImageItem.FileName));
                var preview = Control<Image>(window, "PreviewImage").Source;
                var resets = 0;
                var changes = (INotifyCollectionChanged)list.Items;
                NotifyCollectionChangedEventHandler onChange = (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
                changes.CollectionChanged += onChange;
                try
                {
                    Control<TextBox>(window, "CombinedTextBox").Text = "4가상변경";
                    await window.SaveRenameAsync(false);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    window.UpdateLayout();
                    Assert(!window.HasPendingRename && File.Exists(Path.Combine(context.Root, "4가상변경.png")) && !File.Exists(oldPath), "이름 저장 실패");
                    Assert(resets == 0 && ReferenceEquals(row, list.ItemContainerGenerator.ContainerFromItem(current)), "이름 저장이 전체 목록 또는 행을 재생성함");
                    Assert(file.Text == "4가상변경.png" && current.Name == "가상변경" && current.Quota == "4", "이름 저장 후 기존 행 바인딩 미갱신");
                    Assert(Control<TextBlock>(window, "SelectedFileText").Text == file.Text, "현재 파일명 표시 미갱신");
                    Assert(ReferenceEquals(preview, Control<Image>(window, "PreviewImage").Source), "단순 리네임으로 미리보기 교체");
                    Assert(Control<TextBlock>(window, "StatusSummary").Text == "전체 3 / 표시 3", "이름 저장 후 목록 수량 변경");
                }
                finally { changes.CollectionChanged -= onChange; }
            }));
    }
}

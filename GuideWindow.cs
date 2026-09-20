using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace BoramRms.Lite;

public sealed class GuideWindow : Window
{
    public static IReadOnlyList<string> Topics { get; } = new[] { "빠른 시작", "단축키·버튼", "폴더·화면", "저장·주의" };
    public StackPanel Body { get; } = new() { Margin = new Thickness(2, 2, 8, 2) };
    public ScrollViewer Scroller { get; }
    private readonly List<Button> _topics = new();
    public int TopicIndex { get; private set; }
    public GuideWindow()
    {
        Title = "보람 RMS Lite · 사용 안내"; Width = 810; Height = Math.Min(670, SystemParameters.WorkArea.Height - 30);
        MinWidth = 500; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.BindWindow(this, "PanelBg");
        var root = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var hero = new StackPanel(); hero.Children.Add(Text("보람 RMS Lite, 이렇게 쓰세요", 23, true, "AccentDark"));
        hero.Children.Add(Text("체크는 바로 저장 · 이름은 필요할 때만 · Space로 다음", 13));
        root.Children.Add(hero);
        var nav = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        for (int i = 0; i < Topics.Count; i++)
        {
            var n = i; var button = new Button { Content = Topics[i], Padding = new Thickness(14, 6, 14, 6), MinHeight = 32, Margin = new Thickness(0, 0, 6, 2) };
            button.Click += (_, _) => ShowTopic(n); _topics.Add(button); nav.Children.Add(button);
        }
        Grid.SetRow(nav, 1); root.Children.Add(nav);
        Scroller = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(Scroller, 2); root.Children.Add(Scroller);
        var footer = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
        var close = new Button { Content = "닫기", MinWidth = 76 }; close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var note = Text("신청서 이미지와 작업 데이터는 안내 화면에서 변경하지 않습니다.", 11, false, "MutedTextBrush"); note.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(note);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root; InterfaceScale.Bind(root); ShowTopic(0);
    }
    private static TextBlock Text(string text, double size = 13, bool bold = false, string resource = "TextBrush")
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), LineHeight = size * 1.55 };
        block.SetResourceReference(TextBlock.ForegroundProperty, resource); return block;
    }
    private static Border Box(UIElement child, string color = "PanelFooterBg")
    {
        var box = new Border { Child = child, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(13, 9, 13, 9), Margin = new Thickness(0, 0, 0, 9) };
        box.SetResourceReference(Border.BackgroundProperty, color); box.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return box;
    }
    private static Border Card(string title, string description, string? example = null)
    {
        var content = new StackPanel(); content.Children.Add(Text(title, 15, true, "AccentDark")); content.Children.Add(Text(description));
        if (example != null) content.Children.Add(Text(example, 12, false, "MutedTextBrush"));
        return Box(content);
    }
    private static Border Step(string number, string title, string description)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(46) }); grid.ColumnDefinitions.Add(new());
        var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); badge.Child = new TextBlock { Text = number, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(badge); var stack = new StackPanel(); stack.Children.Add(Text(title, 15, true)); stack.Children.Add(Text(description, 13)); Grid.SetColumn(stack, 1); grid.Children.Add(stack); return Box(grid);
    }
    private static Border Shortcut(string key, string action, string detail)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(142) }); grid.ColumnDefinitions.Add(new());
        var badge = new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "SelectionBg"); badge.Child = Text(key, 14, true, "AccentDark"); grid.Children.Add(badge);
        var text = new StackPanel(); text.Children.Add(Text(action, 14, true)); text.Children.Add(Text(detail, 12)); Grid.SetColumn(text, 1); grid.Children.Add(text); return Box(grid, "PanelBg");
    }
    private static Border ScreenMap()
    {
        var map = new Grid(); map.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); map.ColumnDefinitions.Add(new() { Width = new GridLength(1.4, GridUnitType.Star) }); map.ColumnDefinitions.Add(new());
        var labels = new[] { ("리네임", "이름 수정\n보완·기타 체크"), ("신청서 이미지", "휠 확대 · 드래그 이동\n맞춤으로 전체 보기"), ("신청서 목록", "폴더 선택\n검색 · 이미지 선택") };
        for (int i = 0; i < labels.Length; i++)
        {
            var stack = new StackPanel(); stack.Children.Add(Text(labels[i].Item1, 13, true, "AccentDark")); stack.Children.Add(Text(labels[i].Item2, 11));
            var panel = Box(stack, i == 1 ? "HeaderBg" : "PanelBg"); panel.Margin = new Thickness(0, 0, i == 2 ? 0 : 6, 0); Grid.SetColumn(panel, i); map.Children.Add(panel);
        }
        return Box(map, "PanelBg");
    }
    public void ShowTopic(int index)
    {
        if (index < 0 || index >= Topics.Count) throw new ArgumentOutOfRangeException(nameof(index));
        TopicIndex = index; Body.Children.Clear();
        for (int i = 0; i < _topics.Count; i++) _topics[i].Style = (Style)FindResource(i == index ? "PrimaryButton" : typeof(Button));
        if (index == 0)
        {
            Body.Children.Add(ScreenMap());
            Body.Children.Add(Step("1", "폴더 열기", "+ 폴더 선택 → 신청서 폴더를 고르세요. 여러 폴더가 보이면 필요한 것만 체크합니다."));
            Body.Children.Add(Step("2", "확인하고 체크", "보완 사유·카드 등을 체크하거나 해제하면 바로 저장됩니다. 별도 저장 버튼은 필요 없습니다."));
            Body.Children.Add(Step("3", "↓ 또는 Space로 다음", "이름을 바꿀 때만 입력하세요. 빈칸이면 기존 이름을 유지합니다. ↑ 또는 Ctrl+Space는 이전입니다."));
            Body.Children.Add(Text("예: 카드 체크 → Space → 다음 신청서. 이 흐름으로 한 장씩 확인하세요.", 13, true, "AccentDark"));
        }
        else if (index == 1)
        {
            Body.Children.Add(Shortcut("↑ / ↓", "저장하고 이전 / 다음", "이름 입력칸·이미지·신청서 목록에서 동작합니다. 저장에 실패하면 현재 신청서에 머뭅니다."));
            Body.Children.Add(Shortcut("Space", "저장하고 다음", "새 이름이 있으면 저장합니다. 빈칸이면 기존 파일명 그대로 이동합니다."));
            Body.Children.Add(Shortcut("Ctrl + Space", "저장하고 이전", "현재 이름을 저장한 뒤 앞 신청서로 돌아갑니다."));
            Body.Children.Add(Shortcut("Ctrl + S", "저장만", "이름과 남은 메모를 저장하고 현재 이미지에 머뭅니다."));
            Body.Children.Add(Shortcut("휠 / 드래그", "이미지 확대 / 이동", "이미지 위에서 조작하세요. ‘맞춤’은 전체 이미지 보기입니다."));
            Body.Children.Add(Card("이전 폴더 열기 ≠ 마지막 변경 취소", "이전 폴더 열기: 지난번 폴더와 선택 위치를 다시 엽니다.\n마지막 변경 취소: 이번 실행에서 현재 폴더의 마지막 이름·체크·메모·회전 저장 1건만 취소합니다.", "입력 원래대로: 아직 저장하지 않은 입력만 되돌립니다. 이미 저장된 체크는 유지합니다."));
            Body.Children.Add(Text("검색칸·메모칸·콤보박스의 방향키는 원래 동작을 유지합니다. Ctrl/Shift+방향키와 한글 후보 선택도 가로채지 않습니다. 검색·메모의 Space는 공백이며, 체크박스의 Space는 체크를 바꿉니다.", 12));
        }
        else if (index == 2)
        {
            Body.Children.Add(Card("화면이 답답하거나 아래가 가려질 때", "상단 ‘화면 배율’에서 80·85·90·95·100%를 고르세요. 기본값은 90%이며, 선택한 배율은 다시 실행해도 유지됩니다.", "80~85%: 작은 화면에서 더 많이 보기 / 100%: 글자와 버튼을 크게 보기"));
            Body.Children.Add(Card("화면 배율과 이미지 확대는 다릅니다", "화면 배율: 버튼·글자·간격 등 앱 전체 크기를 바꿉니다.\n이미지 휠 확대: 신청서 이미지만 확대합니다.", "창이나 화면 배율이 바뀌면 이미지 전체가 들어오도록 맞춥니다. 글자·이미지 데이터는 바꾸지 않습니다."));
            Body.Children.Add(Card("여러 폴더 중 필요한 것만 열기", "+ 폴더 선택 → 상위 폴더 지정 → 폴더 이름 검색 → 필요한 항목 체크 → ‘선택한 폴더 열기’. 각 폴더가 별도 탭으로 열립니다."));
            Body.Children.Add(Card("열린 폴더를 목록에서 바꾸기", "오른쪽 ‘신청서 목록’ 위의 ‘작업 폴더’ 드롭다운에서 고르세요. 위쪽 탭과 같은 폴더가 선택되며, 그 폴더에서 보던 이미지로 돌아갑니다."));
            Body.Children.Add(Card("4색 테마 / 패널 분리", "테마 메뉴: 녹색·블루·보라·핑크 즉시 전환.\n각 패널의 ↗: 따로 띄우기. 리네임·목록의 빈자리는 이미지 영역으로 합칩니다.\n이미지 위 ‘리네임 복귀’·‘목록 복귀’ 또는 분리창의 ‘복귀’/닫기로 이전 너비를 되돌립니다.", "작은 창에서도 스크롤로 모든 항목에 접근할 수 있습니다."));
        }
        else
        {
            Body.Children.Add(Card("체크는 즉시 저장됩니다", "보완 사유와 카드·주말미등록·추가·취소·미성년자를 체크하거나 해제하면 현재 이미지의 상태만 저장합니다. 메모는 입력을 마치거나 이동할 때 저장합니다.", "입력전취소/입력후취소 및 입력전변경/입력후변경은 각각 서로 대체됩니다."));
            Body.Children.Add(Card("파일명을 비워도 괜찮습니다", "빈칸은 ‘이름 변경 안 함’입니다. Space·Ctrl+Space로 이동해도 원래 파일명과 이미지가 유지됩니다.", "실제로 새 이름을 넣었을 때는 1~99 구좌수+이름을 사용합니다. 중복 이름·잘못된 문자는 저장하지 않습니다."));
            Body.Children.Add(Card("저장 오류가 보일 때", "화면 아래의 파일명과 이유를 확인하세요. 실제 저장 실패 시 현재 입력을 유지합니다. ‘입력 원래대로’는 미저장 초안을 취소하고, ‘상태 다시 저장’은 실패한 체크 저장을 재시도합니다."));
            Body.Children.Add(Card("삭제·압축 전에 꼭 확인", "휴지통 이동: 선택한 이미지만 처리합니다.\n300KB 미만으로 줄이기: 현재 표시 목록의 큰 이미지를 원본에 바로 저장합니다.", "압축은 별도 백업이 없고 ‘마지막 변경 취소’로 복구할 수 없습니다. 삭제·업데이트도 이 버튼의 취소 대상이 아닙니다."));
            Body.Children.Add(Card("테마나 배율을 바꿔도 작업은 그대로", "테마·화면 배율은 작업 기록과 따로 저장합니다. 신청서 이미지의 색상, 이름 초안, 체크·메모를 바꾸지 않습니다. 전체 RMS와 같은 폴더를 동시에 수정하지 마세요."));
        }
        Scroller.ScrollToTop();
    }
}

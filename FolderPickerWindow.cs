using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
namespace BoramRms.Lite;

public sealed class FolderPickerWindow : Window
{
    public IReadOnlyList<FolderChoice> Choices { get; }
    public TextBox Search { get; } = new() { ToolTip = "폴더 이름 또는 경로 검색", MinHeight = 30 };
    public ListBox ChoicesList { get; } = new();
    public Button OpenSelectedButton { get; } = new() { Content = "선택한 폴더 열기", IsEnabled = false };
    private readonly TextBlock _count = new();
    public IReadOnlyList<string> SelectedPaths => Choices.Where(c => c.IsSelected).Select(c => c.Path).ToArray();
    public FolderPickerWindow(string parent, FolderScan scan)
    {
        Choices = scan.Choices;
        Title = "보람 RMS Lite · 폴더 목록에서 선택"; Width = 780; Height = Math.Min(600, SystemParameters.WorkArea.Height - 30);
        MinWidth = 480; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.BindWindow(this);
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "작업할 폴더만 골라 여세요", FontSize = 21, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock { Text = "체크한 폴더를 각각 탭으로 엽니다. 원본 파일은 이동하거나 복사하지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) });
        title.Children.Add(new TextBlock { Text = parent, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = parent });
        if (scan.Warnings.Count > 0)
        {
            var warning = new TextBlock { Text = "일부 폴더는 검색하지 못했습니다.\n" + string.Join("\n", scan.Warnings.Take(4)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            title.Children.Add(new ScrollViewer { Content = warning, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        grid.Children.Add(title);
        var tools = new DockPanel { Margin = new Thickness(0, 12, 0, 10) };
        var all = new Button { Content = "표시 항목 선택" }; var none = new Button { Content = "전체 해제" };
        DockPanel.SetDock(all, Dock.Right); DockPanel.SetDock(none, Dock.Right); tools.Children.Add(none); tools.Children.Add(all); tools.Children.Add(Search);
        Grid.SetRow(tools, 1); grid.Children.Add(tools);
        ChoicesList.ItemTemplate = (DataTemplate)XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <CheckBox IsChecked="{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" ToolTip="{Binding Path}" Margin="2,3" HorizontalContentAlignment="Stretch" ContentTemplate="{x:Null}">
  <StackPanel Margin="3,2"><TextBlock Text="{Binding Title}" FontSize="13" FontWeight="SemiBold"/><TextBlock Text="{Binding RelativePath}" FontSize="11" Foreground="{DynamicResource MutedTextBrush}" TextTrimming="CharacterEllipsis" Margin="0,3,0,0"/></StackPanel>
 </CheckBox>
</DataTemplate>
""");
        ScrollViewer.SetVerticalScrollBarVisibility(ChoicesList, ScrollBarVisibility.Auto);
        Grid.SetRow(ChoicesList, 2); grid.Children.Add(ChoicesList);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        OpenSelectedButton.Style = (Style)FindResource("PrimaryButton");
        var cancel = new Button { Content = "취소", IsCancel = true };
        actions.Children.Add(cancel); actions.Children.Add(OpenSelectedButton); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        _count.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_count); Grid.SetRow(footer, 3); grid.Children.Add(footer);
        Content = grid; InterfaceScale.Bind(grid);
        all.Click += (_, _) => SelectVisible(); none.Click += (_, _) => { foreach (var c in Choices) c.IsSelected = false; };
        Search.TextChanged += (_, _) => Filter();
        foreach (var c in Choices) c.PropertyChanged += ChoiceChanged;
        Closed += (_, _) => { foreach (var c in Choices) c.PropertyChanged -= ChoiceChanged; };
        OpenSelectedButton.Click += (_, _) => { if (SelectedPaths.Count > 0) DialogResult = true; };
        Filter();
    }
    private void ChoiceChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateCount();
    public void SelectVisible() { foreach (var c in ChoicesList.Items.OfType<FolderChoice>()) c.IsSelected = true; }
    private void Filter()
    {
        var text = Search.Text.Trim();
        ChoicesList.ItemsSource = Choices.Where(c => text.Length == 0 || c.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || c.RelativePath.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        UpdateCount();
    }
    private void UpdateCount()
    {
        var n = SelectedPaths.Count;
        _count.Text = $"표시 {ChoicesList.Items.Count} / {Choices.Count}개 · 선택 {n}개";
        OpenSelectedButton.IsEnabled = n > 0; OpenSelectedButton.Content = $"선택한 {n}개 폴더 열기";
    }
}

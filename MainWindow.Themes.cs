using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private void InitializeThemes()
    {
        ThemeManager.BindWindow(this);
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "PanelBg");
        menu.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        menu.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        foreach (var palette in ThemeManager.Palettes)
        {
            var choice = new MenuItem { Header = palette.Label, Tag = palette.Id, IsCheckable = true, StaysOpenOnClick = false,
                ToolTip = palette.Label + " 테마 · 즉시 적용하고 다음 실행에도 유지합니다.",
                Icon = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = ImageItem.Brush(palette.Accent) } };
            choice.Click += (_, _) => ChangeTheme(palette.Id);
            menu.Items.Add(choice);
        }
        ThemeButton.ContextMenu = menu;
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => { ThemeManager.Changed -= OnThemeChanged; menu.IsOpen = false; };
        UpdateThemeMenu();
    }
    private void OnThemeChanged(object? sender, EventArgs e) => UpdateThemeMenu();
    private void UpdateThemeMenu()
    {
        if (ThemeButton == null) return;
        ThemeLabel.Text = "테마 · " + ThemeManager.Current.Label;
        if (ThemeButton.ContextMenu != null)
            foreach (var item in ThemeButton.ContextMenu.Items.OfType<MenuItem>()) item.IsChecked = (string)item.Tag == ThemeManager.Current.Id;
    }
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeButton.ContextMenu is not ContextMenu menu) return;
        UpdateThemeMenu(); menu.PlacementTarget = ThemeButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    public bool ChangeTheme(string id)
    {
        try
        {
            // No save/navigation/reload: theme switching must leave drafts and selection intact.
            ThemeManager.Select(id); return true;
        }
        catch (Exception ex)
        {
            Error(new System.IO.IOException("테마 설정을 저장하지 못했습니다. 기존 테마와 작업 입력은 유지합니다. " + ex.Message));
            UpdateThemeMenu(); return false;
        }
    }
}

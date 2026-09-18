using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BoramRms.Lite;

public sealed record ThemePalette(string Id, string Label, string Accent, string Dark, string Background,
    string Header, string Selection, string Line, string Text, string Muted, string Canvas,
    string Input, string Focus, string Hover, string Pressed, string TextSelection)
{
    public IReadOnlyDictionary<string, string> Colors => new Dictionary<string, string>
    {
        ["AppBg"] = Background, ["PanelBg"] = "#FFFFFF", ["HeaderBg"] = Header,
        ["SelectionBg"] = Selection, ["LineBrush"] = Line, ["AccentBrush"] = Accent,
        ["AccentDark"] = Dark, ["TextBrush"] = Text, ["MutedTextBrush"] = Muted,
        ["CanvasBrush"] = Canvas, ["ButtonBg"] = Input, ["InputBg"] = "#FCFDFC",
        ["FocusBrush"] = Focus, ["HoverBg"] = Hover, ["PressedBg"] = Pressed,
        ["RailBg"] = Input, ["RailForeground"] = Accent, ["TextSelectionBrush"] = TextSelection,
        ["ProgressTrackBrush"] = Selection, ["PanelFooterBg"] = Input, ["ErrorBrush"] = "#AC4942"
    };
}

// Theme preferences are separate from task/session settings and never touch image data.
public static class ThemeManager
{
    public static IReadOnlyList<ThemePalette> Palettes { get; } = Array.AsReadOnly(new[]
    {
        new ThemePalette("green", "녹색", "#267B4B", "#195434", "#F1F7F3", "#E4F2E9", "#DDEFE3", "#D4E4D9", "#23372B", "#607267", "#EAF0EC", "#F4F9F5", "#8AB49A", "#EAF5EE", "#CDE5D6", "#B9DCC7"),
        new ThemePalette("blue", "블루", "#1466CC", "#124C94", "#F0F6FD", "#E1EDFC", "#DCEBFC", "#D2E1F3", "#213149", "#5F6F85", "#EBF1F8", "#F3F7FD", "#8AAFE1", "#E8F1FD", "#C9DFF9", "#BDD6F6"),
        new ThemePalette("purple", "보라", "#7040C8", "#512A97", "#F6F2FC", "#ECE4FB", "#E9DFF9", "#E0D6EE", "#302442", "#70647E", "#EFECF6", "#F8F5FD", "#B19AD8", "#F0E9FC", "#DDCEF3", "#D2BCEE"),
        new ThemePalette("pink", "핑크", "#C52B68", "#96204F", "#FDF2F7", "#FBE3ED", "#F9DEEA", "#EFD5E0", "#3C2831", "#7D6270", "#F6EDF1", "#FEF5F9", "#D99AB4", "#FCEAF2", "#F3CBDE", "#F1BED4")
    });
    private sealed record Preference(string Theme);
    private static string? _profile;
    private static DrawingImage? _baseLogo;
    private static readonly Dictionary<string, (DrawingImage Logo, BitmapFrame Icon)> Icons = new();
    public static ThemePalette Current { get; private set; } = Palettes[0];
    public static event EventHandler? Changed;
    public static ThemePalette Resolve(string? id) => Palettes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];
    public static string PreferencePath => Path.Combine(SettingsStore.DirectoryPath, "theme.json");
    public static void EnsureLoaded(bool reload = false)
    {
        var profile = SafePaths.Normalize(SettingsStore.DirectoryPath);
        if (!reload && _profile == profile) return;
        var id = "green";
        try
        {
            SafePaths.NoLinks(PreferencePath);
            if (File.Exists(PreferencePath) && new FileInfo(PreferencePath).Length <= 4096)
                id = JsonSerializer.Deserialize<Preference>(File.ReadAllText(PreferencePath), SettingsStore.Json)?.Theme ?? "green";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        ApplyCore(Resolve(id)); _profile = profile;
    }
    public static void Select(string id, bool persist = true)
    {
        var palette = Palettes.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("지원하지 않는 테마입니다.");
        Application.Current.Dispatcher.VerifyAccess();
        if (persist)
        {
            SafePaths.NoLinks(PreferencePath);
            // Write first: an unwritable settings folder must not falsely look persisted.
            FileChanges.AtomicWrite(PreferencePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Preference(palette.Id), SettingsStore.Json)));
        }
        ApplyCore(palette); _profile = SafePaths.Normalize(SettingsStore.DirectoryPath);
    }
    private static void ApplyCore(ThemePalette palette)
    {
        var app = Application.Current; app.Dispatcher.VerifyAccess();
        _baseLogo ??= ((DrawingImage)app.FindResource("RmsIcon")).CloneCurrentValue();
        foreach (var entry in palette.Colors) app.Resources[entry.Key] = ImageItem.Brush(entry.Value);
        if (!Icons.TryGetValue(palette.Id, out var icon))
        {
            var logo = _baseLogo.CloneCurrentValue();
            ((GeometryDrawing)((DrawingGroup)logo.Drawing).Children[1]).Brush = ImageItem.Brush(palette.Accent);
            logo.Freeze();
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(logo, new Rect(0, 0, 64, 64));
            var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            var frame = BitmapFrame.Create(bitmap); frame.Freeze();
            icon = (logo, frame); Icons[palette.Id] = icon;
        }
        app.Resources["RmsIcon"] = icon.Logo; app.Resources["ThemeWindowIcon"] = icon.Icon;
        Current = palette; Changed?.Invoke(null, EventArgs.Empty);
    }
    public static void BindWindow(Window window, string background = "AppBg")
    {
        EnsureLoaded();
        window.SetResourceReference(Window.BackgroundProperty, background);
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.SetResourceReference(Window.IconProperty, "ThemeWindowIcon");
    }
}

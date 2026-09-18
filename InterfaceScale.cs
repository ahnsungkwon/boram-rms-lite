using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
namespace BoramRms.Lite;

public sealed record ScaleOption(int Percent)
{
    public string Label => Percent + "%";
}
// Independent display preference: never overwrites task/session or theme settings.
public static class InterfaceScale
{
    private sealed record Preference(int Percent = 90);
    public static IReadOnlyList<ScaleOption> Options { get; } = Array.AsReadOnly(new[] { 80, 85, 90, 95, 100 }.Select(p => new ScaleOption(p)).ToArray());
    public static int Percent { get; private set; } = 90;
    public static double Factor => Percent / 100.0;
    public static event EventHandler? Changed;
    private static string? _profile;
    public static string PreferencePath => Path.Combine(SettingsStore.DirectoryPath, "display.json");
    public static void EnsureLoaded(bool reload = false)
    {
        var profile = SafePaths.Normalize(SettingsStore.DirectoryPath);
        if (!reload && _profile == profile) return;
        int percent = 90;
        try
        {
            SafePaths.NoLinks(PreferencePath);
            if (File.Exists(PreferencePath) && new FileInfo(PreferencePath).Length <= 4096)
                percent = JsonSerializer.Deserialize<Preference>(File.ReadAllText(PreferencePath), SettingsStore.Json)?.Percent ?? 90;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Apply(Options.Any(p => p.Percent == percent) ? percent : 90); _profile = profile;
    }
    public static void Select(int percent, bool persist = true)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!Options.Any(p => p.Percent == percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        if (persist)
        {
            SafePaths.NoLinks(PreferencePath);
            FileChanges.AtomicWrite(PreferencePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Preference(percent), SettingsStore.Json)));
        }
        Apply(percent); _profile = SafePaths.Normalize(SettingsStore.DirectoryPath);
    }
    private static void Apply(int percent)
    {
        Application.Current.Dispatcher.VerifyAccess();
        Percent = percent;
        var transform = new ScaleTransform(Factor, Factor); transform.Freeze();
        Application.Current.Resources["InterfaceScaleTransform"] = transform;
        Changed?.Invoke(null, EventArgs.Empty);
    }
    public static void Bind(FrameworkElement content)
    {
        EnsureLoaded();
        content.SetResourceReference(FrameworkElement.LayoutTransformProperty, "InterfaceScaleTransform");
    }
}

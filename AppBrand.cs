using System.Windows.Media.Imaging;
namespace BoramRms.Lite;
public static class AppBrand
{
    public const string IconUri = "pack://application:,,,/Assets/rms-lite-green.ico";
    private static readonly Lazy<BitmapFrame> Value = new(() =>
    {
        var frame = BitmapFrame.Create(new Uri(IconUri, UriKind.Absolute));
        frame.Freeze();
        return frame;
    });
    public static BitmapFrame Icon => Value.Value;
}

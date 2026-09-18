using System.IO;
using System.Text;
using System.Windows;
namespace BoramRms.Lite;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdatePackage.ApplyHelperAsync(Path.GetFullPath(e.Args[1]))); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--update-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateTests.ProbeAsync(Path.GetFullPath(e.Args[1]))); return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exit = await LiteWorkflowTests.RunAsync(Path.GetFullPath(e.Args[1]));
            Shutdown(exit); return;
        }
        DispatcherUnhandledException += (_, a) =>
        {
            MessageBox.Show(a.Exception.Message, "보람 RMS Lite · 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        MainWindow = new MainWindow();
        if (e.Args.Length == 3 && e.Args[0] == "--updated" && e.Args[1] == "--health-file")
        {
            var health = Path.GetFullPath(e.Args[2]);
            MainWindow.ContentRendered += async (_, _) =>
            {
                if (SafePaths.Under(health, UpdatePackage.UpdatesRoot) && Path.GetFileName(health) == "startup-ok")
                    FileChanges.AtomicWrite(health, Encoding.UTF8.GetBytes(UpdateIdentity.CurrentVersion));
                await ((MainWindow)MainWindow).RestoreAfterUpdateAsync();
            };
        }
        MainWindow.Show();
        if (e.Args.Length == 1 && Directory.Exists(e.Args[0]))
            await ((MainWindow)MainWindow).OpenFolderAsync(e.Args[0]);
    }
}

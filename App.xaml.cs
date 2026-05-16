using System.Windows;

namespace StellaOrion;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = new MainWindow();
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

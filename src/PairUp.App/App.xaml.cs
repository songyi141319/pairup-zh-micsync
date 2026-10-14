using System.Windows;
using System.Windows.Threading;

namespace PairUp.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        // Let the splash actually paint a frame before MainWindow's constructor (which does
        // synchronous WASAPI device enumeration) blocks the UI thread.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var main = new MainWindow(e.Args.Contains("--ui-preview", StringComparer.Ordinal));
        MainWindow = main;

        void CloseSplash(object? sender, RoutedEventArgs args)
        {
            main.Loaded -= CloseSplash;

            // The ripple loop is feedback, not a mandatory startup wait.
            splash.Close();
        }
        main.Loaded += CloseSplash;

        main.Show();
    }
}

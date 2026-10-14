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
        // Loaded only means the native tree exists. Keep it out of view until the
        // web page has received device state and acknowledged its first render.
        main.Opacity = 0;
        main.ShowInTaskbar = false;
        main.ShowActivated = false;

        async void ShowReadyInterface(object? sender, EventArgs args)
        {
            main.PrimaryInterfaceReady -= ShowReadyInterface;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!main.IsLoaded) return;
            main.Opacity = 1;
            main.ShowInTaskbar = true;
            main.ShowActivated = true;
            main.Activate();
            splash.Close();
        }
        main.PrimaryInterfaceReady += ShowReadyInterface;
        main.Closed += (_, _) => splash.Close();

        main.Show();
    }
}

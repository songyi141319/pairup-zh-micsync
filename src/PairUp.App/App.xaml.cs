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
        // WebView2 owns a native child window: keep its parent opaque and visible
        // while it initializes. Hide only the legacy page to prevent its flash.
        if (main.Content is UIElement legacyPage)
            legacyPage.Visibility = Visibility.Collapsed;
        main.ShowActivated = false;

        async void ShowReadyInterface(object? sender, EventArgs args)
        {
            main.PrimaryInterfaceReady -= ShowReadyInterface;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!main.IsLoaded) return;
            main.ShowActivated = true;
            main.Activate();
            splash.Close();
        }
        main.PrimaryInterfaceReady += ShowReadyInterface;
        main.Closed += (_, _) => splash.Close();

        main.Show();
    }
}

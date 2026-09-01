using System.Threading;
using System.Windows;
using D2MacroNative.Services;

namespace D2MacroNative;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (AppUpdateService.TryApplyUpdate(e.Args, out var updateError))
        {
            if (!string.IsNullOrWhiteSpace(updateError))
                System.Windows.MessageBox.Show($"The update could not be installed.\n\n{updateError}",
                    "/stalking macro updater", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, "StalkingMacro.Singleton.3F71A9C2", out var createdNew);
        _ownsSingleInstance = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("/stalking macro is already running in the system tray.", "/stalking macro",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SquishyDisk.Windows;

namespace SquishyDisk.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--drive-access-worker")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DriveAccessBroker.RunWorkerAsync(e.Args));
            return;
        }
        instanceMutex = new Mutex(true, @"Local\SquishyDisk-" + Environment.UserName, out bool first);
        if (!first) { MessageBox.Show("SquishyDisk is already open.", "SquishyDisk"); Shutdown(); return; }
        DispatcherUnhandledException += (_, error) => {
            MessageBox.Show(error.Exception.Message, "SquishyDisk", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--verify-ui")
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = 0; window.ShowActivated = false;
            window.Show();
            try { await window.VerifyUiAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[1]), "ui-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        var warnings = await Task.Run(TargetFiles.Recover);
        if (warnings.Count > 0) window.ShowNotice(string.Join(Environment.NewLine, warnings));
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}

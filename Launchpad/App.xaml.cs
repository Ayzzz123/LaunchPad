using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Launchpad.Core;
using Launchpad.Services;

namespace Launchpad;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private MainWindow? _mainWindow;
    private NotifyAreaIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var mutexName = $"Local\\Launchpad.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..24]}";
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            ShowExistingInstance();
            Shutdown();
            return;
        }

        var store = LayoutStore.ForUser(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var dataDirectory = store.DirectoryPath;
        _mainWindow = new MainWindow(store, dataDirectory);
        _mainWindow.ShowActivated = false;
        MainWindow = _mainWindow;
        var handle = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _mainWindow.Attach(handle);

        _trayIcon = new NotifyAreaIcon(
            _mainWindow.ShowLauncher,
            _mainWindow.ShowAppManagement,
            _mainWindow.ShowSettings,
            Shutdown);
        _mainWindow.SetTrayNotification(_trayIcon.ShowNotice);
        if (!string.IsNullOrWhiteSpace(_mainWindow.StartupHotkeyNotice))
        {
            _mainWindow.ShowNotice(_mainWindow.StartupHotkeyNotice);
        }

        _mainWindow.Show();
        _mainWindow.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _mainWindow?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void ShowExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var window = NativeMethods.FindWindow(null, global::Launchpad.MainWindow.HostWindowTitle);
            if (window != IntPtr.Zero)
            {
                NativeMethods.PostMessage(window, NativeMethods.ShowLauncherMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            Thread.Sleep(100);
        }
    }
}

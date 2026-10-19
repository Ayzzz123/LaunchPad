using System.ComponentModel;
using System.Runtime.InteropServices;
using Launchpad.Core;

namespace Launchpad.Services;

internal sealed class AppLauncher
{
    public Task LaunchAsync(LaunchableApp app)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Launch(app);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Launchpad application launch",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Launch(LaunchableApp app)
    {
        var parsingName = app.ParsingName;
        var isShellItem = parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        var executeInfo = new NativeMethods.ShellExecuteInfo
        {
            Size = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
            Mask = 0x00000100u | 0x00000400u | (isShellItem ? 0x0000000Cu : 0u),
            Verb = "open",
            File = parsingName,
            Parameters = string.IsNullOrWhiteSpace(app.Arguments) ? null : app.Arguments,
            Directory = string.IsNullOrWhiteSpace(app.WorkingDirectory) ? null : app.WorkingDirectory,
            Show = 1,
        };

        if (!NativeMethods.ShellExecuteEx(ref executeInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开“{app.Name}”。");
        }
    }
}

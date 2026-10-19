using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Launchpad.Services;

internal static class WindowBackdrop
{
    public static bool Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var transparent = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is int value && value != 0;
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && transparent && !SystemParameters.HighContrast;
        // Do not combine the default Desktop Acrylic tint with a custom accent policy.
        var backdrop = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
        var accent = new AccentPolicy
        {
            // BlurBehind (3) returns success but renders black on the verified Windows 11 desktop.
            State = supported ? 4 : 0, // ACCENT_ENABLE_ACRYLICBLURBEHIND / DISABLED
            Flags = 2,
            Color = dark ? 0x18100C08u : 0x40FDF9F7u, // AABBGGRR, dark 9% / light 25% tint
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(accent, memory, false);
            var data = new CompositionAttribute { Attribute = 19, Data = memory, Size = (nuint)Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(hwnd, ref data) && supported;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttribute { public int Attribute; public IntPtr Data; public nuint Size; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribute data);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}

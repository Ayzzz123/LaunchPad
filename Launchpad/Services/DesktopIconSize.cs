using Microsoft.Win32;

namespace Launchpad.Services;

internal static class DesktopIconSize
{
    // Explorer stores its desktop icon size in logical pixels; WPF applies monitor DPI.
    public static double Read() => Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags\1\Desktop", "IconSize", 48) is int size
        ? Math.Clamp(size, 16, 96) : 48;
}

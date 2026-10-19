using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Launchpad.Services;

internal static class SystemTheme
{
    public static bool Apply(Window window)
    {
        var dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var palette = new (string Key, string Light, string Dark)[]
        {
            ("TextPrimary", "#252B35", "#EEF0F5"),
            ("TextSecondary", "#77808F", "#A0A7B4"),
            ("Accent", "#3478F6", "#6BA1FF"),
            ("ControlSurface", "#65FFFFFF", "#5020252D"),
            ("ControlHover", "#DDFFFFFF", "#B0353A44"),
            ("ControlBorder", "#90FFFFFF", "#24FFFFFF"),
            ("InputSurface", "#9CFFFFFF", "#7010151C"),
            ("InputBorder", "#AAFFFFFF", "#28FFFFFF"),
            ("PanelSurface", "#55FFFFFF", "#48101318"),
            ("SidebarSurface", "#38FFFFFF", "#34101318"),
            ("FolderSurface", "#F5F7FA", "#ED101318"),
            ("ActionSurface", "#F8FAFC", "#F013161C"),
            ("TileHover", "#38FFFFFF", "#12FFFFFF"),
            ("TileHoverBorder", "#44FFFFFF", "#14FFFFFF"),
            ("TilePressed", "#50FFFFFF", "#20FFFFFF"),
            ("PlaceholderSurface", "#16909090", "#28B5BDCC"),
            ("FolderIconSurface", "#A0FFFFFF", "#80646C7C"),
            ("SelectedControl", "#FFFFFF", "#A0333944"),
            ("Divider", "#103C506E", "#18FFFFFF"),
            ("Error", "#C44848", "#FF9292"),
        };
        foreach (var (key, light, night) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            window.Resources[key] = brush;
        }
        return dark;
    }
}

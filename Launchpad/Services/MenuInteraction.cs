using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Launchpad.Services;

public static class MenuInteraction
{
    public static readonly DependencyProperty KeyboardNavigationActiveProperty = DependencyProperty.RegisterAttached(
        "KeyboardNavigationActive", typeof(bool), typeof(MenuInteraction),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetKeyboardNavigationActive(DependencyObject target) => (bool)target.GetValue(KeyboardNavigationActiveProperty);
    public static void SetKeyboardNavigationActive(DependencyObject target, bool value) => target.SetValue(KeyboardNavigationActiveProperty, value);

    internal static void Attach(ContextMenu menu)
    {
        menu.Opened += (_, _) => SetKeyboardNavigationActive(menu, false);
        menu.Closed += (_, _) => SetKeyboardNavigationActive(menu, false);
        menu.MouseLeave += (_, _) => SetKeyboardNavigationActive(menu, false);
        menu.PreviewMouseMove += (_, _) => SetKeyboardNavigationActive(menu, false);
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Tab)
            {
                SetKeyboardNavigationActive(menu, true);
            }
        };
    }
}

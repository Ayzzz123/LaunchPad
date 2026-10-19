using System.Windows.Input;
using System.Windows.Interop;

namespace Launchpad.Services;

internal sealed class GlobalHotkeyHost : IDisposable
{
    private const uint Alt = 0x0001;
    private const uint Control = 0x0002;
    private const uint Shift = 0x0004;
    private const uint Windows = 0x0008;
    private const uint NoRepeat = 0x4000;

    private readonly IntPtr _window;
    private readonly HwndSource _source;
    private readonly Action _onPressed;
    private readonly Action _onShow;
    private (uint Modifiers, uint Key)? _registered;

    public GlobalHotkeyHost(IntPtr window, Action onPressed, Action onShow)
    {
        _window = window;
        _onPressed = onPressed;
        _onShow = onShow;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("无法接入窗口快捷键。");
        _source.AddHook(OnWindowMessage);
    }

    public string? TryChange(string binding)
    {
        if (!TryParse(binding, out var next, out var parseError))
        {
            return parseError;
        }

        if (_registered == next)
        {
            return null;
        }

        var previous = _registered;
        if (previous is not null)
        {
            NativeMethods.UnregisterHotKey(_window, NativeMethods.HotkeyId);
            _registered = null;
        }

        if (next is not null && !NativeMethods.RegisterHotKey(_window, NativeMethods.HotkeyId, next.Value.Modifiers | NoRepeat, next.Value.Key))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (previous is not null && NativeMethods.RegisterHotKey(_window, NativeMethods.HotkeyId, previous.Value.Modifiers | NoRepeat, previous.Value.Key))
            {
                _registered = previous;
            }

            return error == 1409
                ? "这个快捷键已被其他程序占用，请改用其他组合键。"
                : $"无法注册快捷键（Windows 错误 {error}）。";
        }

        _registered = next;
        return null;
    }

    public void Dispose()
    {
        _source.RemoveHook(OnWindowMessage);
        NativeMethods.UnregisterHotKey(_window, NativeMethods.HotkeyId);
        _registered = null;
    }

    public static bool TryFormat(KeyEventArgs args, out string binding)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0 || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            binding = "";
            return false;
        }

        parts.Add(key == Key.Space ? "Space" : key.ToString());
        binding = string.Join('+', parts);
        return true;
    }

    private static bool TryParse(string binding, out (uint Modifiers, uint Key)? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(binding))
        {
            return true;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        foreach (var part in binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Control;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Alt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Shift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Windows;
            }
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase))
            {
                virtualKey = 0x20;
            }
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var key) && key != Key.None)
            {
                virtualKey = unchecked((uint)KeyInterop.VirtualKeyFromKey(key));
            }
            else
            {
                error = "无法识别该按键，请重新录入快捷键。";
                return false;
            }
        }

        if (modifiers == 0 || virtualKey == 0)
        {
            error = "快捷键需要至少一个修饰键和一个普通按键。";
            return false;
        }

        parsed = (modifiers, virtualKey);
        return true;
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == NativeMethods.HotkeyId)
        {
            handled = true;
            _onPressed();
        }
        else if (message == NativeMethods.ShowLauncherMessage)
        {
            handled = true;
            _onShow();
        }

        return IntPtr.Zero;
    }
}

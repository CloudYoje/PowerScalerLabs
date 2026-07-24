using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PowerScalerLabs.App.Overlay;

internal sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly HwndSource _source;
    private readonly int _id;
    private bool _disposed;

    private GlobalHotKey(HwndSource source, int id)
    {
        _source = source;
        _id = id;
        _source.AddHook(WndProc);
    }

    internal event EventHandler? Pressed;

    internal static GlobalHotKey Register(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        WindowInteropHelper helper = new(window);
        if (helper.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The PowerScaler Labs window handle is not available yet.");
        }

        HwndSource source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("PowerScaler Labs could not attach its F11 overlay shortcut to the app window.");
        const int id = 0x4A31;
        uint virtualKey = checked((uint)KeyInterop.VirtualKeyFromKey(key));
        uint nativeModifiers = checked((uint)modifiers) | 0x4000u;
        if (!RegisterHotKey(helper.Handle, id, nativeModifiers, virtualKey))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register F11 for the PowerScaler Labs overlay.");
        }

        return new GlobalHotKey(source, id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);
        _ = UnregisterHotKey(_source.Handle, _id);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

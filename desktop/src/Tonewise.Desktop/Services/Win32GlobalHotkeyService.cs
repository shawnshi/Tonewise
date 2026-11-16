using System.Runtime.InteropServices;

namespace Tonewise.Desktop.Services;

/// <summary>
/// Registers OS-wide Win32 global hotkeys (`WM_HOTKEY`) via subclassed HWND WndProc so
/// Play/Pause, Coding instant scene switch, and OLED companion widget toggle work even when minimized.
/// </summary>
public sealed class Win32GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int GwlpWndProc = -4;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = [];
    private readonly WndProcDelegate _wndProcDelegate;
    private IntPtr _prevWndProc;
    private bool _disposed;

    public Win32GlobalHotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = HookedWndProc;
        if (_hwnd != IntPtr.Zero)
        {
            IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _prevWndProc = SetWindowLongPtrW(_hwnd, GwlpWndProc, fnPtr);
        }
    }

    public bool TryRegister(int id, uint modifiers, uint virtualKey, Action handler)
    {
        if (_hwnd == IntPtr.Zero || _disposed) return false;
        if (RegisterHotKey(_hwnd, id, modifiers | ModNoRepeat, virtualKey))
        {
            _handlers[id] = handler;
            return true;
        }
        return false;
    }

    private IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            return IntPtr.Zero;
        }
        return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            foreach (int id in _handlers.Keys)
            {
                UnregisterHotKey(_hwnd, id);
            }
            if (_prevWndProc != IntPtr.Zero)
            {
                SetWindowLongPtrW(_hwnd, GwlpWndProc, _prevWndProc);
                _prevWndProc = IntPtr.Zero;
            }
        }
        _handlers.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

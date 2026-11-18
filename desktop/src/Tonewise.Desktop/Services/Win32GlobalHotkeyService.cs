using System.Runtime.InteropServices;

namespace Tonewise.Desktop.Services;

/// <summary>
/// Registers OS-wide Win32 global hotkeys (`WM_HOTKEY`) via `comctl32.dll!SetWindowSubclass`
/// so Play/Pause, Coding instant scene switch, and OLED companion widget toggle work safely
/// alongside WinUI 3 message hooks without corrupting the WndProc chain.
/// </summary>
public sealed class Win32GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    private static readonly nuint SubclassId = 0x5457_484B;

    private delegate IntPtr SubclassProcDelegate(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        nuint uIdSubclass,
        nuint dwRefData);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = [];
    private readonly SubclassProcDelegate _subclassDelegate;
    private bool _subclassInstalled;
    private bool _disposed;

    public Win32GlobalHotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _subclassDelegate = HookedSubclassProc;
        if (_hwnd != IntPtr.Zero)
        {
            _subclassInstalled = SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, 0);
        }
    }

    public bool TryRegister(int id, uint modifiers, uint virtualKey, Action handler)
    {
        if (_hwnd == IntPtr.Zero || _disposed || !_subclassInstalled) return false;
        if (RegisterHotKey(_hwnd, id, modifiers | ModNoRepeat, virtualKey))
        {
            _handlers[id] = handler;
            return true;
        }
        return false;
    }

    private IntPtr HookedSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        if (uMsg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            return IntPtr.Zero;
        }
        if (uMsg == WmNcDestroy && _subclassInstalled)
        {
            RemoveWindowSubclass(hWnd, _subclassDelegate, uIdSubclass);
            _subclassInstalled = false;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
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
            if (_subclassInstalled)
            {
                RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
                _subclassInstalled = false;
            }
        }
        _handlers.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProcDelegate pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProcDelegate pfnSubclass,
        nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam);
}

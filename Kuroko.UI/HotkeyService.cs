using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kuroko.UI;

public class HotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;

    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const uint VK_S = 0x53;
    public const uint VK_Q = 0x51;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private readonly HashSet<int> _registeredIds = new();

    // Event now returns the ID of the hotkey pressed
    public event EventHandler<int>? HotkeyPressed;

    public void Initialize(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(HwndHook);
    }

    public bool Register(int id, uint modifier, uint key)
    {
        if (_windowHandle == IntPtr.Zero) return false;

        bool success = RegisterHotKey(_windowHandle, id, modifier, key);
        if (success) _registeredIds.Add(id);
        return success;
    }

    public void UnregisterAll()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            foreach (var id in _registeredIds)
            {
                UnregisterHotKey(_windowHandle, id);
            }
            _registeredIds.Clear();
            _source?.RemoveHook(HwndHook);
            _source = null;
            _windowHandle = IntPtr.Zero;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredIds.Contains(id))
            {
                HotkeyPressed?.Invoke(this, id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        GC.SuppressFinalize(this);
    }
}
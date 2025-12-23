using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kuroko.UI;

public class HotkeyService : IDisposable
{
    private const int HOTKEY_ID = 9000;
    private const uint WM_HOTKEY = 0x0312;

    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const uint VK_S = 0x53;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle;
    private HwndSource? _source;

    public event EventHandler? HotkeyPressed;

    public bool Register(IntPtr windowHandle, uint modifier = MOD_ALT, uint key = VK_S)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(HwndHook);

        return RegisterHotKey(_windowHandle, HOTKEY_ID, modifier, key);
    }

    public void Unregister()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            _source = null;
            _windowHandle = IntPtr.Zero;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        GC.SuppressFinalize(this);
    }
}
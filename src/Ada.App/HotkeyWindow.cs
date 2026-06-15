using System.Runtime.InteropServices;

namespace Ada.App;

[Flags]
internal enum ModifierKeys : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A message-only window that owns a system-wide hotkey (<c>RegisterHotKey</c>) and raises an event
/// when it fires. This is how Ada is summoned from anywhere, without stealing focus or a taskbar slot.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x0ADA;

    public event EventHandler? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(ModifierKeys modifiers, Keys key)
        => RegisterHotKey(Handle, HotkeyId, (uint)modifiers, (uint)key);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

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
/// A message-only window that owns system-wide hotkeys (<c>RegisterHotKey</c>) and raises an event,
/// with the hotkey id, when one fires. This is how Ada is summoned (Ctrl+Alt+A) and how Voice Mode is
/// toggled (Ctrl+Alt+Space) from anywhere.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private int _nextId = 0x0ADA;
    private readonly List<int> _ids = [];

    public event Action<int>? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public int Register(ModifierKeys modifiers, Keys key)
    {
        var id = _nextId++;
        if (RegisterHotKey(Handle, id, (uint)modifiers, (uint)key))
            _ids.Add(id);
        return id;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _ids) UnregisterHotKey(Handle, id);
        _ids.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

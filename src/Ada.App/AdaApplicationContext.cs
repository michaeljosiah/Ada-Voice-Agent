namespace Ada.App;

/// <summary>
/// Ada's tray-resident lifetime. The app starts hidden — just a tray icon and a system-wide
/// hotkey (Ctrl+Alt+A). The window is created lazily the first time Ada is summoned, and toggled
/// thereafter. Quitting from the tray is the only thing that ends the process.
/// </summary>
internal sealed class AdaApplicationContext : ApplicationContext
{
    private readonly string _url;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private int _openHotkeyId;
    private int _voiceHotkeyId;
    private MainForm? _form;

    public AdaApplicationContext(string url)
    {
        _url = url;

        _tray = new NotifyIcon
        {
            Text = "Ada — On your machine. In your voice.",
            Visible = true,
            Icon = AppIcon.Create(),
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ToggleWindow();

        _hotkeys = new HotkeyWindow();
        _hotkeys.HotkeyPressed += OnHotkey;
        _openHotkeyId = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Keys.A);
        _voiceHotkeyId = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Keys.Space);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Ada\tCtrl+Alt+A", null, (_, _) => ShowWindow());
        // Voice mode: summon Ada centred and start listening immediately. (Will move to a dedicated
        // compact voice-only HUD window once that surface is built; for now it drives the main window.)
        menu.Items.Add("Voice mode\tCtrl+Alt+Space", null, (_, _) => ToggleVoice());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Ada", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnHotkey(int id)
    {
        if (id == _openHotkeyId) ToggleWindow();
        else if (id == _voiceHotkeyId) ToggleVoice();
    }

    private void ToggleWindow()
    {
        if (_form is { Visible: true })
            HideWindow();
        else
            ShowWindow();
    }

    private void ToggleVoice()
    {
        ShowWindow();
        _form?.ToggleVoice();
    }

    private void ShowWindow()
    {
        _form ??= CreateForm();
        _form.CenterOnScreen();   // summon to the middle of the screen, every time
        _form.Show();
        _form.Activate();
    }

    private void HideWindow() => _form?.Hide();

    private MainForm CreateForm()
    {
        var form = new MainForm(_url);
        form.RequestHide += (_, _) => HideWindow();
        return form;
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        _form?.Dispose();
        base.ExitThreadCore();
    }
}

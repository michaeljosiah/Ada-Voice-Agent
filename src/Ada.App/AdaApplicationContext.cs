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
    private VoiceForm? _voice;

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
        menu.Items.Add("Open Ada\tCtrl+Alt+A", null, (_, _) => OpenConversation());
        // Voice mode: the compact /voiceui widget (a small SuperWhisper-style bar that auto-listens).
        menu.Items.Add("Voice mode\tCtrl+Alt+Space", null, (_, _) => ShowVoice());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Ada", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnHotkey(int id)
    {
        if (id == _openHotkeyId) ToggleWindow();
        else if (id == _voiceHotkeyId) ToggleVoiceMode();
    }

    private void ToggleWindow()
    {
        if (_form is { Visible: true })
            HideWindow();
        else
            OpenConversation();
    }

    private void OpenConversation()
    {
        ShowWindow();               // creates + centres + shows the window
        _form?.ShowConversation();  // land on the chat surface, not whatever view was last open
    }

    // ---- Voice mode: the compact /voiceui widget in its own small window ----

    private void ToggleVoiceMode()
    {
        if (_voice is { Visible: true }) HideVoice();
        else ShowVoice();
    }

    private void ShowVoice()
    {
        var fresh = _voice is null;
        _voice ??= CreateVoiceForm();
        if (_voice.WindowState == FormWindowState.Minimized) _voice.WindowState = FormWindowState.Normal;
        _voice.CenterOnScreen();
        _voice.Show();
        _voice.Activate();
        if (!fresh) _voice.StartListening();   // first load auto-listens; re-arm on a later summon
    }

    private void HideVoice()
    {
        _voice?.StopListening();   // release the mic when dismissed
        _voice?.Hide();
    }

    private VoiceForm CreateVoiceForm()
    {
        var v = new VoiceForm(_url);
        v.RequestHide += (_, _) => HideVoice();
        v.RequestExpand += (_, _) => { HideVoice(); OpenConversation(); };   // expand → full chat window
        return v;
    }

    private void OpenSettings()
    {
        ShowWindow();           // creates + centres + shows the window
        _form?.ShowSettings();  // then navigates the page to the Settings surface
    }

    private void ShowWindow()
    {
        _form ??= CreateForm();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
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
        _voice?.Dispose();
        base.ExitThreadCore();
    }
}

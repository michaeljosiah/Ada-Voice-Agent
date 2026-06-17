using Microsoft.Web.WebView2.WinForms;

namespace Ada.App;

/// <summary>
/// Ada's window: a frameless, taskbar-less panel anchored near the tray that hosts a WebView2
/// pointed at the loopback chat UI. Escape or the hotkey hides it; it never appears in the taskbar
/// and losing focus does not kill it (the tray keeps Ada alive).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly string _url;
    private readonly WebView2 _web;
    private readonly List<string> _pendingJs = new();
    private bool _loaded;

    public event EventHandler? RequestHide;

    public MainForm(string url)
    {
        _url = url;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(920, 640);
        MinimumSize = new Size(560, 560);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(0xFB, 0xF7, 0xEF);
        KeyPreview = true;

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        Load += async (_, _) => await InitWebViewAsync();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) RequestHide?.Invoke(this, EventArgs.Empty); };

        CenterOnScreen();
    }

    private async Task InitWebViewAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // The frameless chrome's window buttons post messages back to the host.
        core.WebMessageReceived += (_, e) =>
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == "minimise") WindowState = FormWindowState.Minimized;
            else if (msg is "hide" or "close") RequestHide?.Invoke(this, EventArgs.Empty);
        };

        // Flush any tray-requested action (voice, settings) queued before the page finished loading.
        core.NavigationCompleted += (_, _) =>
        {
            _loaded = true;
            foreach (var js in _pendingJs) _ = _web.ExecuteScriptAsync(js);
            _pendingJs.Clear();
        };

        _web.Source = new Uri(_url);
    }

    /// <summary>Toggle Voice Mode in the WebView2 page (push-to-talk hotkey / tray "Voice mode").</summary>
    public void ToggleVoice() => RunOrQueue("window.adaToggleVoice && window.adaToggleVoice();");

    /// <summary>Open the Settings surface in the page (tray "Settings" item).</summary>
    public void ShowSettings() => RunOrQueue("window.adaShowView && window.adaShowView('settings');");

    /// <summary>Run script now if the page is loaded, else queue it until it is (first-summon race).</summary>
    private void RunOrQueue(string js)
    {
        if (_web.CoreWebView2 is not null && _loaded) _ = _web.ExecuteScriptAsync(js);
        else _pendingJs.Add(js);
    }

    /// <summary>Centre the window on the primary screen — Ada is summoned to the middle, not the tray corner.</summary>
    public void CenterOnScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
    }

    /// <summary>Hide instead of closing, so the tray companion persists across summons.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            RequestHide?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnFormClosing(e);
    }
}

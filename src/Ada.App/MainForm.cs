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

    public event EventHandler? RequestHide;

    public MainForm(string url)
    {
        _url = url;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(440, 660);
        MinimumSize = new Size(360, 480);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(0xFB, 0xF7, 0xEF);
        KeyPreview = true;

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        Load += async (_, _) => await InitWebViewAsync();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) RequestHide?.Invoke(this, EventArgs.Empty); };

        PositionNearTray();
    }

    private async Task InitWebViewAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        _web.Source = new Uri(_url);
    }

    private void PositionNearTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
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

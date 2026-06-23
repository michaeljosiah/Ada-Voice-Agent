using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
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
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Size = new Size(Math.Min(1680, wa.Width - 60), Math.Min(1040, wa.Height - 60));
        MinimumSize = new Size(960, 620);
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

    // Borderless but resizable: add the native sizing frame so Windows handles edge/corner resize.
    // The WebView2 fills the client area inside the thin frame, so the grab zone stays hittable.
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_THICKFRAME = 0x00040000;   // sizing border
            const int WS_MINIMIZEBOX = 0x00020000;  // allow minimise (taskbar/snap animations)
            var cp = base.CreateParams;
            cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX;
            return cp;
        }
    }

    // Round the window corners on Windows 11 so the frameless window matches the design's card.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { /* pre-Win11: corners stay square, no harm */ }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 6; // px grab zone along each edge

    // Borderless + WS_THICKFRAME otherwise leaves Windows painting a thin native frame line across the very
    // top of the window (the stray light strip above the header). Eating WM_NCCALCSIZE makes the client area
    // fill the whole window so no native border is drawn.
    //
    // But removing the non-client area also removes the area Windows hit-tests for resize, so WS_THICKFRAME
    // alone no longer makes the window resizable (only dragging via the header's CSS -webkit-app-region works).
    // So re-add resize by hand in WM_NCHITTEST: when the cursor is within ResizeBorder of an edge/corner,
    // report the matching HT* code and Windows performs the drag-resize. The base handler runs first so the
    // header's drag region still yields HTCAPTION and the window buttons stay clickable.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }

        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var lp = (long)m.LParam; // screen coords: x in low word, y in high word
                var p = PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
                bool l = p.X <= ResizeBorder, r = p.X >= ClientSize.Width - ResizeBorder;
                bool t = p.Y <= ResizeBorder, b = p.Y >= ClientSize.Height - ResizeBorder;
                int ht = t && l ? HTTOPLEFT : t && r ? HTTOPRIGHT : b && l ? HTBOTTOMLEFT : b && r ? HTBOTTOMRIGHT
                       : l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT;
                if (ht != HTCLIENT) m.Result = (IntPtr)ht;
            }
            return;
        }

        base.WndProc(ref m);
    }

    private async Task InitWebViewAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsNonClientRegionSupportEnabled = true;   // enables CSS -webkit-app-region: drag (header drag)

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

        // Voice needs the microphone; Ada is local and the user initiated it, so auto-grant (no prompt).
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                e.State = CoreWebView2PermissionState.Allow;
        };

        _web.Source = new Uri(_url);
    }

    /// <summary>Toggle Voice Mode in the WebView2 page (push-to-talk hotkey / tray "Voice mode").</summary>
    public void ToggleVoice() => RunOrQueue("window.adaToggleVoice && window.adaToggleVoice();");

    /// <summary>Stop the in-chat mic (the compact Voice mode window takes over voice).</summary>
    public void StopVoice() => RunOrQueue("window.adaStopVoice && window.adaStopVoice();");

    /// <summary>Open the Settings surface in the page (tray "Settings" item).</summary>
    public void ShowSettings() => RunOrQueue("window.adaShowView && window.adaShowView('settings');");

    /// <summary>Switch the page to the conversation surface (where the voice bar lives).</summary>
    public void ShowConversation() => RunOrQueue("window.adaShowView && window.adaShowView('main');");

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

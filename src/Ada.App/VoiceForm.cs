using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Ada.App;

/// <summary>
/// The compact "Voice mode" widget — a small (~600×182), frameless, rounded, always-on-top window that
/// hosts the /voiceui page (a SuperWhisper-style bar that auto-starts listening). Its expand button
/// posts "open-main" to switch to the full chat window; Esc posts "hide".
/// </summary>
internal sealed class VoiceForm : Form
{
    private readonly string _url;
    private readonly WebView2 _web;
    private readonly List<string> _pendingJs = new();
    private bool _loaded;

    public event EventHandler? RequestHide;
    public event EventHandler? RequestExpand;

    public VoiceForm(string url)
    {
        _url = url;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(600, 182);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        KeyPreview = true;

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        Load += async (_, _) => await InitAsync();
        CenterOnScreen();
        ApplyRoundedRegion();
        SizeChanged += (_, _) => ApplyRoundedRegion();
    }

    public void CenterOnScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
    }

    /// <summary>Ensure the widget is listening (idempotent) — used when re-showing after a stop.</summary>
    public void StartListening() => RunOrQueue("window.adaStartVoice && window.adaStartVoice();");

    /// <summary>Stop the widget's mic + socket (called before hiding so the mic doesn't stay live).</summary>
    public void StopListening() => RunOrQueue("window.adaStopVoice && window.adaStopVoice();");

    private void RunOrQueue(string js)
    {
        if (_web.CoreWebView2 is not null && _loaded) _ = _web.ExecuteScriptAsync(js);
        else _pendingJs.Add(js);
    }

    private async Task InitAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsNonClientRegionSupportEnabled = true; // the widget header is a drag region

        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                e.State = CoreWebView2PermissionState.Allow;
        };
        core.WebMessageReceived += (_, e) =>
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == "open-main") RequestExpand?.Invoke(this, EventArgs.Empty);
            else if (msg is "hide" or "close") RequestHide?.Invoke(this, EventArgs.Empty);
            else if (msg == "minimise") WindowState = FormWindowState.Minimized;
        };
        core.NavigationCompleted += (_, _) =>
        {
            _loaded = true;
            foreach (var js in _pendingJs) _ = _web.ExecuteScriptAsync(js);
            _pendingJs.Clear();
        };

        _web.Source = new Uri(_url.TrimEnd('/') + "/voiceui");
    }

    // Clip the frameless window to a 20px-rounded rectangle so it matches the widget card.
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int we, int he);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    private void ApplyRoundedRegion()
    {
        try
        {
            var h = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 40, 40);
            Region = System.Drawing.Region.FromHrgn(h);
            DeleteObject(h);
        }
        catch { /* rounding is cosmetic */ }
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.Style |= 0x00020000 /* WS_MINIMIZEBOX */; return cp; }
    }

    /// <summary>Hide instead of close, so the widget persists across summons.</summary>
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

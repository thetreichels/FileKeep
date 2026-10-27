using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FileKeep.Gui;

/// <summary>
/// COM-visible bridge exposing native Windows dialogs to the dashboard JavaScript.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class NativeBridge
{
    private readonly Form _owner;

    public NativeBridge(Form owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Shows the native Windows folder picker. Returns the selected path,
    /// or empty string if the user cancelled.
    /// </summary>
    public string PickFolder(string title)
    {
        string result = "";
        // Must run on UI thread
        if (_owner.InvokeRequired)
        {
            _owner.Invoke(() => result = PickFolder(title));
            return result;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = string.IsNullOrEmpty(title) ? "Select a folder" : title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            // Root at This PC so all drives are visible at the top level,
            // not buried under Desktop > user folders.
            RootFolder = Environment.SpecialFolder.MyComputer,
        };

        if (dialog.ShowDialog(_owner) == DialogResult.OK)
            result = dialog.SelectedPath;

        return result;
    }

    /// <summary>
    /// Shows the native Windows save-file dialog. Returns the chosen path,
    /// or empty string if the user cancelled.
    /// </summary>
    public string PickSaveFile(string title, string filter, string defaultName)
    {
        string result = "";
        if (_owner.InvokeRequired)
        {
            _owner.Invoke(() => result = PickSaveFile(title, filter, defaultName));
            return result;
        }

        using var dialog = new SaveFileDialog
        {
            Title = string.IsNullOrEmpty(title) ? "Save as" : title,
            Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
            FileName = defaultName ?? "",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(_owner) == DialogResult.OK)
            result = dialog.FileName;

        return result;
    }

    /// <summary>
    /// Shows the native Windows open-file dialog (multi-select) for adding files
    /// to a Stealth Folder. Returns the selected paths joined by newlines, or
    /// empty string if the user cancelled. A native dialog is used instead of the
    /// Chromium file chooser because the chooser does not reliably open from a
    /// remote/automated session, while this dialog does — the dashboard then
    /// uploads by path (see /api/offline-folders/{folder}/files-by-path).
    /// </summary>
    public string PickFiles(string title)
    {
        string result = "";
        if (_owner.InvokeRequired)
        {
            _owner.Invoke(() => result = PickFiles(title));
            return result;
        }

        using var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrEmpty(title) ? "Add files" : title,
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_owner) == DialogResult.OK)
            result = string.Join("\n", dialog.FileNames);

        return result;
    }

    /// <summary>
    /// Gets whether the tray icon is enabled.
    /// </summary>
    public bool GetTrayEnabled()
    {
        return MainForm.LoadTraySetting();
    }

    /// <summary>
    /// Sets whether the tray icon is enabled.
    /// </summary>
    public void SetTrayEnabled(bool enabled)
    {
        if (_owner is MainForm mainForm)
        {
            if (_owner.InvokeRequired)
                _owner.Invoke(() => mainForm.SetTrayEnabled(enabled));
            else
                mainForm.SetTrayEnabled(enabled);
        }
    }

    /// <summary>
    /// Opens a Stealth Folders DAV URL in the shell (the Use dialog's Open
    /// path). Narrow by design: only absolute http URLs on a loopback host
    /// under /dav/offline/ are accepted, so a page can never talk the host
    /// into opening an arbitrary file, LAN address, or remote URL. Anything
    /// else returns false and the page falls back to copying the address.
    /// </summary>
    public bool OpenDavUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host;
        if (host != "127.0.0.1" && host != "localhost" && host != "::1") return false;
        if (!uri.AbsolutePath.StartsWith("/dav/offline/", StringComparison.Ordinal)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Main FileKeep window: a native WinForms shell hosting the dashboard in WebView2.
/// </summary>
public class MainForm : Form
{
    private readonly WebView2 _webView;

    // P2-L13: set when the form starts closing; the async WebView2
    // init checks it after every await and stops touching controls.
    private bool _closing;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayToggleItem;
    private bool _trayEnabled;
    // P2-L14: resolved on EVERY use, not once at process start — a
    // bind/port change in service.json (with the service restarted)
    // used to leave a running GUI pointing at the old address until
    // the GUI itself was relaunched.
    private static string DashboardUrl => ResolveDashboardUrl();

    /// <summary>
    /// The dashboard address (MU-7): the service lets the bind/port be
    /// changed in service.json, and the GUI used to hardcode the
    /// defaults — after a port change it showed "check the service is
    /// running / reinstall" forever while the healthy service listened
    /// on the new port. Read the same config the service reads; fall
    /// back to the defaults when it's missing or unreadable.
    /// </summary>
    private static string ResolveDashboardUrl()
    {
        const string fallback = "http://127.0.0.1:15789/";
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FileKeep", "service.json");
            if (!File.Exists(path))
                return fallback;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string bind = root.TryGetProperty("dashboardBind", out var b)
                && b.ValueKind == System.Text.Json.JsonValueKind.String
                    ? b.GetString() ?? "127.0.0.1" : "127.0.0.1";
            int port = root.TryGetProperty("dashboardPort", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.Number
                && p.TryGetInt32(out int pv) ? pv : 15789;
            // Wildcard binds aren't dialable — loopback reaches them.
            if (bind is "0.0.0.0" or "::" or "*" or "")
                bind = "127.0.0.1";
            // P2-L14: an IPv6 literal bind (::1, fe80::...) must be
            // bracketed in a URL — unbracketed it is unparseable and
            // the GUI blamed the service ("check it's running").
            if (bind.Contains(':'))
                bind = $"[{bind}]";
            return $"http://{bind}:{port}/";
        }
        catch
        {
            return fallback;
        }
    }

    private static string GuiSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileKeep", "gui-settings.json");

    public MainForm()
    {
        Text = "FileKeep";
        Width = 1280;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        Icon? appIcon = null;
        // Load the branded FileKeep icon from embedded resources.
        // (Falls back to the exe's associated icon, then the system default.)
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("FileKeep.Gui.filekeep.ico");
            if (s is not null)
            {
                appIcon = new Icon(s);
                Icon = appIcon;
            }
        }
        catch { /* icon is cosmetic */ }
        if (appIcon is null)
        {
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (appIcon is not null) Icon = appIcon;
        }

        _trayEnabled = LoadTraySetting();

        // System tray icon (optional): shows while FileKeep is running,
        // double-click or "Open FileKeep" restores the window.
        _trayIcon = new NotifyIcon
        {
            Text = "FileKeep",
            Icon = appIcon ?? SystemIcons.Application,
            Visible = _trayEnabled,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open FileKeep", null, (_, _) => RestoreFromTray());
        _trayToggleItem = new ToolStripMenuItem("Show tray icon", null, (_, _) => ToggleTray());
        _trayToggleItem.Checked = _trayEnabled;
        var minimizeItem = new ToolStripMenuItem("Minimize to tray", null, (_, _) => { WindowState = FormWindowState.Minimized; });
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(minimizeItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_trayToggleItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = trayMenu;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeWebViewAsync();
        Resize += (_, _) =>
        {
            // Minimize to tray instead of the taskbar when the tray icon is on.
            if (_trayEnabled && WindowState == FormWindowState.Minimized)
            {
                Hide();
                _trayIcon.ShowBalloonTip(2000, "FileKeep",
                    "FileKeep is still running. Double-click the tray icon to reopen.",
                    ToolTipIcon.Info);
            }
        };
        FormClosing += (s, e) =>
        {
            // Flag FIRST: an in-flight InitializeWebViewAsync resumes
            // after its awaits and must not touch the WebView while
            // (or after) it is disposed here (P2-L13).
            _closing = true;
            LockRestoreSessionOnClose();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            try { _webView?.Dispose(); } catch { /* closing anyway */ }
        };
    }

    /// <summary>
    /// Locks the restore session when the GUI closes (restore gating,
    /// 2026-10-25): a closed GUI is the end of using FileKeep, and a
    /// reopened GUI must start locked rather than inherit authorization.
    /// Best-effort and never blocks closing: the lock endpoint needs the
    /// page's CSRF token, so it is read back out of the served page
    /// (the meta n-token DashboardHtml embeds) and posted with it. Any
    /// failure — service down, token unreadable — is swallowed; the
    /// 1-hour idle expiry is the backstop (a crash runs no close hook,
    /// so it relies on that backstop too). Minimize-to-tray is not a
    /// close and does not lock: the GUI is still running.
    /// </summary>
    private void LockRestoreSessionOnClose()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string html = http.GetStringAsync(DashboardUrl).GetAwaiter().GetResult();
            const string marker = "<meta name=\"n-token\" content=\"";
            int start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return;
            start += marker.Length;
            int end = html.IndexOf('"', start);
            if (end <= start) return;
            string token = html.Substring(start, end - start);
            using var req = new HttpRequestMessage(HttpMethod.Post, DashboardUrl + "api/restore/lock");
            req.Headers.Add("X-CSRF-Token", token);
            using var _ = http.SendAsync(req).GetAwaiter().GetResult();
        }
        catch { /* closing anyway — idle expiry is the backstop */ }
    }

    /// <summary>
    /// Applies the Windows text size (Settings → Accessibility → Text
    /// size) to the dashboard. WebView2 does not pick the scale up by
    /// itself, so the host reads the percentage the slider maintains
    /// (HKCU\Software\Microsoft\Accessibility\TextScaleFactor) and sets
    /// it as the WebView zoom. The page's nav is content-sized, so the
    /// sidebar grows and shrinks with the text like everything else
    /// (Joel, 2026-10-22). Best-effort: any failure leaves the zoom at
    /// 100% and never blocks startup.
    /// </summary>
    private void ApplyTextScaleZoom()
    {
        try
        {
            double factor = 1.0;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int pct && pct >= 50 && pct <= 300)
                factor = pct / 100.0;
            if (!_closing && !IsDisposed)
                _webView.ZoomFactor = factor;
        }
        catch { /* cosmetic — ignore */ }
    }

    private void OnUserPreferenceChanged(
        object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // Fires on a system thread when system preferences change (a
        // text-size change included); marshal back to the UI thread.
        try
        {
            if (!IsDisposed)
                BeginInvoke(new Action(ApplyTextScaleZoom));
        }
        catch { /* form closing */ }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ToggleTray()
    {
        SetTrayEnabled(!_trayEnabled);
    }

    public void SetTrayEnabled(bool enabled)
    {
        _trayEnabled = enabled;
        _trayToggleItem.Checked = _trayEnabled;
        _trayIcon.Visible = _trayEnabled;
        SaveTraySetting(_trayEnabled);
        if (!_trayEnabled && !Visible)
            RestoreFromTray();
    }

    private void ExitApp()
    {
        Close();
    }

    public static bool LoadTraySetting()
    {
        try
        {
            if (File.Exists(GuiSettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(GuiSettingsPath));
                if (doc.RootElement.TryGetProperty("trayIcon", out var v) &&
                    v.ValueKind == JsonValueKind.True)
                    return true;
                if (doc.RootElement.TryGetProperty("trayIcon", out var v2) &&
                    v2.ValueKind == JsonValueKind.False)
                    return false;
            }
        }
        catch { /* corrupt settings; fall through to default */ }
        return true; // default: tray icon on
    }

    private static void SaveTraySetting(bool enabled)
    {
        try
        {
            var dir = Path.GetDirectoryName(GuiSettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(GuiSettingsPath,
                JsonSerializer.Serialize(new GuiSettings(enabled), GuiJsonContext.Default.GuiSettings));
        }
        catch { /* settings are best-effort */ }
    }

    /// <summary>
    /// Warns when the GUI and the FileKeep service report different versions
    /// (they ship in one installer, so a mismatch means mixed binaries).
    /// Best-effort: skipped silently when the service isn't reachable yet.
    /// </summary>
    private async Task CheckServiceVersionAsync()
    {
        try
        {
            string? guiVersion = null;
            try
            {
                string? path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    guiVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0].Trim();
            }
            catch { }
            guiVersion ??= System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0].Trim();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string json = await http.GetStringAsync(DashboardUrl + "api/version");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string? svcVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!string.IsNullOrWhiteSpace(guiVersion) && !string.IsNullOrWhiteSpace(svcVersion) &&
                !string.Equals(guiVersion, svcVersion, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"Version mismatch: this FileKeep window is {guiVersion}, but the FileKeep service is {svcVersion}.\n\n" +
                    "They ship together — reinstall FileKeep so both match.",
                    "FileKeep — version mismatch",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch
        {
            // Service not up yet; the WebView init below reports that case.
        }
    }

    private async Task InitializeWebViewAsync()
    {
        await CheckServiceVersionAsync();
        try
        {
            // WebView2 needs a writable user data folder. The default is next
            // to the executable (Program Files — not writable), which fails
            // with E_ACCESSDENIED. Use LocalAppData instead.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileKeep", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
            // P2-L13: the form may have closed while the runtime was
            // initializing — the WebView is disposed by then and every
            // touch below throws ObjectDisposedException on a dead form.
            if (_closing || IsDisposed)
                return;

            ApplyTextScaleZoom();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // Expose native dialogs to JavaScript as window.chrome.webview.hostObjects.fileKeep
            _webView.CoreWebView2.AddHostObjectToScript("fileKeep", new NativeBridge(this));

            // Inject a helper so dashboard JS can call: await window.fileKeepPickFolder('title')
            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.fileKeepPickFolder = async function(title) {
                    try {
                        const bridge = chrome.webview.hostObjects.fileKeep;
                        const path = await bridge.PickFolder(title || 'Select a folder');
                        return path || '';
                    } catch (e) { return ''; }
                };
                window.fileKeepPickSaveFile = async function(title, filter, defaultName) {
                    try {
                        const bridge = chrome.webview.hostObjects.fileKeep;
                        const path = await bridge.PickSaveFile(title || 'Save as', filter || '', defaultName || '');
                        return path || '';
                    } catch (e) { return ''; }
                };
                window.fileKeepPickFiles = async function(title) {
                    try {
                        const bridge = chrome.webview.hostObjects.fileKeep;
                        const joined = await bridge.PickFiles(title || 'Add files');
                        return (joined || '').split('\n').filter(p => p);
                    } catch (e) { return []; }
                };
                window.fileKeepOpenDav = async function(url) {
                    try {
                        const bridge = chrome.webview.hostObjects.fileKeep;
                        return await bridge.OpenDavUrl(url || '');
                    } catch (e) { return false; }
                };
                // Signal to the dashboard that native dialogs are available
                window.fileKeepNative = true;
            ");

            // Show a helpful error if the dashboard isn't reachable (instead of a blank page).
            _webView.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    _webView.CoreWebView2.NavigateToString(@"
                        <html><head><style>
                        body { font-family: 'Segoe UI', sans-serif; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; background: #f5f5f5; }
                        .box { text-align: center; padding: 40px; background: white; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); max-width: 450px; }
                        h1 { color: #c00; font-size: 20px; margin-bottom: 12px; }
                        p { color: #555; font-size: 14px; line-height: 1.5; }
                        @media (prefers-color-scheme: dark) {
                          body { background: #202020; }
                          .box { background: #2b2b2b; box-shadow: 0 2px 8px rgba(0,0,0,0.4); }
                          h1 { color: #f1707b; }
                          p { color: #a7a7a7; }
                        }
                        </style></head><body><div class='box'>
                        <h1>Cannot reach the FileKeep service</h1>
                        <p>The dashboard is not responding. Please check that the <b>FileKeep Service</b> is running (Services app), then close and reopen this window.</p>
                        <p style='font-size:12px;color:#999'>If the problem persists, the service may need to be reinstalled.</p>
                        </div></body></html>");
                }
            };

            _webView.CoreWebView2.Navigate(DashboardUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "FileKeep needs the Microsoft Edge WebView2 runtime, which isn't installed on this PC.\n\n" +
                "Install it free from Microsoft, then start FileKeep again:\n" +
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "FileKeep — WebView2 required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start the FileKeep interface.\n\nIs the FileKeep service running?\n\n{ex.Message}",
                "FileKeep",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

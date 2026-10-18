using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UsenetBackup.Gui;

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
        };

        if (dialog.ShowDialog(_owner) == DialogResult.OK)
            result = dialog.SelectedPath;

        return result;
    }

    /// <summary>
    /// Shows the native Windows file picker. Returns the selected file path,
    /// or empty string if the user cancelled.
    /// </summary>
    public string PickFile(string title, string filter)
    {
        string result = "";
        if (_owner.InvokeRequired)
        {
            _owner.Invoke(() => result = PickFile(title, filter));
            return result;
        }

        using var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrEmpty(title) ? "Select a file" : title,
            Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_owner) == DialogResult.OK)
            result = dialog.FileName;

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
}

/// <summary>
/// Main FileKeep window: a native WinForms shell hosting the dashboard in WebView2.
/// </summary>
public class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayToggleItem;
    private bool _trayEnabled;
    private const string DashboardUrl = "http://127.0.0.1:15789/";

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
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _webView?.Dispose();
        };
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

    private async Task InitializeWebViewAsync()
    {
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
                window.fileKeepPickFile = async function(title, filter) {
                    try {
                        const bridge = chrome.webview.hostObjects.fileKeep;
                        const path = await bridge.PickFile(title || 'Select a file', filter || '');
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

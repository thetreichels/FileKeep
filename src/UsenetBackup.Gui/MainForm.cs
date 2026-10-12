using System.Runtime.InteropServices;
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
}

/// <summary>
/// Main FileKeep window: a native WinForms shell hosting the dashboard in WebView2.
/// </summary>
public class MainForm : Form
{
    private readonly WebView2 _webView;
    private const string DashboardUrl = "http://127.0.0.1:15789/";

    public MainForm()
    {
        Text = "FileKeep";
        Width = 1280;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        // Try to set the FileKeep icon if available
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "filekeep.ico");
            if (File.Exists(iconPath))
                Icon = new Icon(iconPath);
        }
        catch { /* icon is cosmetic */ }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosing += (_, _) => _webView?.Dispose();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();

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
                // Signal to the dashboard that native dialogs are available
                window.fileKeepNative = true;
            ");

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

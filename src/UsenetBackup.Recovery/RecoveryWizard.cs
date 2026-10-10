using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Recovery;

namespace UsenetBackup.Recovery;

/// <summary>
/// USB recovery wizard: repo location → credentials → select backup →
/// download → verify → restore. Thin UI over <see cref="WizardState"/>.
/// </summary>
public sealed class RecoveryWizard : Form
{
    private readonly WizardState _state = new();

    private readonly TabControl _tabs = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Label _status = new();
    private readonly Label _headerTitle = new();
    private readonly Label _headerDesc = new();

    // Per-page header text (title, description), classic wizard style.
    private static readonly (string Title, string Desc)[] PageHeaders = new[]
    {
        ("Where is the backup data?",
         "Select the folder with repo.json, manifests, and catalog.db — usually on this USB stick."),
        ("Unlock the repository",
         "Enter your passphrase and Usenet credentials. They stay in memory and are never saved."),
        ("Choose a backup",
         "Pick the backup to restore. You can check Usenet for backups newer than this stick."),
        ("Download from Usenet",
         "Fetch the backup's chunks. Every chunk is verified as it arrives."),
        ("Verify",
         "Check every chunk before restoring. Do not skip this step."),
        ("Restore",
         "Restore files to a folder, or write a disk image to a drive."),
    };

    // Page 1: repo
    private readonly TextBox _repoPath = new();
    private readonly Label _repoError = new();

    // Page 2: credentials
    private readonly TextBox _passphrase = new();
    private readonly TextBox _nntpHost = new();
    private readonly TextBox _nntpPort = new();
    private readonly CheckBox _nntpSsl = new();
    private readonly TextBox _nntpUser = new();
    private readonly TextBox _nntpPassword = new();
    private readonly TextBox _newsgroup = new();

    // Page 3: select backup
    private readonly ListBox _backupList = new();
    private readonly Label _backupDetail = new();
    private readonly Button _checkRemote = new();
    private readonly Button _checkLan = new();
    private readonly TextBox _lanServer = new();
    private readonly Button _checkSmb = new();
    private readonly TextBox _smbShare = new();
    private readonly TextBox _smbUser = new();
    private readonly TextBox _smbPassword = new();

    // Page 4: download
    private readonly TextBox _nzbPath = new();
    private readonly ProgressBar _dlProgress = new();
    private readonly TextBox _dlLog = new();
    private readonly Button _dlStart = new();

    // Page 5: verify
    private readonly TextBox _verifyResult = new();
    private readonly Button _verifyStart = new();

    // Page 6: restore
    private readonly RadioButton _restoreFiles = new();
    private readonly RadioButton _restoreDisk = new();
    private readonly TextBox _restoreDest = new();
    private readonly ComboBox _drivePicker = new();
    private readonly Label _driveDetail = new();
    private readonly ProgressBar _restoreProgress = new();
    private readonly TextBox _restoreLog = new();
    private readonly Button _restoreStart = new();
    private readonly IDriveEnumerator _drives = new WmiDriveEnumerator();

    // Win95 shell controls (only used when win95 is true).
    private readonly bool _win95;
    private Panel? _dialog;
    private Panel? _titleBar;
    private ListBox? _steps;

    public RecoveryWizard(bool win95 = false)
    {
        _win95 = win95;
        Text = "FileKeep — USB Recovery";

        if (win95)
            BuildWin95Shell();
        else
            BuildModernShell();

        // Shared tab configuration and pages.
        _tabs.Dock = DockStyle.Fill;
        _tabs.Appearance = TabAppearance.FlatButtons;
        _tabs.ItemSize = new Size(0, 1);
        _tabs.SizeMode = TabSizeMode.Fixed;

        BuildRepoPage();
        BuildCredsPage();
        BuildSelectPage();
        BuildDownloadPage();
        BuildVerifyPage();
        BuildRestorePage();

        UpdateNav();
    }

    /// <summary>Modern native Windows shell: Segoe UI, white wizard header banner.</summary>
    private void BuildModernShell()
    {
        Size = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        // Native Windows look: Segoe UI is the system font since Vista.
        Font = new Font("Segoe UI", 9f);

        // Wizard header banner (white, like Windows Setup): title + description.
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
        _headerTitle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _headerTitle.Location = new Point(16, 8);
        _headerTitle.AutoSize = true;
        _headerDesc.Location = new Point(16, 34);
        _headerDesc.AutoSize = true;
        _headerDesc.ForeColor = SystemColors.GrayText;
        header.Controls.Add(_headerTitle);
        header.Controls.Add(_headerDesc);
        var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };
        var nav = new Panel { Dock = DockStyle.Bottom, Height = 56 };

        _status.Dock = DockStyle.Left;
        _status.AutoSize = true;
        _status.Padding = new Padding(12, 18, 0, 0);
        nav.Controls.Add(_status);

        _back.Text = "< Back";
        _back.Size = new Size(90, 32);
        _back.Location = new Point(430, 12);
        _back.Click += (_, _) => MovePage(-1);
        nav.Controls.Add(_back);

        _next.Text = "Next >";
        _next.Size = new Size(90, 32);
        _next.Location = new Point(528, 12);
        _next.Click += (_, _) => MovePage(1);
        nav.Controls.Add(_next);

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_tabs);

        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(headerLine);
        Controls.Add(header);
    }

    /// <summary>
    /// Windows 95 Setup shell: full-screen teal desktop, navy gradient title bar,
    /// centered classic 3D dialog with the step list on the left.
    /// Visual styles are disabled app-wide in this mode (see Program.cs), so all
    /// standard controls render in the authentic classic style automatically.
    /// </summary>
    private void BuildWin95Shell()
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        BackColor = Win95Theme.Teal;
        Font = Win95Theme.UiFont;

        _titleBar = new Panel { Dock = DockStyle.Top, Height = 26 };
        _titleBar.Paint += TitleBar_Paint;
        _titleBar.MouseClick += TitleBar_MouseClick;
        Controls.Add(_titleBar);

        _dialog = new Panel
        {
            Size = new Size(700, 500),
            BackColor = Win95Theme.Face,
        };
        _dialog.Paint += (_, e) =>
            ControlPaint.DrawBorder3D(e.Graphics, _dialog!.ClientRectangle, Border3DStyle.Raised);
        Controls.Add(_dialog);
        Resize += (_, _) => CenterDialog();

        // Left: dark step list, like Win95 Setup's "steps" pane.
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 168,
            BackColor = Win95Theme.Navy,
        };
        var sideTitle = new Label
        {
            Text = "Recovery steps",
            Font = Win95Theme.TitleFont,
            ForeColor = Win95Theme.SidebarText,
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };
        sidebar.Controls.Add(sideTitle);

        _steps = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
            BackColor = Win95Theme.Navy,
            ForeColor = Win95Theme.SidebarText,
            BorderStyle = BorderStyle.None,
            Font = Win95Theme.UiFont,
            SelectionMode = SelectionMode.None,
        };
        foreach (var (title, _) in PageHeaders)
            _steps.Items.Add(title);
        _steps.DrawItem += Steps_DrawItem;
        sidebar.Controls.Add(_steps);
        // Keep the title above the list.
        sidebar.Controls.SetChildIndex(sideTitle, 0);
        _dialog.Controls.Add(sidebar);

        // Bottom nav: status left, classic buttons right.
        var nav = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Win95Theme.Face };
        _status.Dock = DockStyle.Left;
        _status.AutoSize = true;
        _status.Padding = new Padding(12, 16, 0, 0);
        nav.Controls.Add(_status);

        var cancel = new Button { Text = "Cancel", Size = new Size(80, 26) };
        cancel.Click += (_, _) => Close();
        var nextX = _dialog.Width - 12;
        _next.Text = "Next >";
        _next.Size = new Size(80, 26);
        _next.Click += (_, _) => MovePage(1);
        _back.Text = "< Back";
        _back.Size = new Size(80, 26);
        _back.Click += (_, _) => MovePage(-1);
        // Right-aligned, classic Win95 order: Back, Next, Cancel.
        nextX -= cancel.Width; cancel.Location = new Point(nextX, 11);
        nextX -= _next.Width + 8; _next.Location = new Point(nextX, 11);
        nextX -= _back.Width + 8; _back.Location = new Point(nextX, 11);
        nav.Controls.Add(cancel);
        nav.Controls.Add(_next);
        nav.Controls.Add(_back);
        _dialog.Controls.Add(nav);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Win95Theme.Face };
        content.Controls.Add(_tabs);
        _dialog.Controls.Add(content);

        CenterDialog();
    }

    private void CenterDialog()
    {
        if (_dialog is null || _titleBar is null)
            return;
        int areaH = ClientSize.Height - _titleBar.Height;
        _dialog.Location = new Point(
            Math.Max(0, (ClientSize.Width - _dialog.Width) / 2),
            _titleBar.Height + Math.Max(0, (areaH - _dialog.Height) / 2));
    }

    private void TitleBar_Paint(object? sender, PaintEventArgs e)
    {
        var bar = _titleBar!;
        Win95Theme.PaintTitleBar(e.Graphics, bar.ClientRectangle, Text);
        Win95Theme.PaintCloseButton(e.Graphics, Win95Theme.CloseButtonBounds(bar.ClientRectangle));
    }

    private void TitleBar_MouseClick(object? sender, MouseEventArgs e)
    {
        var bar = _titleBar!;
        if (Win95Theme.CloseButtonBounds(bar.ClientRectangle).Contains(e.Location))
            Close();
    }

    private void Steps_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        var lb = (ListBox)sender!;
        string text = lb.Items[e.Index].ToString()!;
        bool current = e.Index == _tabs.SelectedIndex;
        bool done = e.Index < _tabs.SelectedIndex;

        e.Graphics.FillRectangle(
            new SolidBrush(current ? Win95Theme.TitleEnd : Win95Theme.Navy), e.Bounds);
        var color = current || done ? Win95Theme.SidebarText : Win95Theme.SidebarDim;
        var font = current ? Win95Theme.TitleFont : Win95Theme.UiFont;
        string prefix = done ? "✓ " : current ? "> " : "   ";
        TextRenderer.DrawText(e.Graphics, prefix + text, font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
            color, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _state.Dispose();
        base.Dispose(disposing);
    }

    private TabPage AddPage(string title)
    {
        var p = new TabPage(title) { Padding = new Padding(16) };
        _tabs.TabPages.Add(p);
        return p;
    }

    private void MovePage(int dir)
    {
        int next = _tabs.SelectedIndex + dir;
        if (next < 0 || next >= _tabs.TabPages.Count)
            return;
        // Validate current page before advancing.
        if (dir > 0 && !ValidatePage(_tabs.SelectedIndex))
            return;
        _tabs.SelectedIndex = next;
        OnPageEnter(next);
        UpdateNav();
    }

    private void UpdateNav()
    {
        _back.Enabled = _tabs.SelectedIndex > 0;
        _next.Enabled = _tabs.SelectedIndex < _tabs.TabPages.Count - 1;
        _next.Text = _tabs.SelectedIndex == _tabs.TabPages.Count - 2 ? "Finish" : "Next >";
        if (_win95)
        {
            _steps?.Invalidate();
        }
        else
        {
            var (title, desc) = PageHeaders[_tabs.SelectedIndex];
            _headerTitle.Text = title;
            _headerDesc.Text = desc;
        }
    }

    private void SetStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.ForeColor = isError ? Color.DarkRed : SystemColors.ControlText;
    }

    // ---------- Page 1: repo location ----------

    private void BuildRepoPage()
    {
        var p = AddPage("Repository");
        var lbl = new Label
        {
            Text = "Where is the repo metadata?\n(repo.json, manifests/, catalog.db — from your USB stick or a network share)",
            AutoSize = true, Location = new Point(16, 16)
        };
        p.Controls.Add(lbl);

        _repoPath.Location = new Point(16, 64);
        _repoPath.Size = new Size(440, 28);
        p.Controls.Add(_repoPath);

        var browse = new Button { Text = "Browse…", Location = new Point(464, 62), Size = new Size(90, 30) };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Select the folder containing repo.json" };
            if (d.ShowDialog() == DialogResult.OK)
                _repoPath.Text = d.SelectedPath;
        };
        p.Controls.Add(browse);

        _repoError.Location = new Point(16, 104);
        _repoError.AutoSize = true;
        _repoError.ForeColor = Color.DarkRed;
        p.Controls.Add(_repoError);
    }

    // ---------- Page 2: credentials ----------

    private void BuildCredsPage()
    {
        var p = AddPage("Credentials");
        int y = 16;
        void Row(string label, Control c, bool password = false)
        {
            var l = new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true };
            c.Location = new Point(180, y);
            c.Size = new Size(360, 28);
            if (password && c is TextBox t)
                t.UseSystemPasswordChar = true;
            p.Controls.Add(l);
            p.Controls.Add(c);
            y += 40;
        }

        Row("Passphrase:", _passphrase, password: true);
        Row("Usenet host:", _nntpHost);
        Row("Port:", _nntpPort);
        _nntpPort.Text = "119";
        _nntpPort.Size = new Size(80, 28);
        Row("Use SSL:", _nntpSsl);
        _nntpSsl.AutoSize = true;
        Row("Username:", _nntpUser);
        Row("Password:", _nntpPassword, password: true);
        Row("Newsgroup:", _newsgroup);

        var note = new Label
        {
            Text = "Credentials are kept in memory only and never written to disk.",
            Location = new Point(16, y + 8), AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        p.Controls.Add(note);
    }

    // ---------- Page 3: select backup ----------

    private void BuildSelectPage()
    {
        var p = AddPage("Select backup");
        var lbl = new Label { Text = "Choose the backup to restore:", AutoSize = true, Location = new Point(16, 16) };
        p.Controls.Add(lbl);

        _backupList.Location = new Point(16, 44);
        _backupList.Size = new Size(560, 220);
        p.Controls.Add(_backupList);

        _backupDetail.Location = new Point(16, 276);
        _backupDetail.Size = new Size(560, 60);
        p.Controls.Add(_backupDetail);

        _checkRemote.Text = "Check for newer backups on Usenet…";
        _checkRemote.Location = new Point(16, 344);
        _checkRemote.Size = new Size(280, 32);
        _checkRemote.Click += CheckRemote_Click;
        p.Controls.Add(_checkRemote);

        // LAN server input and check button
        var lanLabel = new Label { Text = "LAN server (e.g., 192.168.1.10:8477):", Location = new Point(310, 348), Size = new Size(200, 20) };
        p.Controls.Add(lanLabel);
        _lanServer.Location = new Point(310, 368);
        _lanServer.Size = new Size(150, 24);
        _lanServer.PlaceholderText = "192.168.1.10:8477";
        p.Controls.Add(_lanServer);
        _checkLan.Text = "Check LAN…";
        _checkLan.Location = new Point(470, 366);
        _checkLan.Size = new Size(106, 28);
        _checkLan.Click += CheckLan_Click;
        p.Controls.Add(_checkLan);

        // SMB share input and check button
        var smbLabel = new Label { Text = "SMB share (e.g., \\\\NAS\\backups):", Location = new Point(16, 404), Size = new Size(200, 20) };
        p.Controls.Add(smbLabel);
        _smbShare.Location = new Point(16, 424);
        _smbShare.Size = new Size(180, 24);
        _smbShare.PlaceholderText = "\\\\server\\share";
        p.Controls.Add(_smbShare);
        _smbUser.Location = new Point(204, 424);
        _smbUser.Size = new Size(110, 24);
        _smbUser.PlaceholderText = "Username";
        p.Controls.Add(_smbUser);
        _smbPassword.Location = new Point(322, 424);
        _smbPassword.Size = new Size(110, 24);
        _smbPassword.PlaceholderText = "Password";
        _smbPassword.UseSystemPasswordChar = true;
        p.Controls.Add(_smbPassword);
        _checkSmb.Text = "Check share…";
        _checkSmb.Location = new Point(440, 422);
        _checkSmb.Size = new Size(136, 28);
        _checkSmb.Click += CheckSmb_Click;
        p.Controls.Add(_checkSmb);

        _backupList.SelectedIndexChanged += (_, _) =>
        {
            if (_backupList.SelectedItem is BackupSummaryView b)
            {
                _state.SelectedBackupId = b.BackupId;
                _backupDetail.Text = $"ID: {b.BackupId}\nType: {b.Type}  Source: {b.Source}\nTaken: {b.CreatedUtc:u}";
            }
        };
    }

    private sealed record BackupSummaryView(string BackupId, string Type, string Source, DateTime CreatedUtc)
    {
        public override string ToString() => $"{CreatedUtc:u}  [{Type}]  {BackupId[..8]}… ({Source})";
    }

    private async void CheckRemote_Click(object? sender, EventArgs e)
    {
        _checkRemote.Enabled = false;
        try
        {
            var found = await Task.Run(() =>
            {
                var client = new NntpClient(_state.NntpHost, _state.NntpPort, _state.NntpSsl);
                try
                {
                    client.Connect();
                    if (!string.IsNullOrEmpty(_state.NntpUser))
                        client.Authenticate(_state.NntpUser, _state.NntpPassword);
                    var repo = _state.OpenRepo();
                    using var store = new NntpBlobStore(
                        client, _state.Newsgroup, repo.RepoId, repo.CatalogPath, messageIndex: repo.MessageIndex,
                        providerKey: ChunkMessageIndex.MakeProviderKey(_state.NntpHost, _state.Newsgroup));
                    store.MaxArticleBytes = repo.MaxDownloadBytes;
                    return _state.DiscoverRemoteManifests(store);
                }
                finally
                {
                    client.Dispose();
                }
            });
            if (found.Count == 0)
            {
                SetStatus("No backups on Usenet newer than this USB stick.");
            }
            else
            {
                // Save remote manifests locally so download/restore work normally.
                _state.SaveRemoteManifests(found);
                foreach (var m in found)
                {
                    _backupList.Items.Add(new BackupSummaryView(
                        m.BackupId, m.Manifest.Type + " (from Usenet)",
                        m.Manifest.Snapshot ?? "", m.Manifest.CreatedUtc));
                }
                SetStatus($"Found {found.Count} newer backup(s) on Usenet — marked '(from Usenet)'.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Discovery failed: " + ex.Message, isError: true);
        }
        finally
        {
            _checkRemote.Enabled = true;
        }
    }

    private async void CheckLan_Click(object? sender, EventArgs e)
    {
        string server = _lanServer.Text.Trim();
        if (string.IsNullOrEmpty(server))
        {
            SetStatus("Enter a LAN server address (e.g., 192.168.1.10:8477).", isError: true);
            return;
        }
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            server = "http://" + server;

        _checkLan.Enabled = false;
        try
        {
            var found = await Task.Run(() =>
            {
                using var store = new UsenetBackup.Core.Lan.HttpBlobStore(server);
                var ids = store.ListManifestIds();
                var repo = _state.OpenRepo();
                var result = new List<(string BackupId, BackupManifest Manifest)>();
                foreach (string id in ids)
                {
                    string? json = store.GetManifestJson(id);
                    if (json is null) continue;
                    // Check if already local
                    string localPath = Path.Combine(_state.RepoPath, "manifests", id + ".json");
                    if (File.Exists(localPath)) continue;
                    var manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(json);
                    if (manifest is not null)
                        result.Add((id, manifest));
                }
                return result;
            });
            if (found.Count == 0)
            {
                SetStatus("No backups on LAN server newer than this USB stick.");
            }
            else
            {
                // Save LAN manifests locally
                foreach (var (id, manifest) in found)
                {
                    string localPath = Path.Combine(_state.RepoPath, "manifests", id + ".json");
                    // Re-fetch to save (we already have it, but this is simpler)
                    using var store = new UsenetBackup.Core.Lan.HttpBlobStore(server);
                    string? json = store.GetManifestJson(id);
                    if (json is not null)
                        File.WriteAllText(localPath, json);
                    _backupList.Items.Add(new BackupSummaryView(
                        id, manifest.Type + " (from LAN)",
                        manifest.Snapshot ?? "", manifest.CreatedUtc));
                }
                // Store the LAN server for the download phase
                _state.LanServer = server;
                SetStatus($"Found {found.Count} backup(s) on LAN — marked '(from LAN)'.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("LAN discovery failed: " + ex.Message, isError: true);
        }
        finally
        {
            _checkLan.Enabled = true;
        }
    }

    private async void CheckSmb_Click(object? sender, EventArgs e)
    {
        string share = _smbShare.Text.Trim();
        if (string.IsNullOrEmpty(share))
        {
            SetStatus("Enter an SMB share path (e.g., \\\\NAS\\backups).", isError: true);
            return;
        }
        string user = _smbUser.Text.Trim();
        string password = _smbPassword.Text; // transient; never stored

        _checkSmb.Enabled = false;
        try
        {
            var found = await Task.Run(() =>
            {
                using var smb = UsenetBackup.Core.SmbShare.Connect(
                    share,
                    string.IsNullOrEmpty(user) ? null : user,
                    string.IsNullOrEmpty(password) ? null : password);
                string manifestsDir = smb.Combine("manifests");
                var result = new List<(string BackupId, BackupManifest Manifest)>();
                if (!Directory.Exists(manifestsDir))
                    return result;
                foreach (string file in Directory.GetFiles(manifestsDir, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    string localPath = Path.Combine(_state.RepoPath, "manifests", id + ".json");
                    if (File.Exists(localPath)) continue; // already local
                    try
                    {
                        var manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(
                            File.ReadAllText(file));
                        if (manifest is not null)
                            result.Add((id, manifest));
                    }
                    catch { /* skip unreadable manifests */ }
                }
                return result;
            });
            if (found.Count == 0)
            {
                SetStatus("No backups on the SMB share newer than this USB stick.");
            }
            else
            {
                // Save SMB manifests locally
                foreach (var (id, manifest) in found)
                {
                    string localPath = Path.Combine(_state.RepoPath, "manifests", id + ".json");
                    using var smb = UsenetBackup.Core.SmbShare.Connect(
                        share,
                        string.IsNullOrEmpty(user) ? null : user,
                        string.IsNullOrEmpty(password) ? null : password);
                    string remotePath = smb.Combine("manifests", id + ".json");
                    File.Copy(remotePath, localPath, overwrite: true);
                    _backupList.Items.Add(new BackupSummaryView(
                        id, manifest.Type + " (from SMB)",
                        manifest.Snapshot ?? "", manifest.CreatedUtc));
                }
                // Store the SMB share for the chunk-pull phase before restore
                _state.SmbShare = share;
                _state.SmbUser = user;
                _state.SmbPassword = password;
                SetStatus($"Found {found.Count} backup(s) on SMB share — marked '(from SMB)'.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("SMB discovery failed: " + ex.Message, isError: true);
        }
        finally
        {
            _checkSmb.Enabled = true;
            _smbPassword.Text = ""; // never retain the password in the UI
        }
    }

    // ---------- Page 4: download ----------

    private void BuildDownloadPage()
    {
        var p = AddPage("Download");
        var lbl = new Label { Text = "NZB file for the selected backup:", AutoSize = true, Location = new Point(16, 16) };
        p.Controls.Add(lbl);

        _nzbPath.Location = new Point(16, 44);
        _nzbPath.Size = new Size(440, 28);
        p.Controls.Add(_nzbPath);

        var browse = new Button { Text = "Browse…", Location = new Point(464, 42), Size = new Size(90, 30) };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "NZB files (*.nzb)|*.nzb" };
            if (d.ShowDialog() == DialogResult.OK)
                _nzbPath.Text = d.FileName;
        };
        p.Controls.Add(browse);

        _dlStart.Text = "Start download";
        _dlStart.Location = new Point(16, 84);
        _dlStart.Size = new Size(140, 32);
        _dlStart.Click += DlStart_Click;
        p.Controls.Add(_dlStart);

        _dlProgress.Location = new Point(16, 128);
        _dlProgress.Size = new Size(560, 24);
        p.Controls.Add(_dlProgress);

        _dlLog.Location = new Point(16, 164);
        _dlLog.Size = new Size(560, 180);
        _dlLog.Multiline = true;
        _dlLog.ReadOnly = true;
        _dlLog.ScrollBars = ScrollBars.Vertical;
        p.Controls.Add(_dlLog);
    }

    private async void DlStart_Click(object? sender, EventArgs e)
    {
        _dlStart.Enabled = false;
        _dlLog.Clear();
        try
        {
            await Task.Run(() =>
            {
                _state.LoadNzb(_nzbPath.Text);
                var result = _state.Download((done, total) =>
                {
                    Invoke(() =>
                    {
                        _dlProgress.Maximum = total;
                        _dlProgress.Value = Math.Min(done, total);
                        _dlLog.AppendText($"\r{done}/{total} chunks");
                    });
                });
                Invoke(() => _dlLog.AppendText(
                    $"\r\nDone: {result.Downloaded} downloaded, {result.AlreadyPresent} already present.\r\n"));
            });
            SetStatus("Download complete.");
        }
        catch (Exception ex)
        {
            SetStatus("Download failed: " + ex.Message, isError: true);
            _dlLog.AppendText("\r\nERROR: " + ex.Message + "\r\n");
        }
        finally
        {
            _dlStart.Enabled = true;
        }
    }

    // ---------- Page 5: verify ----------

    private void BuildVerifyPage()
    {
        var p = AddPage("Verify");
        var lbl = new Label
        {
            Text = "Verify the backup before restoring. Every chunk is decrypted\nand hash-checked; any mismatch aborts the restore.",
            AutoSize = true, Location = new Point(16, 16)
        };
        p.Controls.Add(lbl);

        _verifyStart.Text = "Run verification";
        _verifyStart.Location = new Point(16, 84);
        _verifyStart.Size = new Size(160, 32);
        _verifyStart.Click += VerifyStart_Click;
        p.Controls.Add(_verifyStart);

        _verifyResult.Location = new Point(16, 128);
        _verifyResult.Size = new Size(560, 220);
        _verifyResult.Multiline = true;
        _verifyResult.ReadOnly = true;
        _verifyResult.ScrollBars = ScrollBars.Vertical;
        p.Controls.Add(_verifyResult);
    }

    private async void VerifyStart_Click(object? sender, EventArgs e)
    {
        _verifyStart.Enabled = false;
        _verifyResult.Clear();
        try
        {
            var issues = await Task.Run(() => _state.Verify());
            if (issues.Count == 0)
            {
                _verifyResult.Text = "OK — all chunks verified.";
                SetStatus("Verification passed.");
            }
            else
            {
                _verifyResult.Lines = issues.ToArray();
                SetStatus($"Verification found {issues.Count} issue(s).", isError: true);
            }
        }
        catch (Exception ex)
        {
            _verifyResult.Text = "ERROR: " + ex.Message;
            SetStatus("Verification failed: " + ex.Message, isError: true);
        }
        finally
        {
            _verifyStart.Enabled = true;
        }
    }

    // ---------- Page 6: restore ----------

    private void BuildRestorePage()
    {
        var p = AddPage("Restore");
        _restoreFiles.Text = "Restore files to a folder";
        _restoreFiles.Location = new Point(16, 16);
        _restoreFiles.AutoSize = true;
        _restoreFiles.Checked = true;
        _restoreFiles.CheckedChanged += (_, _) => UpdateRestoreMode();
        p.Controls.Add(_restoreFiles);

        _restoreDisk.Text = "Restore disk image to a drive (destructive)";
        _restoreDisk.Location = new Point(16, 44);
        _restoreDisk.AutoSize = true;
        _restoreDisk.CheckedChanged += (_, _) => UpdateRestoreMode();
        p.Controls.Add(_restoreDisk);

        var destLbl = new Label { Text = "Destination folder:", AutoSize = true, Location = new Point(16, 80) };
        p.Controls.Add(destLbl);
        _restoreDest.Location = new Point(16, 104);
        _restoreDest.Size = new Size(440, 28);
        p.Controls.Add(_restoreDest);
        var destBrowse = new Button { Text = "Browse…", Location = new Point(464, 102), Size = new Size(90, 30) };
        destBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Select restore destination" };
            if (d.ShowDialog() == DialogResult.OK)
                _restoreDest.Text = d.SelectedPath;
        };
        p.Controls.Add(destBrowse);

        // Drive picker (disk restore mode)
        var driveLbl = new Label { Text = "Target drive:", AutoSize = true, Location = new Point(16, 144) };
        driveLbl.Name = "driveLbl";
        p.Controls.Add(driveLbl);

        _drivePicker.Location = new Point(16, 168);
        _drivePicker.Size = new Size(440, 28);
        _drivePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _drivePicker.SelectedIndexChanged += (_, _) => UpdateDriveDetail();
        p.Controls.Add(_drivePicker);

        var refreshBtn = new Button { Text = "Refresh", Location = new Point(464, 166), Size = new Size(90, 30) };
        refreshBtn.Name = "refreshBtn";
        refreshBtn.Click += (_, _) => LoadDrives();
        p.Controls.Add(refreshBtn);

        _driveDetail.Location = new Point(16, 202);
        _driveDetail.Size = new Size(560, 40);
        _driveDetail.ForeColor = Color.DarkRed;
        p.Controls.Add(_driveDetail);

        _restoreStart.Text = "Start restore";
        _restoreStart.Location = new Point(16, 310);
        _restoreStart.Size = new Size(140, 32);
        _restoreStart.Click += RestoreStart_Click;
        p.Controls.Add(_restoreStart);

        _restoreProgress.Location = new Point(16, 352);
        _restoreProgress.Size = new Size(560, 24);
        p.Controls.Add(_restoreProgress);

        _restoreLog.Location = new Point(16, 384);
        _restoreLog.Size = new Size(560, 40);
        _restoreLog.Multiline = true;
        _restoreLog.ReadOnly = true;
        _restoreLog.ScrollBars = ScrollBars.Vertical;
        p.Controls.Add(_restoreLog);

        UpdateRestoreMode();
    }

    private void LoadDrives()
    {
        _drivePicker.Items.Clear();
        _driveDetail.Text = "";
        try
        {
            var drives = _state.ListPhysicalDrives(_drives);
            foreach (var d in drives)
                _drivePicker.Items.Add(d);
            if (_drivePicker.Items.Count > 0)
                _drivePicker.SelectedIndex = 0;
            SetStatus(drives.Count == 0 ? "No physical drives found." : "");
        }
        catch (Exception ex)
        {
            SetStatus("Cannot list drives: " + ex.Message, isError: true);
        }
    }

    private void UpdateDriveDetail()
    {
        if (_drivePicker.SelectedItem is PhysicalDriveInfo d)
        {
            _driveDetail.Text = $"WILL ERASE: {d.Model}\n{d.DevicePath} — {d.Display}";
        }
        else
        {
            _driveDetail.Text = "";
        }
    }

    private void UpdateRestoreMode()
    {
        bool disk = _restoreDisk.Checked;
        _drivePicker.Enabled = disk;
        _restoreDest.Enabled = !disk;
        // Show/hide drive controls vs folder controls
        foreach (Control c in Controls)
        {
            if (c is TabPage)
                foreach (Control inner in c.Controls)
                {
                    if (inner.Name is "driveLbl" or "refreshBtn")
                        inner.Visible = disk;
                }
        }
        _driveDetail.Visible = disk;
        if (disk && _drivePicker.Items.Count == 0)
            LoadDrives();
    }

    private bool ConfirmDiskRestore(PhysicalDriveInfo drive)
    {
        string body = $"This will permanently erase everything on:\n\n{drive.Model}\n{drive.DevicePath} ({drive.Display})\n\nThis cannot be undone.";
        if (_win95)
        {
            // Classic MessageBox matches the Win95 shell (no visual styles there).
            return MessageBox.Show(this, body, "Erase this drive and restore the disk image?",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }
        // Native Windows TaskDialog for the destructive disk-restore step.
        var page = new TaskDialogPage
        {
            Caption = "Confirm disk restore",
            Heading = "Erase this drive and restore the disk image?",
            Text = body,
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Cancel },
        };
        var confirm = new TaskDialogButton("Erase drive and restore");
        page.Buttons.Add(confirm);
        page.DefaultButton = TaskDialogButton.Cancel;
        return TaskDialog.ShowDialog(this, page) == confirm;
    }

    private async void RestoreStart_Click(object? sender, EventArgs e)
    {
        _restoreStart.Enabled = false;
        _restoreLog.Clear();
        try
        {
            // Progress reporter marshals back to the UI thread.
            void ReportSmb(int done, int total)
            {
                if (total == 0) return;
                BeginInvoke(() =>
                {
                    _restoreProgress.Value = Math.Min(100, done * 100 / total);
                    _restoreLog.Text = $"Copying chunks from SMB share… {done}/{total}";
                });
            }
            if (_restoreFiles.Checked)
            {
                if (string.IsNullOrWhiteSpace(_restoreDest.Text))
                    throw new InvalidOperationException("Choose a destination folder.");
                await Task.Run(() => _state.RestoreFiles(_restoreDest.Text, ReportSmb));
                _restoreLog.Text = "File restore complete.";
            }
            else
            {
                if (_drivePicker.SelectedItem is not PhysicalDriveInfo drive)
                    throw new InvalidOperationException("Select a target drive.");
                // Native Windows confirmation dialog (TaskDialog), not a custom popup.
                if (!ConfirmDiskRestore(drive))
                {
                    _restoreLog.Text = "Restore cancelled.";
                    return;
                }
                await Task.Run(() => _state.RestoreDiskToDrive(drive, ReportSmb));
                _restoreLog.Text = "Disk restore complete. Reboot from the restored drive.";
            }
            SetStatus("Restore complete.");
        }
        catch (Exception ex)
        {
            _restoreLog.Text = "ERROR: " + ex.Message;
            SetStatus("Restore failed: " + ex.Message, isError: true);
        }
        finally
        {
            _restoreStart.Enabled = true;
        }
    }

    // ---------- page validation & entry ----------

    private bool ValidatePage(int index)
    {
        switch (index)
        {
            case 0: // repo
                _state.RepoPath = _repoPath.Text.Trim();
                var (ok, error) = _state.ValidateRepoPath();
                _repoError.Text = ok ? "" : error;
                if (!ok)
                    SetStatus(error, isError: true);
                return ok;

            case 1: // credentials
                _state.Passphrase = _passphrase.Text;
                _state.NntpHost = _nntpHost.Text.Trim();
                _state.NntpUser = _nntpUser.Text.Trim();
                _state.NntpPassword = _nntpPassword.Text;
                _state.Newsgroup = _newsgroup.Text.Trim();
                if (!int.TryParse(_nntpPort.Text, out int port) || port is < 1 or > 65535)
                {
                    SetStatus("Port must be 1–65535.", isError: true);
                    return false;
                }
                _state.NntpPort = port;
                _state.NntpSsl = _nntpSsl.Checked;
                if (string.IsNullOrEmpty(_state.Passphrase))
                {
                    SetStatus("Passphrase is required.", isError: true);
                    return false;
                }
                if (string.IsNullOrEmpty(_state.NntpHost))
                {
                    SetStatus("Usenet host is required.", isError: true);
                    return false;
                }
                try
                {
                    _state.OpenRepo(); // validates repo format
                    var (pwOk, note) = _state.ValidatePassphrase();
                    if (!pwOk)
                    {
                        SetStatus(note, isError: true);
                        return false;
                    }
                    if (note == "unverified")
                        SetStatus("Note: passphrase will be verified when chunks download (no local chunks to check).");
                    else
                        SetStatus("");
                }
                catch (Exception ex)
                {
                    SetStatus("Cannot open repo: " + ex.Message, isError: true);
                    return false;
                }
                SetStatus("");
                return true;

            case 2: // select backup
                if (_state.SelectedBackupId is null)
                {
                    SetStatus("Select a backup.", isError: true);
                    return false;
                }
                SetStatus("");
                return true;

            case 3: // download
                if (string.IsNullOrWhiteSpace(_nzbPath.Text) || !File.Exists(_nzbPath.Text))
                {
                    SetStatus("Choose the NZB file for this backup.", isError: true);
                    return false;
                }
                SetStatus("");
                return true;

            default:
                return true;
        }
    }

    private void OnPageEnter(int index)
    {
        if (index == 2) // select backup: populate list
        {
            _backupList.Items.Clear();
            _backupDetail.Text = "";
            try
            {
                foreach (var b in _state.ListBackups())
                    _backupList.Items.Add(new BackupSummaryView(b.BackupId, b.Type, b.Source, b.CreatedUtc));
                SetStatus(_backupList.Items.Count == 0 ? "No backups found in this repo." : "");
            }
            catch (Exception ex)
            {
                SetStatus("Cannot list backups: " + ex.Message, isError: true);
            }
        }
    }
}

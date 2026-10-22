using UsenetBackup.Core;
using UsenetBackup.Core.Json;
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
         "Pick the backup from the list — we scanned every attached drive. Or browse to its folder."),
        ("Unlock the backup",
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

    // Page 1: backups
    private readonly TextBox _repoPath = new();
    private readonly Label _repoError = new();
    private readonly ListBox _foundList = new();
    private readonly Button _rescanButton = new();
    private List<FoundBackup> _foundBackups = new();

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
    private readonly Button _checkSmb = new();
    private readonly TextBox _smbShare = new();
    private readonly TextBox _smbUser = new();
    private readonly TextBox _smbPassword = new();
    private readonly Button _scanSmb = new();
    private readonly ListBox _smbResults = new();

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
    private readonly Label _policyCaption = new();
    private readonly RadioButton _policySkip = new();
    private readonly RadioButton _policyOverwrite = new();
    private readonly RadioButton _policyNewer = new();
    private readonly Label _policyInfo = new();
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

    /// <summary>
    /// P2-L17: marshals a UI update from a worker thread ONLY if the
    /// form is still alive. The progress callbacks used to call
    /// Invoke/BeginInvoke unguarded — closing the wizard mid-operation
    /// threw ObjectDisposedException INSIDE the worker (aborting a
    /// healthy chunk pull), and the handlers' catches then touched
    /// disposed controls and stacked more error dialogs. A dead form
    /// simply drops the update; the operation itself continues.
    /// </summary>
    private void UiInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* form closing/gone (ObjectDisposedException included) */ }
    }

    private void SetStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.ForeColor = isError ? Color.DarkRed : SystemColors.ControlText;
    }

    // ---------- Page 1: backup location ----------

    /// <summary>A backup folder found by scanning attached drives.</summary>
    private sealed record FoundBackup(string Folder, string Display);

    private void BuildRepoPage()
    {
        var p = AddPage("Backups");
        var lbl = new Label
        {
            Text = "Where are your backups?\nWe checked every attached drive — pick one, or browse to a folder.",
            AutoSize = true, Location = new Point(16, 16)
        };
        p.Controls.Add(lbl);

        _foundList.Location = new Point(16, 64);
        _foundList.Size = new Size(440, 110);
        _foundList.SelectedIndexChanged += (_, _) =>
        {
            if (_foundList.SelectedIndex >= 0 && _foundList.SelectedIndex < _foundBackups.Count)
            {
                _repoPath.Text = _foundBackups[_foundList.SelectedIndex].Folder;
                _repoError.Text = "";
            }
        };
        p.Controls.Add(_foundList);

        _rescanButton.Text = "Rescan";
        _rescanButton.Location = new Point(464, 64);
        _rescanButton.Size = new Size(90, 30);
        _rescanButton.Click += (_, _) => RescanBackups();
        p.Controls.Add(_rescanButton);

        var manualLbl = new Label
        {
            Text = "Or pick a folder directly:",
            AutoSize = true, Location = new Point(16, 184)
        };
        p.Controls.Add(manualLbl);

        _repoPath.Location = new Point(16, 204);
        _repoPath.Size = new Size(440, 28);
        p.Controls.Add(_repoPath);

        var browse = new Button { Text = "Browse…", Location = new Point(464, 202), Size = new Size(90, 30) };
        browse.Click += (_, _) =>
        {
            try
            {
                using var d = new FolderBrowserDialog { Description = "Select the folder containing the backup" };
                if (d.ShowDialog() == DialogResult.OK)
                    _repoPath.Text = d.SelectedPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the folder picker:\n\n" + ex.Message,
                    "FileKeep Recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        p.Controls.Add(browse);

        _repoError.Location = new Point(16, 240);
        _repoError.AutoSize = true;
        _repoError.ForeColor = Color.DarkRed;
        p.Controls.Add(_repoError);

        RescanBackups();
    }

    private void RescanBackups()
    {
        _foundBackups = ScanForBackupFolders();
        _foundList.BeginUpdate();
        _foundList.Items.Clear();
        foreach (var f in _foundBackups)
            _foundList.Items.Add(f.Display);
        _foundList.EndUpdate();
        // Exactly one backup found: select it so the user can just continue.
        _foundList.SelectedIndex = _foundBackups.Count == 1 ? 0 : -1;
    }

    /// <summary>
    /// Scans every ready drive for folders that look like a FileKeep backup
    /// (a directory containing repo.json), checking the drive root and one
    /// level down. The WinRE RAM disk is skipped.
    /// </summary>
    private static List<FoundBackup> ScanForBackupFolders()
    {
        var found = new List<FoundBackup>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return found;
        }
        foreach (var drive in drives)
        {
            bool ready;
            DriveType kind;
            string root;
            try
            {
                ready = drive.IsReady;
                kind = drive.DriveType;
                root = drive.RootDirectory.FullName;
            }
            catch
            {
                continue;
            }
            if (!ready || kind == DriveType.Ram)
                continue;
            var candidates = new List<string> { root };
            try
            {
                candidates.AddRange(Directory.GetDirectories(root));
            }
            catch
            {
                // Unreadable root: the root check below still runs.
            }
            foreach (string dir in candidates)
            {
                string key;
                try
                {
                    key = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    continue;
                }
                if (!seen.Add(key))
                    continue;
                bool hasMarker;
                try
                {
                    hasMarker = File.Exists(Path.Combine(dir, "repo.json"));
                }
                catch
                {
                    continue;
                }
                if (hasMarker)
                    found.Add(new FoundBackup(dir, FriendlyBackupLabel(drive, dir)));
            }
        }
        return found;
    }

    /// <summary>Human-readable label: drive, volume name, folder, newest backup date.</summary>
    private static string FriendlyBackupLabel(DriveInfo drive, string dir)
    {
        string letter;
        try
        {
            letter = drive.Name.TrimEnd('\\', '/');
        }
        catch
        {
            letter = "?";
        }
        string vol;
        try
        {
            vol = drive.VolumeLabel;
        }
        catch
        {
            vol = "";
        }
        string folder;
        try
        {
            string root = drive.RootDirectory.FullName.TrimEnd('\\', '/');
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            folder = full.Equals(root, StringComparison.OrdinalIgnoreCase) ? "drive root" : Path.GetFileName(full);
        }
        catch
        {
            folder = "";
        }
        var parts = new List<string> { letter };
        if (!string.IsNullOrWhiteSpace(vol))
            parts.Add(vol);
        if (!string.IsNullOrWhiteSpace(folder))
            parts.Add(folder);
        if (NewestManifestDate(dir) is DateTime d)
            parts.Add($"backup from {d:d}");
        return string.Join(" — ", parts);
    }

    /// <summary>Newest manifest write time in a backup folder, if any.</summary>
    private static DateTime? NewestManifestDate(string dir)
    {
        try
        {
            string manifests = Path.Combine(dir, "manifests");
            if (!Directory.Exists(manifests))
                return null;
            DateTime latest = DateTime.MinValue;
            bool any = false;
            foreach (string f in Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories))
            {
                DateTime w;
                try
                {
                    w = File.GetLastWriteTime(f);
                }
                catch
                {
                    continue;
                }
                if (!any || w > latest)
                {
                    latest = w;
                    any = true;
                }
            }
            return any ? latest : null;
        }
        catch
        {
            return null;
        }
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

        // Network scan: find SMB hosts/shares on the local subnet
        _scanSmb.Text = "Scan network for shares…";
        _scanSmb.Location = new Point(16, 456);
        _scanSmb.Size = new Size(180, 28);
        _scanSmb.Click += ScanSmb_Click;
        p.Controls.Add(_scanSmb);
        _smbResults.Location = new Point(204, 456);
        _smbResults.Size = new Size(372, 60);
        _smbResults.SelectedIndexChanged += (_, _) =>
        {
            if (_smbResults.SelectedItem is SmbShareView v)
                _smbShare.Text = v.Unc;
        };
        p.Controls.Add(_smbResults);

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

    private sealed record SmbShareView(string Unc, string Display)
    {
        public override string ToString() => Display;
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
                    string.IsNullOrEmpty(password) ? null : password,
                    createIfMissing: false);
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
                        var manifest = System.Text.Json.JsonSerializer.Deserialize(
                            File.ReadAllText(file), FileKeepJsonContext.Default.BackupManifest);
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
                // P2-L16: record the share FIRST — the copy loop below
                // used to run before these assignments, so one failed
                // copy aborted into "SMB discovery failed" with the
                // listed backups unable to pull chunks at restore time
                // (no share stored) and retries adding duplicates.
                _state.SmbShare = share;
                _state.SmbUser = user;
                _state.SmbPassword = password;
                Directory.CreateDirectory(Path.Combine(_state.RepoPath, "manifests"));
                int added = 0;
                var failed = new List<string>();
                foreach (var (id, manifest) in found)
                {
                    if (_backupList.Items.Cast<BackupSummaryView>()
                            .Any(v => v.BackupId == id))
                        continue; // already listed (a retry after a partial copy)
                    try
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
                        added++;
                    }
                    catch (Exception ex)
                    {
                        // One failed copy must not poison the whole
                        // discovery: the rest still list and restore.
                        failed.Add($"{id} ({ex.Message})");
                    }
                }
                SetStatus(failed.Count == 0
                    ? $"Found {added} backup(s) on SMB share — marked '(from SMB)'."
                    : $"Found {added} backup(s) on SMB share. {failed.Count} manifest(s) could not be copied: " +
                      string.Join("; ", failed),
                    isError: failed.Count > 0 && added == 0);
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

    private async void ScanSmb_Click(object? sender, EventArgs e)
    {
        _scanSmb.Enabled = false;
        _smbResults.Items.Clear();
        _smbResults.Items.Add("Scanning local network for SMB shares…");
        try
        {
            var discovery = new UsenetBackup.Core.SmbDiscovery();
            var hosts = await Task.Run(() => discovery.DiscoverAsync(
                (done, total) => UiInvoke(() =>
                {
                    if (_smbResults.Items.Count > 0)
                        _smbResults.Items[0] = $"Scanning… {done}/{total} addresses";
                })));
            _smbResults.Items.Clear();
            int shareCount = 0;
            foreach (var host in hosts)
            {
                string label = host.HostName is not null
                    ? $"{host.HostName} ({host.HostAddress})"
                    : host.HostAddress;
                if (!host.SharesListed)
                {
                    _smbResults.Items.Add($"{label} — reachable, share list needs credentials");
                    continue;
                }
                foreach (var share in host.Shares)
                {
                    string unc = $@"\\{host.HostAddress}\{share.ShareName}";
                    string display = string.IsNullOrEmpty(share.Remark) ? unc : $"{unc} — {share.Remark}";
                    _smbResults.Items.Add(new SmbShareView(unc, display));
                    shareCount++;
                }
            }
            if (hosts.Count == 0)
                _smbResults.Items.Add("No SMB hosts found on the local network.");
            else if (shareCount == 0)
                SetStatus($"Found {hosts.Count} SMB host(s), but no visible shares. Enter the share path manually.");
            else
                SetStatus($"Found {shareCount} share(s). Select one to fill the share field.");
        }
        catch (Exception ex)
        {
            _smbResults.Items.Clear();
            _smbResults.Items.Add("Scan failed: " + ex.Message);
        }
        finally
        {
            _scanSmb.Enabled = true;
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
            try
            {
                using var d = new OpenFileDialog { Filter = "NZB files (*.nzb)|*.nzb" };
                if (d.ShowDialog() == DialogResult.OK)
                    _nzbPath.Text = d.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the file picker:\n\n" + ex.Message,
                    "FileKeep Recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                    UiInvoke(() =>
                    {
                        _dlProgress.Maximum = total;
                        _dlProgress.Value = Math.Min(done, total);
                        _dlLog.AppendText($"\r{done}/{total} chunks");
                    });
                });
                UiInvoke(() => _dlLog.AppendText(
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

    // The backup ID the last COMPLETED verification ran against (CX-5).
    private string? _verifyRunForBackupId;

    private async void VerifyStart_Click(object? sender, EventArgs e)
    {
        _verifyStart.Enabled = false;
        _verifyResult.Clear();
        try
        {
            var issues = await Task.Run(() => _state.Verify());
            _verifyRunForBackupId = _state.SelectedBackupId;
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
            try
            {
                using var d = new FolderBrowserDialog { Description = "Select restore destination" };
                if (d.ShowDialog() == DialogResult.OK)
                    _restoreDest.Text = d.SelectedPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the folder picker:\n\n" + ex.Message,
                    "FileKeep Recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        p.Controls.Add(destBrowse);

        // Merge policy chooser (file-restore mode only): what to do when a
        // file already exists in the destination.
        _policyCaption.Text = "If a file already exists in the destination:";
        _policyCaption.AutoSize = true;
        _policyCaption.Location = new Point(16, 142);
        p.Controls.Add(_policyCaption);

        _policySkip.Text = MergePolicyInfo.DisplayName(MergePolicy.SkipDiffering);
        _policySkip.Location = new Point(32, 164);
        _policySkip.AutoSize = true;
        _policySkip.Checked = true;
        _policySkip.CheckedChanged += (_, _) => UpdatePolicyInfo();
        p.Controls.Add(_policySkip);

        _policyOverwrite.Text = MergePolicyInfo.DisplayName(MergePolicy.Overwrite);
        _policyOverwrite.Location = new Point(32, 188);
        _policyOverwrite.AutoSize = true;
        _policyOverwrite.CheckedChanged += (_, _) => UpdatePolicyInfo();
        p.Controls.Add(_policyOverwrite);

        _policyNewer.Text = MergePolicyInfo.DisplayName(MergePolicy.KeepNewer);
        _policyNewer.Location = new Point(32, 212);
        _policyNewer.AutoSize = true;
        _policyNewer.CheckedChanged += (_, _) => UpdatePolicyInfo();
        p.Controls.Add(_policyNewer);

        _policyInfo.Location = new Point(32, 238);
        _policyInfo.Size = new Size(528, 60);
        _policyInfo.AutoSize = false;
        p.Controls.Add(_policyInfo);
        UpdatePolicyInfo();

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

    private MergePolicy SelectedPolicy =>
        _policyNewer.Checked ? MergePolicy.KeepNewer :
        _policyOverwrite.Checked ? MergePolicy.Overwrite :
        MergePolicy.SkipDiffering;

    private void UpdatePolicyInfo()
    {
        _policyInfo.Text = MergePolicyInfo.Description(SelectedPolicy);
    }

    private void UpdateRestoreMode()
    {
        bool disk = _restoreDisk.Checked;
        _drivePicker.Enabled = disk;
        _restoreDest.Enabled = !disk;
        // Merge policy only applies to file restore; the drive controls
        // only apply to disk restore.
        _policyCaption.Visible = !disk;
        _policySkip.Visible = !disk;
        _policyOverwrite.Visible = !disk;
        _policyNewer.Visible = !disk;
        _policyInfo.Visible = !disk;
        // Show/hide drive controls vs folder controls.
        // P2-L18: the old loop searched this.Controls for a TabPage —
        // the pages live in _tabs inside a content panel, so it never
        // matched and the drive label/refresh button stayed visible
        // (overlapping the policy radios) in file-restore mode.
        // Controls.Find with searchAllChildren walks the real tree.
        foreach (string name in new[] { "driveLbl", "refreshBtn" })
            foreach (Control c in Controls.Find(name, searchAllChildren: true))
                c.Visible = disk;
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
                UiInvoke(() =>
                {
                    _restoreProgress.Value = Math.Min(100, done * 100 / total);
                    _restoreLog.Text = $"Copying chunks from SMB share… {done}/{total}";
                });
            }
            if (_restoreFiles.Checked)
            {
                if (string.IsNullOrWhiteSpace(_restoreDest.Text))
                    throw new InvalidOperationException("Choose a destination folder.");
                var policy = SelectedPolicy;
                var result = await Task.Run(() => _state.RestoreFiles(_restoreDest.Text, policy, ReportSmb));
                _restoreLog.Text = $"File restore complete: {result.RestoredFiles} file(s) restored.";
                if (result.SkippedIdentical > 0)
                    _restoreLog.Text += $" {result.SkippedIdentical} already matched.";
                if (result.SkippedDiffering.Count > 0)
                    _restoreLog.Text += $" {result.SkippedDiffering.Count} differing file(s) left alone: " +
                        string.Join(", ", result.SkippedDiffering);
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
                    // Empty is allowed: the user may unlock with a YubiKey
                    // after picking a backup (checked on the next page).
                    SetStatus("No passphrase typed — you can use a YubiKey instead if the backup supports it.");
                }
                else
                    SetStatus("");
                if (string.IsNullOrEmpty(_state.NntpHost))
                {
                    SetStatus("Usenet host is required.", isError: true);
                    return false;
                }
                try
                {
                    _state.OpenRepo(); // validates repo format
                    if (!string.IsNullOrEmpty(_state.Passphrase))
                    {
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
                    // Empty passphrase: validated via YubiKey on the next page,
                    // or rejected there if the backup has no YubiKey challenge.
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
                // YubiKey unlock (if needed): prompts for a touch when the
                // backup is YubiKey-protected and no passphrase was typed.
                if (!TryYubiKeyUnlock())
                    return false;
                // Still no credential (no passphrase, no YubiKey success,
                // backup has no YubiKey challenge)? Require the passphrase.
                if (string.IsNullOrEmpty(_state.Passphrase))
                {
                    SetStatus("This backup needs your passphrase. Go back and type it, " +
                        "or pick a YubiKey-protected backup and touch your key.",
                        isError: true);
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

            case 4: // verify — the step says "do not skip", so don't let
                // Next sail past it (CX-5). Verification must have RUN for
                // the currently selected backup; whether it passed is the
                // user's call (restore hash-verifies every file anyway).
                if (_verifyRunForBackupId is null ||
                    _verifyRunForBackupId != _state.SelectedBackupId)
                {
                    SetStatus("Run verification first — it checks every chunk " +
                        "before anything is restored.", isError: true);
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

    /// <summary>
    /// If the selected backup carries a YubiKey challenge and the user did
    /// not type a passphrase, prompts for a YubiKey touch and unwraps the
    /// passphrase. Returns true if the credentials are ready to proceed
    /// (typed passphrase OR successful YubiKey touch).
    /// </summary>
    private bool TryYubiKeyUnlock()
    {
        // Typed passphrase takes precedence — nothing to do.
        if (!string.IsNullOrEmpty(_state.Passphrase))
            return true;

        bool hasChallenge;
        try
        {
            hasChallenge = _state.LoadYubiKeyChallenge();
        }
        catch (Exception ex)
        {
            // MU-13: a corrupt manifest is not "no YubiKey on this
            // backup" — stop with the real error instead of steering
            // into a restore that fails confusingly later.
            SetStatus($"This backup's manifest is damaged or has been tampered with: {ex.Message}",
                isError: true);
            return false;
        }
        if (!hasChallenge)
            return true; // no YubiKey on this backup; typed flow already validated

        // This backup wants a YubiKey and the user didn't type a passphrase.
        // P2-L15: DetectKeys P/Invokes raw HID (SetupAPI) — in WinPE the
        // HID stack may be absent entirely, and the resulting
        // DllNotFound/TypeInitialization failure used to escape to the
        // generic crash dialog ("Something went wrong: Unable to load
        // DLL 'hid.dll'"), reading as data corruption at the worst
        // moment of a bare-metal restore. Detection being unavailable
        // is a steering message, not a crash.
        IReadOnlyList<YubiKeyRecovery.YubiKeyDeviceInfo> keys;
        try
        {
            keys = YubiKeyRecovery.DetectKeys();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                   or TypeInitializationException or BadImageFormatException)
        {
            SetStatus("YubiKey detection isn't available in this recovery environment " +
                "(no HID device support), so the key can't be used here. " +
                "Go back and unlock with your passphrase or recovery secret instead.",
                isError: true);
            return false;
        }
        if (keys.Count == 0)
        {
            SetStatus("This backup is YubiKey-protected but no YubiKey was found. " +
                "Plug in your YubiKey, or go back and type your passphrase instead.",
                isError: true);
            return false;
        }

        using var dlg = new YubiKeyTouchDialog(_state.YubiKeyChallenge!);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.HmacResponse is null)
        {
            // MU-11: hardware failures and timeouts are not "cancelled" —
            // say which one happened, so a hopeless retry loop (missing
            // HID driver, removed key) is recognizable as hopeless.
            SetStatus(dlg.Failure is not null
                    ? $"YubiKey unlock failed: {dlg.Failure.Message} " +
                      "Type your passphrase or try again."
                    : dlg.TimedOut
                        ? "No YubiKey touch within 30 seconds. " +
                          "Type your passphrase or try again."
                        : "YubiKey unlock cancelled. Type your passphrase or try again.",
                isError: true);
            return false;
        }

        try
        {
            _state.UnlockWithYubiKey(dlg.HmacResponse);
            SetStatus("YubiKey accepted.");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("YubiKey unlock failed: " + ex.Message +
                " Type your passphrase instead.", isError: true);
            return false;
        }
    }
}

/// <summary>
/// Modal "touch your YubiKey" prompt. Runs the HID challenge-response on a
/// worker thread with a 30-second countdown; OK only on a valid response.
/// </summary>
internal sealed class YubiKeyTouchDialog : Form
{
    private readonly string _challengeB64;
    private readonly Label _label = new();
    private readonly Label _countdown = new();
    private readonly Button _cancel = new();
    private System.Windows.Forms.Timer? _timer;
    private DateTime _deadline;
    private bool _done;

    public byte[]? HmacResponse { get; private set; }

    /// <summary>
    /// The hardware/SDK failure that ended the touch attempt, when one
    /// occurred (MU-11) — null for a user cancel or a plain timeout.
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <summary>True when the attempt ended by the 30s deadline, not by the user.</summary>
    public bool TimedOut { get; private set; }

    public YubiKeyTouchDialog(string challengeB64)
    {
        _challengeB64 = challengeB64;
        Text = "YubiKey required";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 150);

        _label.Text = "Touch your YubiKey now.\nThis backup is protected by your security key.";
        _label.Location = new Point(16, 16);
        _label.Size = new Size(348, 50);
        Controls.Add(_label);

        _countdown.Location = new Point(16, 70);
        _countdown.Size = new Size(348, 24);
        _countdown.ForeColor = Color.DarkBlue;
        Controls.Add(_countdown);

        _cancel.Text = "Cancel";
        _cancel.Location = new Point(290, 110);
        _cancel.Size = new Size(74, 28);
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_cancel);
        CancelButton = _cancel;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) =>
        {
            var left = _deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || _done)
            {
                _timer.Stop();
                if (!_done)
                {
                    TimedOut = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                return;
            }
            _countdown.Text = $"Waiting for touch… {left.TotalSeconds:F0}s";
        };
        _timer.Start();

        // Run the blocking HID touch off the UI thread.
        Task.Run(() =>
        {
            byte[]? response = null;
            Exception? failure = null;
            try
            {
                byte[] challenge = Convert.FromBase64String(_challengeB64);
                response = YubiKeyRecovery.ChallengeResponse(
                    challenge, slot: 1, timeout: TimeSpan.FromSeconds(28));
            }
            catch (Exception ex)
            {
                // MU-11: keep the failure — it used to be discarded and
                // surfaced as "cancelled," indistinguishable from the
                // user giving up.
                failure = ex;
            }

            if (_done) // user cancelled or timed out while we were waiting
            {
                if (response is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(response);
                return;
            }
            _done = true;
            BeginInvoke(() =>
            {
                _timer?.Stop();
                HmacResponse = response;
                Failure = failure;
                DialogResult = response is not null
                    ? DialogResult.OK : DialogResult.Cancel;
                Close();
            });
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _done = true;
        _timer?.Stop();
        _timer?.Dispose();
        base.OnFormClosing(e);
    }
}

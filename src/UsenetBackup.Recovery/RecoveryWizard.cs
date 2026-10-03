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
    private readonly TextBox _devicePath = new();
    private readonly TextBox _deviceConfirm = new();
    private readonly ProgressBar _restoreProgress = new();
    private readonly TextBox _restoreLog = new();
    private readonly Button _restoreStart = new();

    public RecoveryWizard()
    {
        Text = "Usenet Backup — USB Recovery";
        Size = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _tabs.Dock = DockStyle.Fill;
        _tabs.Appearance = TabAppearance.FlatButtons;
        _tabs.ItemSize = new Size(0, 1);
        _tabs.SizeMode = TabSizeMode.Fixed;
        Controls.Add(_tabs);

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
        Controls.Add(nav);

        BuildRepoPage();
        BuildCredsPage();
        BuildSelectPage();
        BuildDownloadPage();
        BuildVerifyPage();
        BuildRestorePage();

        UpdateNav();
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
        _backupDetail.Size = new Size(560, 80);
        p.Controls.Add(_backupDetail);

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

        _restoreDisk.Text = "Restore disk image to a device (destructive)";
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

        var devLbl = new Label { Text = "Device path (e.g. \\\\.\\PhysicalDrive0):", AutoSize = true, Location = new Point(16, 144) };
        p.Controls.Add(devLbl);
        _devicePath.Location = new Point(16, 168);
        _devicePath.Size = new Size(560, 28);
        p.Controls.Add(_devicePath);

        var confirmLbl = new Label
        {
            Text = "Type the device path again to confirm (destructive):",
            AutoSize = true, Location = new Point(16, 204), ForeColor = Color.DarkRed
        };
        p.Controls.Add(confirmLbl);
        _deviceConfirm.Location = new Point(16, 228);
        _deviceConfirm.Size = new Size(560, 28);
        p.Controls.Add(_deviceConfirm);

        _restoreStart.Text = "Start restore";
        _restoreStart.Location = new Point(16, 268);
        _restoreStart.Size = new Size(140, 32);
        _restoreStart.Click += RestoreStart_Click;
        p.Controls.Add(_restoreStart);

        _restoreProgress.Location = new Point(16, 312);
        _restoreProgress.Size = new Size(560, 24);
        p.Controls.Add(_restoreProgress);

        _restoreLog.Location = new Point(16, 348);
        _restoreLog.Size = new Size(560, 60);
        _restoreLog.Multiline = true;
        _restoreLog.ReadOnly = true;
        _restoreLog.ScrollBars = ScrollBars.Vertical;
        p.Controls.Add(_restoreLog);

        UpdateRestoreMode();
    }

    private void UpdateRestoreMode()
    {
        bool disk = _restoreDisk.Checked;
        _devicePath.Enabled = disk;
        _deviceConfirm.Enabled = disk;
        _restoreDest.Enabled = !disk;
    }

    private async void RestoreStart_Click(object? sender, EventArgs e)
    {
        _restoreStart.Enabled = false;
        _restoreLog.Clear();
        try
        {
            if (_restoreFiles.Checked)
            {
                if (string.IsNullOrWhiteSpace(_restoreDest.Text))
                    throw new InvalidOperationException("Choose a destination folder.");
                await Task.Run(() => _state.RestoreFiles(_restoreDest.Text));
                _restoreLog.Text = "File restore complete.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_devicePath.Text))
                    throw new InvalidOperationException("Enter the device path.");
                await Task.Run(() => _state.RestoreDisk(_devicePath.Text, _deviceConfirm.Text));
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

namespace Connector.Upgrade.Bootstrapper;

// Burn has already installed the machine prerequisites. This original-user stage
// runs automatically and never asks for a token or opens the enrollment workflow.
internal sealed class StandaloneInstallForm : Form, IBootstrapperStatusSink
{
    private readonly IStandaloneDesktopInstaller _installer;
    private readonly Label _status;
    private readonly Button _close;
    private readonly LinkLabel _journalLink;
    private readonly StandaloneInstallJournal _journal = StandaloneInstallJournal.Create();
    private bool _running;
    public int ExitCode { get; private set; } = 1;

    public StandaloneInstallForm(IStandaloneDesktopInstaller installer)
    {
        _installer = installer;
        Text = "Платформа Коннектор — установка";
        ClientSize = new Size(640, 330);
        Font = new Font("Segoe UI", 10F);
        BackColor = BootstrapperForm.InstallerTheme.Background;
        ForeColor = BootstrapperForm.InstallerTheme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(BootstrapperForm.InstallerTheme.Logo(28, 24, 52));
        Controls.Add(new Label { Left = 96, Top = 26, Width = 516, Height = 42, Text = "Платформа Коннектор",
            Font = new Font("Segoe UI", 23F, FontStyle.Bold) });
        _status = new Label { Left = 30, Top = 105, Width = 580, Height = 105,
            Text = "Подготавливаем установку…" };
        _journalLink = new LinkLabel { Left = 30, Top = 215, Width = 580, Height = 24,
            Text = "Журнал установки", Visible = false, LinkColor = Color.FromArgb(117, 193, 225) };
        _journalLink.LinkClicked += (_, _) =>
        {
            if (_journal.LogPath is { } path && File.Exists(path))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { Show("Не удалось открыть журнал установки."); }
            }
        };
        var progress = new ProgressBar { Left = 30, Top = 245, Width = 580, Height = 4, Style = ProgressBarStyle.Marquee };
        _close = BootstrapperForm.InstallerTheme.Button("Закрыть", 410, 270, 200);
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        Controls.AddRange([_status, _journalLink, progress, _close]);
        FormClosed += (_, _) => _journal.Dispose();
        FormClosing += (_, e) => e.Cancel = _running;
        Shown += async (_, _) =>
        {
            _running = true;
            try
            {
                var result = await _installer.InstallAsync(this, CancellationToken.None);
                ExitCode = result.RebootRequired ? 3010 : 0;
                // The outer Burn window supplies the final success page. Do not hold its
                // transaction waiting for a second Finish click or for account enrollment.
                _running = false;
                Close();
            }
            catch (Exception error)
            {
                _journal.Failure(error);
                _journalLink.Visible = _journal.LogPath is { } path && File.Exists(path);
                var message = error is InvalidOperationException or System.Security.SecurityException
                    ? error.Message : "Не удалось завершить установку приложения.";
                Show(message + "\n\nКод ошибки: 0x" + error.HResult.ToString("X8"));
                progress.Visible = false;
                _running = false;
                _close.Enabled = true;
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BootstrapperForm.InstallerTheme.UseDarkTitleBar(Handle);
    }
    public void Show(string message)
    {
        _status.Text = message;
        _journal.Progress(message);
    }
}

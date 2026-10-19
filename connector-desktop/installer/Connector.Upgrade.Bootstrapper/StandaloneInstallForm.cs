namespace Connector.Upgrade.Bootstrapper;

// Burn has already installed the machine prerequisites. This original-user stage
// runs automatically and never asks for a token or opens the enrollment workflow.
internal sealed class StandaloneInstallForm : Form, IBootstrapperStatusSink
{
    private readonly IStandaloneDesktopInstaller _installer;
    private readonly Label _status;
    private readonly Button _close;
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
            Text = "Подготовка приложения…" };
        var progress = new ProgressBar { Left = 30, Top = 220, Width = 580, Height = 4, Style = ProgressBarStyle.Marquee };
        _close = BootstrapperForm.InstallerTheme.Button("Закрыть", 410, 260, 200);
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        Controls.AddRange([_status, progress, _close]);
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
                // Installation exceptions contain no enrollment credential or server response.
                Show(error is InvalidOperationException or System.Security.SecurityException
                    ? error.Message : "Установка приложения не завершена. Сведения доступны в журнале установки.");
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
    public void Show(string message) => _status.Text = message;
}

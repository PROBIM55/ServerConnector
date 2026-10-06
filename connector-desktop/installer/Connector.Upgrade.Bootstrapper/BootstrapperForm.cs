namespace Connector.Upgrade.Bootstrapper;

internal sealed class BootstrapperForm : Form, IBootstrapperStatusSink
{
    private readonly IWindowsUpgradeBootstrapperRuntimeFactory _runtimeFactory;
    private readonly Label _status;
    private readonly Button _execute;
    private readonly Button _recover;
    private bool _isRunning;
    public int ExitCode { get; private set; } = 1602;

    public BootstrapperForm(IWindowsUpgradeBootstrapperRuntimeFactory runtimeFactory)
    {
        _runtimeFactory = runtimeFactory;
        Text = "Structura Connector — перенос доступа";
        Width = 520;
        Height = 230;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _status = new Label
        {
            Left = 22, Top = 22, Width = 455, Height = 72,
            Text = "Выберите действие. Операция выполняется под текущей учётной записью Windows.",
            AutoSize = false
        };
        _execute = new Button { Left = 22, Top = 112, Width = 220, Height = 38, Text = "Перенести доступ…" };
        _recover = new Button { Left = 257, Top = 112, Width = 220, Height = 38, Text = "Восстановить прерванную операцию" };
        _execute.Click += async (_, _) => await RunAsync(BootstrapperAction.ExecuteUpgrade);
        _recover.Click += async (_, _) => await RunAsync(BootstrapperAction.RecoverInterrupted);
        FormClosing += (_, args) =>
        {
            if (!_isRunning) return;
            args.Cancel = true;
            Show("Операция выполняется. Дождитесь её завершения перед закрытием окна.");
        };
        Controls.AddRange([_status, _execute, _recover]);
    }

    public void Show(string message) => _status.Text = message;

    private async Task RunAsync(BootstrapperAction action)
    {
        if (_isRunning) return;
        _isRunning = true;
        _execute.Enabled = false;
        _recover.Enabled = false;
        try
        {
            var outcome = await new BootstrapperRunner(_runtimeFactory, new PlatformTokenPrompt(this), this).RunAsync(action);
            if (outcome is { Outcome: Connector.Upgrade.Core.UpgradeOutcome.Succeeded or Connector.Upgrade.Core.UpgradeOutcome.AlreadyCommitted })
            {
                ExitCode = BootstrapperRunner.ToProcessExitCode(outcome);
                _isRunning = false;
                Close();
            }
            else if (outcome is not null)
            {
                ExitCode = 1;
            }
        }
        finally
        {
            _isRunning = false;
            _execute.Enabled = true;
            _recover.Enabled = true;
        }
    }

    private sealed class PlatformTokenPrompt(IWin32Window owner) : IPlatformTokenPrompt
    {
        public ValueTask<string?> RequestTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var dialog = new TokenEntryDialog();
            return ValueTask.FromResult(dialog.ShowDialog(owner) == DialogResult.OK ? dialog.ConsumeToken() : null);
        }
    }

    internal sealed class TokenEntryDialog : Form
    {
        private readonly TextBox _token;
        private string? _acceptedToken;

        public TokenEntryDialog()
        {
            Text = "Токен Platform";
            Width = 445;
            Height = 175;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            Controls.Add(new Label
            {
                Left = 16, Top = 16, Width = 395, Height = 38,
                Text = "Введите одноразовый токен Platform. Он будет использован только для этой операции."
            });
            _token = new TextBox { Left = 16, Top = 60, Width = 395, UseSystemPasswordChar = true, MaxLength = 4096 };
            var accept = new Button { Left = 235, Top = 96, Width = 85, Text = "Продолжить" };
            var cancel = new Button { Left = 326, Top = 96, Width = 85, Text = "Отмена", DialogResult = DialogResult.Cancel };
            accept.Click += (_, _) => AcceptToken();
            Controls.AddRange([_token, accept, cancel]);
            AcceptButton = accept;
            CancelButton = cancel;
        }

        public string? ConsumeToken()
        {
            var value = _acceptedToken;
            _acceptedToken = null;
            return value;
        }

        internal void AcceptToken(bool closeDialog = true)
        {
            if (string.IsNullOrWhiteSpace(_token.Text))
            {
                MessageBox.Show(this, "Введите токен Platform.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _acceptedToken = _token.Text;
            _token.Clear();
            DialogResult = DialogResult.OK;
            if (closeDialog)
                Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _token.Clear();
            if (DialogResult != DialogResult.OK)
                _acceptedToken = null;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _token.Clear();
                _acceptedToken = null;
            }
            base.Dispose(disposing);
        }
    }
}

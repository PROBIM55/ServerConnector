using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace Connector.Upgrade.Bootstrapper;

internal sealed class BootstrapperForm : Form, IBootstrapperStatusSink
{
    private readonly IWindowsUpgradeBootstrapperRuntimeFactory _runtimeFactory;
    private readonly Label _status;
    private readonly Label _statusTitle;
    private readonly ProgressBar _progress;
    private readonly Button _execute;
    private readonly Button _recover;
    private bool _isRunning;
    private bool _isComplete;
    public int ExitCode { get; private set; } = 1602;

    public BootstrapperForm(IWindowsUpgradeBootstrapperRuntimeFactory runtimeFactory)
    {
        _runtimeFactory = runtimeFactory;
        Text = "Structura Connector — установка";
        ClientSize = new Size(640, 382);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Segoe UI", 10F);
        BackColor = InstallerTheme.Background;
        ForeColor = InstallerTheme.Text;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var logo = InstallerTheme.Logo(28, 24, 52);

        var brand = new Label
        {
            Left = 96, Top = 26, Width = 516, Height = 42,
            Text = "Structura Connector", Font = new Font("Segoe UI", 23F, FontStyle.Bold)
        };
        var introduction = new Label
        {
            Left = 30, Top = 80, Width = 580, Height = 52,
            Text = "Установка приложения и подключение к Платформе.",
            ForeColor = InstallerTheme.Muted
        };
        var statusPanel = new Panel
        {
            Left = 28, Top = 151, Width = 584, Height = 143,
            BackColor = InstallerTheme.Surface
        };
        _statusTitle = new Label
        {
            Left = 18, Top = 16, Width = 548, Height = 26,
            Text = "Готово к установке", Font = new Font(Font, FontStyle.Bold),
            ForeColor = InstallerTheme.Accent
        };
        _status = new Label
        {
            Left = 18, Top = 50, Width = 548, Height = 66,
            Text = "Сначала проверим сервер. Затем потребуется одноразовый токен для подключения этого компьютера.",
            AutoSize = false
        };
        _progress = new ProgressBar
        {
            Left = 18, Top = 123, Width = 548, Height = 4,
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false
        };
        statusPanel.Controls.AddRange([_statusTitle, _status, _progress]);
        _execute = InstallerTheme.Button("Установить и подключить", 28, 316, 286, primary: true);
        _recover = InstallerTheme.Button("Восстановить установку", 328, 316, 284);
        _execute.Click += async (_, _) => await RunAsync(BootstrapperAction.ExecuteUpgrade);
        _recover.Click += async (_, _) => await RunAsync(BootstrapperAction.RecoverInterrupted);
        FormClosing += (_, args) =>
        {
            if (!_isRunning) return;
            args.Cancel = true;
            Show("Операция выполняется. Дождитесь её завершения перед закрытием окна.");
        };
        Controls.AddRange([logo, brand, introduction, statusPanel, _execute, _recover]);
        AcceptButton = _execute;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InstallerTheme.UseDarkTitleBar(Handle);
    }

    public void Show(string message) => _status.Text = message;

    private async Task RunAsync(BootstrapperAction action)
    {
        if (_isRunning) return;
        if (_isComplete) { Close(); return; }
        _isRunning = true;
        _statusTitle.Text = "Выполняется установка";
        _statusTitle.ForeColor = InstallerTheme.Accent;
        _progress.Visible = true;
        _execute.Enabled = false;
        _recover.Enabled = false;
        try
        {
            var outcome = await new BootstrapperRunner(_runtimeFactory, new PlatformTokenPrompt(this), this).RunAsync(action);
            if (outcome is { Outcome: Connector.Upgrade.Core.UpgradeOutcome.Succeeded or Connector.Upgrade.Core.UpgradeOutcome.AlreadyCommitted })
            {
                ExitCode = BootstrapperRunner.ToProcessExitCode(outcome);
                _isComplete = true;
                _statusTitle.Text = "Коннектор установлен";
                _statusTitle.ForeColor = InstallerTheme.Success;
                _execute.Text = "Готово";
                _recover.Visible = false;
            }
            else
            {
                ExitCode = outcome is null ? 1602 : 1;
                _statusTitle.Text = "Подключение не завершено";
                _statusTitle.ForeColor = InstallerTheme.Warning;
                _execute.Text = "Повторить проверку";
            }
        }
        finally
        {
            _isRunning = false;
            _progress.Visible = false;
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
            Text = "Structura Connector — подключение";
            ClientSize = new Size(520, 288);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Segoe UI", 10F);
            BackColor = InstallerTheme.Background;
            ForeColor = InstallerTheme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Controls.Add(InstallerTheme.Logo(24, 22, 40));

            Controls.Add(new Label
            {
                Left = 80, Top = 24, Width = 416, Height = 36,
                Text = "Подключить этот компьютер", Font = new Font("Segoe UI", 17F, FontStyle.Bold)
            });
            Controls.Add(new Label
            {
                Left = 26, Top = 76, Width = 468, Height = 51,
                Text = "Введите одноразовый токен из Платформы.\nОн используется только для этого подключения.",
                ForeColor = InstallerTheme.Muted
            });
            _token = new TextBox
            {
                Left = 26, Top = 148, Width = 468, UseSystemPasswordChar = true, MaxLength = 4096,
                BackColor = InstallerTheme.Surface, ForeColor = InstallerTheme.Text,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 13F),
                AccessibleName = "Одноразовый токен"
            };
            var accept = InstallerTheme.Button("Подключить", 26, 212, 274, primary: true);
            var cancel = InstallerTheme.Button("Отмена", 314, 212, 180);
            cancel.DialogResult = DialogResult.Cancel;
            accept.Click += (_, _) => AcceptToken();
            Controls.AddRange([_token, accept, cancel]);
            AcceptButton = accept;
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            InstallerTheme.UseDarkTitleBar(Handle);
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
                MessageBox.Show(this, "Введите одноразовый токен из Платформы.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    internal static class InstallerTheme
    {
        // Former Graphite review palette (kit-tokens.js, removed); the application colours now live in design/tokens.css.
        public static readonly Color Background = Color.FromArgb(0x12, 0x17, 0x1d);
        public static readonly Color Surface = Color.FromArgb(0x1a, 0x20, 0x27);
        public static readonly Color Text = Color.FromArgb(0xed, 0xf2, 0xf6);
        public static readonly Color Muted = Color.FromArgb(0xae, 0xb9, 0xc4);
        public static readonly Color Accent = Color.FromArgb(0x77, 0xb8, 0xdd);
        public static readonly Color Success = Color.FromArgb(0x72, 0xc7, 0x9d);
        public static readonly Color Warning = Color.FromArgb(0xe4, 0xbb, 0x5d);

        public static PictureBox Logo(int left, int top, int size)
        {
            using var stream = typeof(BootstrapperForm).Assembly.GetManifestResourceStream(
                "Connector.Upgrade.Bootstrapper.PlatformLogo.png")
                ?? throw new InvalidOperationException("Platform logo resource is missing.");
            using var source = Image.FromStream(stream);
            var image = new Bitmap(source.Width, source.Height);
            using (var graphics = Graphics.FromImage(image))
            using (var attributes = new ImageAttributes())
            {
                // Match the desktop kit's brightness(0) invert(1) for its monochrome mark.
                var tint = new ColorMatrix(new[]
                {
                    new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 1, 1, 1, 0, 1 }
                });
                attributes.SetColorMatrix(tint);
                graphics.DrawImage(source, new Rectangle(0, 0, image.Width, image.Height),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            var logo = new PictureBox
            {
                Left = left, Top = top, Width = size, Height = size,
                Image = image, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Платформа"
            };
            logo.Disposed += (_, _) => image.Dispose();
            return logo;
        }

        public static Button Button(string text, int left, int top, int width, bool primary = false)
        {
            var button = new Button
            {
                Text = text, Left = left, Top = top, Width = width, Height = 44,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                BackColor = primary ? Accent : Surface,
                ForeColor = primary ? Color.FromArgb(0x0d, 0x1d, 0x27) : Text,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold), Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(0x39, 0x46, 0x52);
            return button;
        }

        public static void UseDarkTitleBar(IntPtr handle)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
            var enabled = 1;
            // Unsupported Windows versions retain the normal system title bar.
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}

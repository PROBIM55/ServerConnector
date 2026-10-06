using System.Windows.Forms;
using System.Reflection;
using Connector.Upgrade.Bootstrapper;

namespace Connector.Upgrade.Bootstrapper.Tests;

public sealed class TokenEntryDialogTests
{
    [Fact]
    public void Accepted_token_survives_form_closed_until_consumed_once()
    {
        RunOnSta(() =>
        {
            using var dialog = new BootstrapperForm.TokenEntryDialog();
            var tokenBox = dialog.Controls.OfType<TextBox>().Single();
            var closed = false;
            dialog.FormClosed += (_, _) => closed = true;
            tokenBox.Text = "one-time-platform-token";

            dialog.AcceptToken(closeDialog: false);
            InvokeFormClosed(dialog);

            Assert.True(closed);
            Assert.Equal(DialogResult.OK, dialog.DialogResult);
            Assert.Empty(tokenBox.Text);
            Assert.Equal("one-time-platform-token", dialog.ConsumeToken());
            Assert.Null(dialog.ConsumeToken());
        });
    }

    [Fact]
    public void Cancel_and_dispose_clear_unconsumed_token()
    {
        RunOnSta(() =>
        {
            var cancelled = new BootstrapperForm.TokenEntryDialog();
            var cancelledBox = cancelled.Controls.OfType<TextBox>().Single();
            cancelledBox.Text = "must-not-survive-cancel";
            cancelled.DialogResult = DialogResult.Cancel;
            InvokeFormClosed(cancelled);
            Assert.Empty(cancelledBox.Text);
            Assert.Null(cancelled.ConsumeToken());
            cancelled.Dispose();

            var disposed = new BootstrapperForm.TokenEntryDialog();
            disposed.Controls.OfType<TextBox>().Single().Text = "must-not-survive-dispose";
            disposed.AcceptToken(closeDialog: false);
            disposed.Dispose();
            Assert.Null(disposed.ConsumeToken());
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"STA form lifecycle failed: {failure}");
    }

    private static void InvokeFormClosed(Form form)
    {
        // An unshown WinForms Form.Close() can dispose the form without raising
        // FormClosed. Invoke the protected lifecycle hook directly so this test
        // exercises the exact cleanup/preservation override without showing UI.
        var lifecycle = typeof(Form).GetMethod("OnFormClosed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WinForms Form.OnFormClosed was not found.");
        lifecycle.Invoke(form, [new FormClosedEventArgs(CloseReason.None)]);
    }
}

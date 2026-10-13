using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        string directory = Path.Combine(Path.GetTempPath(), "StructuraVpnRecovery-" + Guid.NewGuid().ToString("N"));
        string[] names = { "connector_vpn_recovery.ps1", "install_connector_vpn_recovery.ps1" };
        bool cleanUp = true;
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, security);
            foreach (string name in names)
            {
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                using (Stream output = File.Create(Path.Combine(directory, name))) input.CopyTo(output);
            }
            bool uninstall = args.Length == 1 && args[0] == "/uninstall";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(directory, names[1]) +
                "\" -Mode Client" + (uninstall ? " -Uninstall" : ""));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardError = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    // The child may still be copying its payload. Do not delete
                    // its inputs or report success while installation is pending.
                    cleanUp = false;
                    MessageBox.Show("Установка ещё выполняется. Не запускайте установщик повторно.\nПроверьте задачу StructuraConnectorVpnRecoveryClient в планировщике Windows.",
                        "Structura — восстановление VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 2;
                }
                if (process.ExitCode != 0) throw new InvalidOperationException(error.Result);
            }
            MessageBox.Show(uninstall ? "Автовосстановление отключено. VPN-служба не изменена." :
                "Автовосстановление VPN установлено. Работающее соединение не перезапускалось.\n\nПри потере доступа проверка выполняется автоматически. Открытые SMB-соединения сохраняются.",
                "Structura — восстановление VPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("Не удалось завершить установку:\n" + error.Message,
                "Structura — восстановление VPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            // Delete only the two files created by this process, never recurse.
            if (cleanUp)
            {
                foreach (string name in names) { try { File.Delete(Path.Combine(directory, name)); } catch { } }
                try { Directory.Delete(directory, false); } catch { }
            }
        }
    }
}

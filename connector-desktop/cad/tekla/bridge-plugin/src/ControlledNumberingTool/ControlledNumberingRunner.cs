using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Tekla.Macros.Akit;
using Tekla.Structures.Model;

namespace Structura.Tekla.ControlledNumbering
{
    public static class ControlledNumberingRunner
    {
        private const string DialogName = "m3_dialog";

        public static void Run(object akitHost)
        {
            try
            {
                if (!(akitHost is IAkitScriptHost akit))
                {
                    throw new InvalidOperationException("Tekla не передала интерфейс выполнения макроса.");
                }

                var model = new Model();
                if (!model.GetConnectionStatus())
                {
                    throw new InvalidOperationException("Нет подключения к открытой модели Tekla Structures.");
                }

                var modelPath = model.GetInfo()?.ModelPath;
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    throw new InvalidOperationException("Не удалось определить папку открытой модели.");
                }

                var historyPath = Path.Combine(modelPath, "numberinghistory.txt");
                var originalLength = File.Exists(historyPath) ? new FileInfo(historyPath).Length : 0L;

                ApplySafeNumberingSettings(akit);
                akit.Callback("acmd_partnumbers_all", string.Empty, "main_frame");

                var appendedText = WaitForCompletedSession(historyPath, originalLength);
                var session = NumberingHistoryParser.ParseLatestCompleteSession(appendedText);

                using (var form = new ControlledNumberingResultsForm(model, session))
                {
                    form.ShowDialog(new MainWindow(Process.GetCurrentProcess().MainWindowHandle));
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ShowError(ex.InnerException);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private static void ApplySafeNumberingSettings(IAkitScriptHost akit)
        {
            akit.Callback("acmd_display_partnumbers_set_options", string.Empty, "main_frame");
            akit.ValueChange(DialogName, "m3_save_numbering_save", "0");
            akit.ValueChange(DialogName, "m3_automatic_cloning", "0");
            akit.ValueChange(DialogName, "m3_use_old_numbers", "0");
            akit.PushButton("m3_apply", DialogName);
            akit.PushButton("m3_ok", DialogName);
        }

        private static string WaitForCompletedSession(string historyPath, long originalLength)
        {
            string appendedText = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                appendedText = ReadAppendedText(historyPath, originalLength);
                if (appendedText.IndexOf("***NUMBERING_HISTORY Operation finished", StringComparison.Ordinal) >= 0)
                {
                    return appendedText;
                }

                Thread.Sleep(100);
            }

            throw new InvalidDataException(
                "Tekla не записала завершённую сессию в numberinghistory.txt. " +
                "Нумерация могла быть отменена или прервана.");
        }

        private static string ReadAppendedText(string historyPath, long originalLength)
        {
            if (!File.Exists(historyPath))
            {
                return string.Empty;
            }

            using (var stream = new FileStream(
                       historyPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var start = stream.Length >= originalLength ? originalLength : 0L;
                stream.Position = start;
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return DecodeLogBytes(buffer.ToArray());
                }
            }
        }

        private static string DecodeLogBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1251).GetString(bytes);
            }
        }

        private static void ShowError(Exception ex)
        {
            MessageBox.Show(
                "Контролируемая нумерация не завершена.\r\n\r\n" + ex.Message,
                "Контроль нумерации",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private sealed class MainWindow : IWin32Window
        {
            public MainWindow(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}

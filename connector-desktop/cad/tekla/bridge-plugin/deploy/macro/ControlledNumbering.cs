#pragma warning disable 1633
#pragma reference "Tekla.Macros.Akit"
#pragma reference "Tekla.Macros.Wpf.Runtime"
#pragma reference "Tekla.Macros.Runtime"
#pragma warning restore 1633

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace UserMacros
{
    public sealed class Macro
    {
        [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
        public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime)
        {
            try
            {
                var akit = runtime.Get<Tekla.Macros.Akit.IAkitScriptHost>();
                var assemblyPath = FindToolAssembly();
                var assembly = Assembly.LoadFrom(assemblyPath);
                var runner = assembly.GetType(
                    "Structura.Tekla.ControlledNumbering.ControlledNumberingRunner",
                    true,
                    false);
                runner.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { akit });
            }
            catch (TargetInvocationException ex)
            {
                ShowError(ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private static string FindToolAssembly()
        {
            string xsDir = null;
            Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_DIR", ref xsDir);
            var candidates = new[]
            {
                Path.Combine(xsDir ?? string.Empty, "Environments", "common", "extensions", "custom", "FachwerkKmd", "ControlledNumberingTool.dll"),
                Path.Combine(xsDir ?? string.Empty, "Environments", "common", "Extensions", "custom", "FachwerkKmd", "ControlledNumberingTool.dll")
            };

            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null)
            {
                throw new FileNotFoundException(
                    "Не найден ControlledNumberingTool.dll. Повторно выполните развёртывание инструмента.",
                    candidates[0]);
            }

            return found;
        }

        private static void ShowError(Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить контролируемую нумерацию.\r\n\r\n" + ex.Message,
                "Контроль нумерации",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

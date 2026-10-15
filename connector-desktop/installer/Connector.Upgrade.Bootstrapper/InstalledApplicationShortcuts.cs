using System.Runtime.InteropServices;
using Connector.Upgrade.Velopack;

namespace Connector.Upgrade.Bootstrapper;

internal static class InstalledApplicationShortcuts
{
    public static void Publish(string installRoot)
    {
        var launcher=Path.Combine(installRoot,UnifiedVelopackApplication.RootLauncher);
        var executable=Path.Combine(installRoot,"current",UnifiedVelopackApplication.MainExecutable);
        if(!File.Exists(launcher)||!File.Exists(executable))
            throw new InvalidOperationException("Не удалось создать ярлыки приложения. Восстановите установку.");
        var shellType=Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Служба ярлыков Windows недоступна.");
        object shell=Activator.CreateInstance(shellType)!;
        try
        {
            foreach(var location in new[] {Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.Programs})
            {
                var directory=Environment.GetFolderPath(location,Environment.SpecialFolderOption.Create);
                if(string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Не удалось найти папку для ярлыков приложения.");
                Directory.CreateDirectory(directory);
                object shortcut=((dynamic)shell).CreateShortcut(Path.Combine(directory,"Structura Connector.lnk"));
                try
                {
                    ((dynamic)shortcut).TargetPath=launcher;
                    ((dynamic)shortcut).Arguments="";
                    ((dynamic)shortcut).WorkingDirectory=installRoot;
                    ((dynamic)shortcut).Description="Structura Connector";
                    ((dynamic)shortcut).IconLocation=executable+",0";
                    ((dynamic)shortcut).Save();
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
                var previous=Path.Combine(directory,"Structura Connector (новый).lnk");
                if(!File.Exists(previous)) continue;
                object old=((dynamic)shell).CreateShortcut(previous);
                bool owned;
                try
                {
                    var target=(string)((dynamic)old).TargetPath;
                    owned=!string.IsNullOrWhiteSpace(target) && Path.GetFullPath(target).StartsWith(Path.TrimEndingDirectorySeparator(installRoot)+Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
                }
                finally { Marshal.FinalReleaseComObject(old); }
                if(owned) File.Delete(previous);
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}

using System.Runtime.ExceptionServices;
using Connector.Upgrade.Velopack;
using Xunit;

namespace Connector.Upgrade.Bootstrapper.Tests;

public sealed class InstalledApplicationShortcutsTests
{
    [Fact]
    public void NativeUnicodeLinksUseLauncherAndApplicationIconAndRepeatSafely()
    {
        RunSta(() =>
        {
            using var fixture = new ShortcutFixture();
            InstalledApplicationShortcuts.Publish(fixture.Root, fixture.Directories);
            InstalledApplicationShortcuts.Publish(fixture.Root, fixture.Directories);
            foreach (var directory in fixture.Directories)
            {
                var link = InstalledApplicationShortcuts.Read(Path.Combine(directory, "Платформа Коннектор.lnk"));
                Assert.Equal(fixture.Launcher, link.Target, ignoreCase: true);
                Assert.Equal(fixture.Executable, link.Icon, ignoreCase: true);
                Assert.Equal(0, link.IconIndex);
                Assert.Equal(string.Empty, link.Arguments);
                Assert.Equal(fixture.Root, link.WorkingDirectory, ignoreCase: true);
                Assert.Empty(Directory.EnumerateFiles(directory, ".platform-shortcut-*.lnk"));
            }
        });
    }

    [Fact]
    public void RemovesOnlyOwnedLegacyShortcutsAndPreservesForeignTargets()
    {
        RunSta(() =>
        {
            using var fixture = new ShortcutFixture();
            var owned = Path.Combine(fixture.Directories[0], "Structura Connector (новый).lnk");
            var foreign = Path.Combine(fixture.Directories[1], "Structura Connector.lnk");
            var outside = Path.Combine(fixture.Base, "чужая программа.exe");
            File.WriteAllText(outside, string.Empty);
            InstalledApplicationShortcuts.Write(owned, fixture.Launcher, fixture.Executable, fixture.Root);
            InstalledApplicationShortcuts.Write(foreign, outside, outside, fixture.Base);
            var before = File.ReadAllBytes(foreign);

            InstalledApplicationShortcuts.Publish(fixture.Root, fixture.Directories);

            Assert.False(File.Exists(owned));
            Assert.True(File.Exists(foreign));
            Assert.Equal(before, File.ReadAllBytes(foreign));
            Assert.Equal(outside, InstalledApplicationShortcuts.Read(foreign).Target, ignoreCase: true);
        });
    }

    private static void RunSta(Action action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native shortcut tests require Windows.");
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } })
            { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native shortcut test exceeded its bound.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ShortcutFixture : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "Platform-Shortcut-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Launcher { get; }
        public string Executable { get; }
        public string[] Directories { get; }

        public ShortcutFixture()
        {
            Root = Path.Combine(Base, "Каталог приложения");
            Launcher = Path.Combine(Root, UnifiedVelopackApplication.RootLauncher);
            Executable = Path.Combine(Root, "current", UnifiedVelopackApplication.MainExecutable);
            Directories = [Path.Combine(Base, "Рабочий стол"), Path.Combine(Base, "Меню Пуск")];
            Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
            foreach (var directory in Directories) Directory.CreateDirectory(directory);
            File.WriteAllText(Launcher, string.Empty);
            File.WriteAllText(Executable, string.Empty);
        }

        public void Dispose()
        {
            var full = Path.GetFullPath(Base);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("Platform-Shortcut-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its own temporary directory.");
            Directory.Delete(full, recursive: true);
        }
    }
}

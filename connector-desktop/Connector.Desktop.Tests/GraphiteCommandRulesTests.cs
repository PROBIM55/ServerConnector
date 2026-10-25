using System.Windows;
using Connector.Desktop.Features.GraphiteWeb;
using Connector.Desktop.Models;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class GraphiteCommandRulesTests
{
    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("Light", null)]
    [InlineData("sepia", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void UiTheme_AcceptsOnlyKnownThemes(string? requested, string? expected)
        => Assert.Equal(expected, GraphiteCommandRules.NormalizeTheme(requested));

    [Theory]
    [InlineData("window-minimize", WindowState.Normal, WindowState.Minimized)]
    [InlineData("window-maximize", WindowState.Normal, WindowState.Maximized)]
    [InlineData("window-maximize", WindowState.Maximized, WindowState.Normal)]
    public void WindowCommands_MapToWindowStates(string command, WindowState current, WindowState expected)
        => Assert.Equal(expected, GraphiteCommandRules.NextWindowState(command, current));

    [Fact]
    public void WindowClose_MeansClose_AndUnknownCommandIsRefused()
    {
        Assert.Null(GraphiteCommandRules.NextWindowState("window-close", WindowState.Normal));
        Assert.True(GraphiteCommandRules.IsWindowCommand("window-close"));
        Assert.False(GraphiteCommandRules.IsWindowCommand("window-move"));
        Assert.Throws<GraphiteUserException>(() => GraphiteCommandRules.NextWindowState("window-move", WindowState.Normal));
    }

    [Fact]
    public void TeklaFolders_ComeFromPickerOrSettings_NeverFromTheDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphite-tekla-folders-" + Guid.NewGuid());
        var saved = Directory.CreateDirectory(Path.Combine(root, "saved")).FullName;
        var picked = Directory.CreateDirectory(Path.Combine(root, "picked")).FullName;
        try
        {
            var settings = new AppSettings { TeklaStandardLocalPath = saved, TeklaExtensionsLocalPath = saved, TeklaLibrariesLocalPath = "" };
            var none = new Dictionary<string, string>();
            Assert.Equal(saved, GraphiteCommandRules.ResolveTeklaFolder("xs-open", none, settings));
            Assert.Equal(picked, GraphiteCommandRules.ResolveTeklaFolder("ext-open",
                new Dictionary<string, string> { ["ext-path"] = picked }, settings));
            // A picked folder of another section does not leak into this one.
            Assert.Equal(saved, GraphiteCommandRules.ResolveTeklaFolder("xs-open",
                new Dictionary<string, string> { ["ext-path"] = picked }, settings));
            Assert.Equal("Папка ещё не выбрана. Нажмите «Изменить папку».",
                Assert.Throws<GraphiteUserException>(() => GraphiteCommandRules.ResolveTeklaFolder("lib-open", none, settings)).Message);
            settings.TeklaLibrariesLocalPath = Path.Combine(root, "missing");
            Assert.StartsWith("Папка не найдена", Assert.Throws<GraphiteUserException>(() =>
                GraphiteCommandRules.ResolveTeklaFolder("lib-open", none, settings)).Message);
            settings.TeklaLibrariesLocalPath = "relative\\folder";
            Assert.Throws<GraphiteUserException>(() => GraphiteCommandRules.ResolveTeklaFolder("lib-open", none, settings));
            Assert.Throws<GraphiteUserException>(() => GraphiteCommandRules.ResolveTeklaFolder("publish-open", none, settings));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Готово", true)]
    [InlineData("Отменено", true)]
    [InlineData("Ошибка", true)]
    [InlineData("Превышено время", true)]
    [InlineData("Прервано", true)]
    [InlineData("В очереди", false)]
    [InlineData("Принято исполнителем", false)]
    [InlineData("Выполняется", false)]
    public void HistoryClear_RemovesOnlyFinishedJobs(string state, bool finished)
        => Assert.Equal(finished, GraphiteCommandRules.IsFinishedJobState(state));

    [Fact]
    public async Task UserErrors_ReachTheDocumentWithTheirMessage_OtherErrorsDoNot()
    {
        string? refused = null;
        Task<object?> Refuse(string message) { refused = message; return Task.FromResult<object?>(message); }

        var result = await GraphiteCommandRules.RefuseUserErrors(
            () => throw new GraphiteUserException("Выберите папку проекта."), Refuse);
        Assert.Equal("Выберите папку проекта.", refused);
        Assert.Equal("Выберите папку проекта.", result);

        refused = null;
        // Other exceptions are not turned into user messages (the host reports a fixed failure text instead).
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = GraphiteCommandRules.RefuseUserErrors(() => throw new InvalidOperationException("internal detail"), Refuse);
        });
        Assert.Null(refused);
        Assert.Equal("ok", await GraphiteCommandRules.RefuseUserErrors(() => Task.FromResult<object?>("ok"), Refuse));
    }

    [Fact]
    public void OptionalWebViewFeature_FailureIsReportedNotThrown()
    {
        var log = new List<string>();
        Assert.True(GraphiteWebSecurity.TryEnableOptional(() => { }, log.Add, "non-client region"));
        Assert.False(GraphiteWebSecurity.TryEnableOptional(() => throw new NotImplementedException(), log.Add, "non-client region"));
        Assert.Single(log);
        Assert.Contains("non-client region", log[0]);
        Assert.Contains(nameof(NotImplementedException), log[0]);
    }
}

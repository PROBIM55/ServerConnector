using System.Windows;
using System.Windows.Controls;

namespace Connector.Desktop;

public partial class ReleaseNotesWindow : Window
{
    private readonly IReadOnlyList<ReleaseNoteItem> _releaseNotes;

    public ReleaseNotesWindow(IReadOnlyList<ReleaseNoteItem> releaseNotes, string preferredVersion)
    {
        InitializeComponent();
        _releaseNotes = releaseNotes
            .Where(note => !string.IsNullOrWhiteSpace(note.Version))
            .ToList();

        VersionsListBox.ItemsSource = _releaseNotes;
        if (_releaseNotes.Count == 0)
        {
            ShowEmptyState();
            return;
        }

        var preferred = _releaseNotes.FirstOrDefault(x =>
            string.Equals(x.Version, preferredVersion, StringComparison.OrdinalIgnoreCase));
        VersionsListBox.SelectedItem = preferred ?? _releaseNotes[0];
    }

    private void VersionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionsListBox.SelectedItem is ReleaseNoteItem item)
        {
            Render(item);
        }
    }

    private void Render(ReleaseNoteItem item)
    {
        VersionHeaderTextBlock.Text = "Версия " + item.Version;
        VersionMetaTextBlock.Text = "Релиз от " + item.PublishedAt;
        VersionTitleTextBlock.Text = string.IsNullOrWhiteSpace(item.Title) ? "Изменения релиза" : item.Title;
        ChangesItemsControl.ItemsSource = item.Changes.Count > 0
            ? item.Changes
            : new[] { "Для этой версии список изменений пока не заполнен" };
    }

    private void ShowEmptyState()
    {
        VersionHeaderTextBlock.Text = "Пока пусто";
        VersionMetaTextBlock.Text = "Список релизов не заполнен";
        VersionTitleTextBlock.Text = "Что нового";
        ChangesItemsControl.ItemsSource = new[]
        {
            "Добавьте записи о релизах, чтобы пользователи видели изменения по версиям"
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

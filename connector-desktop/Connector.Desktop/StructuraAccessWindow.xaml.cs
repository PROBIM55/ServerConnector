using System.Windows;

namespace Connector.Desktop;

public partial class StructuraAccessWindow : Window
{
    public StructuraAccessWindow(string title, string domain, string login, string password)
    {
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = title;
        DomainTextBox.Text = domain;
        LoginTextBox.Text = login;
        PasswordTextBox.Text = password;
    }

    private void CopyDomain_Click(object sender, RoutedEventArgs e) => CopyValue(DomainTextBox.Text, "Домен");

    private void CopyLogin_Click(object sender, RoutedEventArgs e) => CopyValue(LoginTextBox.Text, "Логин");

    private void CopyPassword_Click(object sender, RoutedEventArgs e) => CopyValue(PasswordTextBox.Text, "Пароль");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyValue(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CopyStatusTextBlock.Text = label + ": нечего копировать";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(value);
            CopyStatusTextBlock.Text = label + " скопирован";
        }
        catch
        {
            CopyStatusTextBlock.Text = "Не удалось скопировать значение";
        }
    }
}

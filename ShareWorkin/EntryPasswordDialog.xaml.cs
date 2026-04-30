using System.Windows;
using System.Windows.Input;

namespace ShareWorkin;

public partial class EntryPasswordDialog : Window
{
    public string EnteredPassword => PasswordBox.Password;

    public EntryPasswordDialog(bool isSetup, string? initialStatus = null)
    {
        InitializeComponent();
        if (isSetup)
        {
            TitleTextBlock.Text = "アプリ用パスワードを決める";
            HintTextBlock.Text = "この画面を開くためのパスワードを決めてください。";
        }
        StatusTextBlock.Text = initialStatus ?? string.Empty;
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
    }
}

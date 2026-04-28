using System.Windows;

namespace ShareWorkin;

public partial class NameInputDialog : Window
{
    public string EnteredName => NameTextBox.Text;

    public NameInputDialog(string prompt, string initialValue)
    {
        InitializeComponent();
        PromptTextBlock.Text = prompt;
        NameTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ShareWorkin;

public partial class InteractionActionConfirmDialog : Window
{
    public string NotificationMessage => MessageTextBox.Text.Trim();

    public InteractionActionConfirmDialog(
        string friendLabel,
        string actionLabel,
        IReadOnlyList<string> targetNames)
    {
        InitializeComponent();

        string normalizedFriendLabel = string.IsNullOrWhiteSpace(friendLabel) ? "相手" : friendLabel;
        ActionSummaryTextBlock.Text = $"{actionLabel} を実行し、{normalizedFriendLabel} に通知します。";
        FriendSummaryTextBlock.Text = $"通知対象: {normalizedFriendLabel}";

        foreach (string name in targetNames
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct())
        {
            TargetsListBox.Items.Add(name);
        }

        if (TargetsListBox.Items.Count == 0)
        {
            TargetsListBox.Items.Add("対象項目");
        }
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

using System;
using System.Linq;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class ImportInviteDialog : Window
{
    public Friend? Imported { get; private set; }

    public ImportInviteDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TokenTextBox.Focus();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = string.Empty;
        string raw = TokenTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusTextBlock.Text = "招待コードを貼り付けてください。";
            return;
        }

        if (!InviteToken.TryDecode(raw, out InviteTokenPayload? payload, out string? error) || payload is null)
        {
            StatusTextBlock.Text = error ?? "招待コードを読み取れませんでした。";
            return;
        }

        string display = string.IsNullOrWhiteSpace(DisplayNameTextBox.Text)
            ? payload.HostMachineName
            : DisplayNameTextBox.Text.Trim();

        var existing = FriendsRepository.LoadAll().ToList();

        existing.RemoveAll(f =>
            string.Equals(f.HostMachineName, payload.HostMachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.ShareName, payload.ShareName, StringComparison.OrdinalIgnoreCase));

        Friend friend = new()
        {
            DisplayName = display,
            HostMachineName = payload.HostMachineName,
            ShareName = payload.ShareName,
            UserName = payload.UserName,
            PasswordProtected = FriendsRepository.ProtectPassword(payload.Password),
            AccessLevel = payload.AccessLevel,
            ProfileLabel = payload.ProfileLabel,
            AddedAt = DateTime.UtcNow.ToString("o"),
        };

        existing.Add(friend);
        if (!FriendsRepository.SaveAll(existing))
        {
            StatusTextBlock.Text = "お友達リストに保存できませんでした。";
            return;
        }

        Imported = friend;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

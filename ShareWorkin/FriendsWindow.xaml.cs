using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class FriendsWindow : Window
{
    private readonly Window _ownerForDialogs;
    private readonly string? _ownerShopFolder;
    private readonly ObservableCollection<FriendRow> _rows = new();

    public FriendsWindow(Window owner, string? ownerShopFolder)
    {
        InitializeComponent();
        _ownerForDialogs = owner;
        _ownerShopFolder = ownerShopFolder;
        FriendsListView.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAndCheckPresenceAsync();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (Friend f in FriendsRepository.LoadAll())
        {
            _rows.Add(new FriendRow(f));
        }
        if (_rows.Count == 0)
        {
            StatusTextBlock.Text = "まだお友達はいません。「招待を取り込む」から始めましょう。";
        }
        else
        {
            StatusTextBlock.Text = string.Empty;
        }
    }

    private async Task ReloadAndCheckPresenceAsync()
    {
        Reload();
        if (_rows.Count == 0)
        {
            return;
        }

        StatusTextBlock.Text = "お友達のお店を探しています。";

        IReadOnlyList<LanCandidate> candidates;
        try
        {
            candidates = await LanScanner.ScanAsync();
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Lan scan failed: {ex.Message}");
            StatusTextBlock.Text = "お友達の今いる場所を確認できませんでした。";
            return;
        }

        List<Friend> friends = FriendsRepository.LoadAll().ToList();
        int foundCount = 0;
        int movedCount = 0;
        string checkedAt = DateTime.UtcNow.ToString("o");

        // Presence checking only reconciles already-invited shops. Unknown SMB
        // hosts on the LAN are intentionally not promoted into friend candidates.
        foreach (Friend friend in friends)
        {
            friend.LastCheckedAt = checkedAt;
            LanCandidate? match = candidates.FirstOrDefault(c => IsSameHost(friend.HostMachineName, c.HostName));
            if (match is null)
            {
                continue;
            }

            string address = match.Address.ToString();
            if (!string.IsNullOrWhiteSpace(friend.LastKnownAddress) &&
                !string.Equals(friend.LastKnownAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                movedCount++;
            }

            friend.LastKnownAddress = address;
            friend.LastFoundAt = checkedAt;
            foundCount++;
        }

        FriendsRepository.SaveAll(friends);
        Reload();

        StatusTextBlock.Text = movedCount > 0
            ? $"{foundCount} 件のお店を見つけました。{movedCount} 件は場所が変わっていました。"
            : $"{foundCount} 件のお店を見つけました。";
    }

    private FriendRow? GetSelected() => FriendsListView.SelectedItem as FriendRow;

    private void FriendsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        FriendRow? row = GetSelected();
        if (row is null)
        {
            StatusTextBlock.Text = "お友達を選んでください。";
            return;
        }

        Friend friend = row.Source;
        string password = FriendsRepository.UnprotectPassword(friend.PasswordProtected);
        if (!string.IsNullOrEmpty(password))
        {
            RegisterCredential(friend.ConnectHost, BuildRemoteUserName(friend.ConnectHost, friend.UserName), password);
        }

        string unc = friend.ConnectUncPath;
        if (string.IsNullOrEmpty(unc))
        {
            StatusTextBlock.Text = "お友達のお店の場所がわかりません。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{unc}\"",
                UseShellExecute = true,
            });
            StatusTextBlock.Text = $"{friend.DisplayName} のお店を開きました。";
            friend.LastSeenAt = DateTime.UtcNow.ToString("o");
            PersistChanges();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
        {
            SwkLogger.Warn($"Open friend shop failed: {ex.Message}");
            StatusTextBlock.Text = "お店を開けませんでした。";
        }
    }

    private static bool IsSameHost(string expectedHost, string? foundHost)
    {
        string expected = NormalizeHostName(expectedHost);
        string found = NormalizeHostName(foundHost);
        return !string.IsNullOrWhiteSpace(expected) &&
               !string.IsNullOrWhiteSpace(found) &&
               string.Equals(expected, found, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        string trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportInviteDialog dialog = new() { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Imported is not null)
        {
            Reload();
            StatusTextBlock.Text = $"{dialog.Imported.DisplayName} のお店を取り込みました。";
        }
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        FriendRow? row = GetSelected();
        if (row is null)
        {
            StatusTextBlock.Text = "お友達を選んでください。";
            return;
        }

        NameInputDialog dialog = new("呼び名を入れてください。", row.Source.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string newName = dialog.EnteredName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        row.Source.DisplayName = newName;
        PersistChanges();
        Reload();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        FriendRow? row = GetSelected();
        if (row is null)
        {
            StatusTextBlock.Text = "お友達を選んでください。";
            return;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"「{row.Source.DisplayName}」をお友達から外します。よろしいですか?",
            "ShareWorkin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        List<Friend> all = FriendsRepository.LoadAll().ToList();
        all.RemoveAll(f => string.Equals(f.Id, row.Source.Id, StringComparison.Ordinal));
        FriendsRepository.SaveAll(all);
        Reload();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void PersistChanges()
    {
        List<Friend> all = _rows.Select(r => r.Source).ToList();
        FriendsRepository.SaveAll(all);
    }

    private static void RegisterCredential(string host, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "cmdkey.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add($"/add:{host}");
            psi.ArgumentList.Add($"/user:{user}");
            psi.ArgumentList.Add($"/pass:{password}");

            using Process? process = Process.Start(psi);
            if (process is null) return;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is Win32Exception or System.IO.FileNotFoundException)
        {
            SwkLogger.Warn($"cmdkey register failed: {ex.Message}");
        }
    }

    private static string BuildRemoteUserName(string host, string user)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return user;
        }

        return user.Contains('\\', StringComparison.Ordinal) || user.Contains('@', StringComparison.Ordinal)
            ? user
            : $@"{host}\{user}";
    }

    private sealed class FriendRow
    {
        public Friend Source { get; }
        public string DisplayName => Source.DisplayName;
        public string ShareName => Source.ShareName;
        public string LocationLabel => string.IsNullOrWhiteSpace(Source.LastKnownAddress)
            ? Source.UncPath
            : $@"\\{Source.LastKnownAddress}\{Source.ShareName}";
        public string PresenceLabel => string.IsNullOrWhiteSpace(Source.LastCheckedAt)
            ? "未確認"
            : Source.IsCurrentlyFound
                ? "来店可能"
                : "不在";
        public string AccessLabel => string.Equals(Source.AccessLevel, "Read", StringComparison.OrdinalIgnoreCase)
            ? "見るだけ"
            : "自由に編集";

        public FriendRow(Friend source)
        {
            Source = source;
        }
    }
}

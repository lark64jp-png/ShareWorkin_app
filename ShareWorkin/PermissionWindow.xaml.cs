using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class PermissionWindow : Window
{
    private readonly ShopItem _target;
    private readonly ObservableCollection<string> _allowed = new();
    private readonly ObservableCollection<string> _unset = new();
    private bool _isInitializing = true;

    public PermissionWindow(ShopItem target)
    {
        InitializeComponent();
        _isInitializing = true;
        _target = target;
        Title = $"許可指定  {target.Name}";
        TargetItemTextBlock.Text = target.Name;

        ReadWriteRadio.IsChecked = !target.IsReadOnly && !target.IsSharedOff;
        ReadOnlyRadio.IsChecked = target.IsReadOnly && !target.IsSharedOff;
        SharedOffRadio.IsChecked = target.IsSharedOff;

        AllowedListBox.ItemsSource = _allowed;
        UnsetListBox.ItemsSource = _unset;

        foreach (string user in target.AllowedUsers)
        {
            _allowed.Add(user);
        }

        ReloadUnsetUsers();

        _allowed.CollectionChanged += (_, _) => UpdateOverlay();
        _unset.CollectionChanged += (_, _) => UpdateUnsetOverlay();
        _isInitializing = false;
        if (SharedOffRadio.IsChecked == true)
        {
            _allowed.Clear();
            ReloadUnsetUsers();
        }
        ApplyAccessLevelUiState();
        UpdateHintText();
        UpdateOverlay();
        UpdateUnsetOverlay();
    }

    private void UpdateOverlay()
    {
        EveryoneOverlay.Text = "全員";
        EveryoneOverlay.Visibility = _allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUnsetOverlay()
    {
        UnsetOverlay.Visibility = _unset.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AccessLevelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (SharedOffRadio.IsChecked == true)
        {
            _allowed.Clear();
            ReloadUnsetUsers();
        }

        ApplyAccessLevelUiState();
        UpdateHintText();
    }

    private void ApplyAccessLevelUiState()
    {
        bool canSpecifyUsers = SharedOffRadio.IsChecked != true;
        AllowedListBox.IsEnabled = canSpecifyUsers;
        UnsetListBox.IsEnabled = canSpecifyUsers;
        MoveLeftButton.IsEnabled = canSpecifyUsers;
        MoveRightButton.IsEnabled = canSpecifyUsers;
    }

    private void UpdateHintText()
    {
        HintTextBlock.Text = SharedOffRadio.IsChecked == true
            ? "共有OFFでは、この項目は誰にも共有されません。ユーザー指定は使いません。"
            : ReadOnlyRadio.IsChecked == true
                ? "左に誰もいないと「全員」が読みのみできます。左に入れた相手だけが指定対象になります。"
                : "左に誰もいないと「全員」が読み書きできます。左に入れた相手だけが指定対象になります。";
    }

    private void ReloadUnsetUsers()
    {
        _unset.Clear();
        HashSet<string> already = new(_allowed, StringComparer.OrdinalIgnoreCase);
        foreach (Friend f in FriendsRepository.LoadAll())
        {
            string name = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || already.Contains(name)) continue;
            _unset.Add(name);
        }
    }

    private void MoveLeftButton_Click(object sender, RoutedEventArgs e) => MoveUnsetToAllowed();

    private void MoveRightButton_Click(object sender, RoutedEventArgs e) => MoveAllowedToUnset();

    private void AllowedListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => MoveAllowedToUnset();

    private void UnsetListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => MoveUnsetToAllowed();

    private void MoveUnsetToAllowed()
    {
        List<string> picked = UnsetListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _unset.Remove(name);
            if (!_allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                _allowed.Add(name);
        }
    }

    private void MoveAllowedToUnset()
    {
        List<string> picked = AllowedListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _allowed.Remove(name);
            if (!_unset.Contains(name, StringComparer.OrdinalIgnoreCase))
                _unset.Add(name);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _target.IsSharedOff = SharedOffRadio.IsChecked == true;
        _target.IsReadOnly = ReadOnlyRadio.IsChecked == true;
        _target.AllowedUsers.Clear();
        foreach (string name in _allowed)
        {
            _target.AllowedUsers.Add(name);
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

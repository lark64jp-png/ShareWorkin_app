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

    public PermissionWindow(ShopItem target)
    {
        InitializeComponent();
        _target = target;
        Title = $"共有設定  {target.Name}";
        TargetItemTextBlock.Text = target.Name;

        ReadWriteRadio.IsChecked = !target.IsReadOnly && !target.IsSharedOff;
        ReadOnlyRadio.IsChecked = target.IsReadOnly && !target.IsSharedOff;
        SharedOffRadio.IsChecked = target.IsSharedOff;

        AllowedListBox.ItemsSource = _allowed;
        UnsetListBox.ItemsSource = _unset;

        foreach (Friend f in FriendsRepository.LoadAll())
        {
            string name = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            _unset.Add(name);
        }

        _allowed.CollectionChanged += (_, _) => UpdateOverlay();
        _unset.CollectionChanged += (_, _) => UpdateUnsetOverlay();
        UpdateOverlay();
        UpdateUnsetOverlay();
    }

    private void UpdateOverlay()
    {
        EveryoneOverlay.Visibility = _allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUnsetOverlay()
    {
        UnsetOverlay.Visibility = _unset.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // v1.13 keeps the live ACL surface to 全員 / 読みのみ / OFF.
        // Per-friend selection needs per-friend credentials before it can be honest.
        _target.AllowedUsers.Clear();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

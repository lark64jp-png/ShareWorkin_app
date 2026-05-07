using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
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
        Title = $"許可指定  {target.Name}";
        TargetItemTextBlock.Text = target.Name;

        ReadWriteRadio.IsChecked = !target.IsReadOnly;
        ReadOnlyRadio.IsChecked = target.IsReadOnly;

        AllowedListBox.ItemsSource = _allowed;
        UnsetListBox.ItemsSource = _unset;

        foreach (string user in target.AllowedUsers)
        {
            _allowed.Add(user);
        }

        HashSet<string> already = new(_allowed, StringComparer.OrdinalIgnoreCase);
        foreach (Friend f in FriendsRepository.LoadAll())
        {
            string name = string.IsNullOrWhiteSpace(f.DisplayName) ? f.HostMachineName : f.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || already.Contains(name)) continue;
            _unset.Add(name);
        }

        _allowed.CollectionChanged += (_, _) => UpdateOverlay();
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        EveryoneOverlay.Visibility = _allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoveLeftButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> picked = UnsetListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _unset.Remove(name);
            if (!_allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _allowed.Add(name);
            }
        }
    }

    private void MoveRightButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> picked = AllowedListBox.SelectedItems.Cast<string>().ToList();
        foreach (string name in picked)
        {
            _allowed.Remove(name);
            if (!_unset.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _unset.Add(name);
            }
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // In-memory commit only. NTFS ACL writes are deferred to v2.2 wiring.
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

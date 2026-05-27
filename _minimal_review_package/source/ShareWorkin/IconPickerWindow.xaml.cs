using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Win32;
using ShareWorkin.SMB;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace ShareWorkin;

public partial class IconPickerWindow : Window
{
    // 結果。確定時に値が入る。
    public string? SelectedIconKey { get; private set; }
    public bool Picked { get; private set; }

    private readonly string _friendId;
    private readonly string _initialKey;

    // プレビューの状態。ライブラリ選択 → _stagedLibraryKey、画像指定 → _stagedCustomPath。
    // 確定時にこれを実体に変換して SelectedIconKey に詰める。
    private string? _stagedLibraryKey;
    private string? _stagedCustomPath;
    private bool _stagedClear;
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png" };

    public IconPickerWindow(Window owner, string currentIconKey, string friendId)
    {
        InitializeComponent();
        Owner = owner;
        _friendId = friendId;
        _initialKey = currentIconKey ?? string.Empty;
        BuildLibrary();
        ShowInitialPreview();
    }

    private void BuildLibrary()
    {
        List<LibraryItem> items = new();
        foreach (string name in IconService.LibraryNames)
        {
            string key = IconService.MakeLibraryKey(name);
            BitmapImage? img = LoadBitmap(IconService.ResolvePath(key));
            items.Add(new LibraryItem
            {
                Key = key,
                ImageSource = img,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
            });
        }
        LibraryItems.ItemsSource = items;
        HighlightLibrary(_initialKey);
    }

    private void HighlightLibrary(string? selectedKey)
    {
        if (LibraryItems.ItemsSource is not IEnumerable<LibraryItem> items) return;
        Brush selected = new SolidColorBrush(Color.FromRgb(74, 124, 89));
        Brush plain = Brushes.Transparent;
        foreach (LibraryItem item in items)
        {
            bool match = selectedKey != null && string.Equals(item.Key, selectedKey, StringComparison.Ordinal);
            item.BorderBrush = match ? selected : plain;
            item.BorderThickness = new Thickness(match ? 2 : 1);
        }
        LibraryItems.Items.Refresh();
    }

    private void ShowInitialPreview()
    {
        string? path = IconService.ResolvePath(_initialKey);
        if (path != null)
        {
            SetPreviewFromFile(path);
        }
        else
        {
            ShowEmptyPreview();
        }
        UpdateConfirmState();
    }

    private void SetPreviewFromFile(string path)
    {
        BitmapImage? img = LoadBitmap(path);
        if (img == null)
        {
            ShowEmptyPreview();
            return;
        }
        PreviewImage.Source = img;
        PreviewImage.Visibility = Visibility.Visible;
        DropHintTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowEmptyPreview()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        DropHintTextBlock.Visibility = Visibility.Visible;
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            BitmapImage img = new();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"IconPicker: failed to load image {path}: {ex.Message}");
            return null;
        }
    }

    private void LibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        SwkLogger.Debug($"IconPicker: library staged {key}");
        _stagedLibraryKey = key;
        _stagedCustomPath = null;
        _stagedClear = false;
        string? path = IconService.ResolvePath(key);
        if (path != null) SetPreviewFromFile(path); else ShowEmptyPreview();
        HighlightLibrary(key);
        UpdateConfirmState();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dlg = new()
        {
            Title = "アイコン画像を選ぶ",
            Filter = "画像ファイル (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        StageCustomPath(dlg.FileName);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SwkLogger.Debug("IconPicker: cleared (staged)");
        _stagedLibraryKey = null;
        _stagedCustomPath = null;
        _stagedClear = true;
        ShowEmptyPreview();
        HighlightLibrary(null);
        UpdateConfirmState();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        // ステージされたものを実体化する。
        if (_stagedLibraryKey != null)
        {
            SelectedIconKey = _stagedLibraryKey;
            Picked = true;
        }
        else if (_stagedCustomPath != null)
        {
            try
            {
                SelectedIconKey = IconService.ImportCustom(_stagedCustomPath, _friendId);
                Picked = true;
            }
            catch (Exception ex)
            {
                SwkLogger.Warn($"IconPicker: import on confirm failed: {ex.Message}");
                System.Windows.MessageBox.Show(this, $"画像を取り込めませんでした: {ex.Message}",
                    "アイコン", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (_stagedClear)
        {
            SelectedIconKey = string.Empty;
            Picked = true;
        }
        else
        {
            // 何もステージされていない（変更なし）
            DialogResult = false;
            Close();
            return;
        }
        SwkLogger.Debug($"IconPicker: confirmed {SelectedIconKey}");
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ── ドラッグ＆ドロップ ─────────────────────────────────────────

    private void PreviewDropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetSingleImageFile(e, out _))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            PreviewDropZone.Background = new SolidColorBrush(Color.FromRgb(238, 244, 235));
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void PreviewDropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        PreviewDropZone.Background = new SolidColorBrush(Color.FromRgb(250, 247, 240));
        e.Handled = true;
    }

    private void PreviewDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        PreviewDropZone.Background = new SolidColorBrush(Color.FromRgb(250, 247, 240));
        if (TryGetSingleImageFile(e, out string? path) && path != null)
        {
            StageCustomPath(path);
        }
        e.Handled = true;
    }

    private static bool TryGetSingleImageFile(System.Windows.DragEventArgs e, out string? path)
    {
        path = null;
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
        string candidate = files[0];
        string ext = Path.GetExtension(candidate).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions, ext) < 0) return false;
        path = candidate;
        return true;
    }

    private void StageCustomPath(string path)
    {
        SwkLogger.Debug($"IconPicker: custom staged {path}");
        _stagedLibraryKey = null;
        _stagedCustomPath = path;
        _stagedClear = false;
        SetPreviewFromFile(path);
        HighlightLibrary(null);
        UpdateConfirmState();
    }

    private void UpdateConfirmState()
    {
        bool hasChange = _stagedLibraryKey != null || _stagedCustomPath != null || _stagedClear;
        ConfirmButton.IsEnabled = hasChange;
    }

    // ── ハイパーリンク ────────────────────────────────────────────

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            SwkLogger.Debug($"IconPicker: opened {e.Uri.AbsoluteUri}");
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"IconPicker: failed to open URL {e.Uri.AbsoluteUri}: {ex.Message}");
        }
        e.Handled = true;
    }

    private sealed class LibraryItem
    {
        public string Key { get; init; } = string.Empty;
        public ImageSource? ImageSource { get; init; }
        public Brush BorderBrush { get; set; } = Brushes.Transparent;
        public Thickness BorderThickness { get; set; } = new(1);
    }
}

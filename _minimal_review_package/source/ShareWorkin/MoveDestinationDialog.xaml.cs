using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ShareWorkin;

public partial class MoveDestinationDialog : Window
{
    private readonly string? _shopFolder;
    private readonly HashSet<string> _sourceParents;
    private readonly HashSet<string> _sourceFullPaths;

    public string? SelectedFolderPath { get; private set; }

    public MoveDestinationDialog(string? shopFolder, IReadOnlyList<string> sourcePaths)
    {
        InitializeComponent();

        _shopFolder = shopFolder;
        _sourceParents = sourcePaths
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sourceFullPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BuildTree();
    }

    private void BuildTree()
    {
        if (!string.IsNullOrWhiteSpace(_shopFolder) && Directory.Exists(_shopFolder))
        {
            string shopLabel = Path.GetFileName(
                _shopFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(shopLabel))
            {
                shopLabel = "お店";
            }
            TreeViewItem shopRoot = CreateNode(_shopFolder, shopLabel);
            shopRoot.IsExpanded = true;
            DestinationTreeView.Items.Add(shopRoot);
        }

    }

    private TreeViewItem CreateNode(string folderPath, string label)
    {
        TreeViewItem item = new()
        {
            Header = label,
            Tag = folderPath
        };

        if (HasAccessibleSubdirectory(folderPath))
        {
            item.Items.Add(CreatePlaceholder());
            item.Expanded += TreeViewItem_Expanded;
        }

        return item;
    }

    private static TreeViewItem CreatePlaceholder()
    {
        return new TreeViewItem
        {
            Header = string.Empty,
            Tag = null
        };
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string path)
        {
            return;
        }

        if (item.Items.Count != 1)
        {
            return;
        }

        if (item.Items[0] is not TreeViewItem placeholder || placeholder.Tag is not null)
        {
            return;
        }

        item.Items.Clear();

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(path).OrderBy(p => p))
            {
                if (IsSourceFolder(sub))
                {
                    continue;
                }
                item.Items.Add(CreateNode(sub, Path.GetFileName(sub)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool HasAccessibleSubdirectory(string folderPath)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(folderPath))
            {
                if (IsSourceFolder(sub))
                {
                    continue;
                }
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private bool IsSourceFolder(string path)
    {
        return _sourceFullPaths.Contains(NormalizePath(path));
    }

    private void DestinationTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path } && Directory.Exists(path))
        {
            bool isSourceParent = _sourceParents.Contains(NormalizePath(path));
            MoveButton.IsEnabled = !isSourceParent;
            SelectedFolderPath = path;
        }
        else
        {
            MoveButton.IsEnabled = false;
            SelectedFolderPath = null;
        }
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedFolderPath))
        {
            return;
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

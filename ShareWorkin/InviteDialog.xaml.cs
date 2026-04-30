using System;
using System.IO;
using System.Text;
using System.Windows;
using ShareWorkin.SMB;

namespace ShareWorkin;

public partial class InviteDialog : Window
{
    private readonly string _shareName;
    private readonly ShareAccessRight _accessRight;
    private string _tokenString = string.Empty;

    public InviteDialog(string shareName, ShareAccessRight accessRight)
    {
        InitializeComponent();
        _shareName = shareName;
        _accessRight = accessRight;

        Loaded += (_, _) => GenerateToken();
    }

    private void GenerateToken()
    {
        string? password = SecureStorage.Get(SecureStorage.KeySwkGuestPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            HintTextBlock.Text = "招待コードを作るには、まず一度「お店を開く」を行ってお店の鍵を用意してください。";
            TokenTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            SaveSheetButton.IsEnabled = false;
            return;
        }

        InviteTokenPayload payload = new()
        {
            HostMachineName = Environment.MachineName,
            ShareName = _shareName,
            UserName = SmbAccountManager.AccountName,
            Password = password,
            AccessLevel = _accessRight == ShareAccessRight.Read ? "Read" : "Full",
            ProfileLabel = _shareName,
            IssuedAt = DateTime.UtcNow.ToString("o"),
        };

        try
        {
            _tokenString = InviteToken.Encode(payload);
        }
        catch (Exception ex)
        {
            SwkLogger.Error("InviteToken.Encode failed", ex);
            HintTextBlock.Text = "招待コードを作れませんでした。";
            TokenTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            SaveSheetButton.IsEnabled = false;
            return;
        }

        HintTextBlock.Text =
            $"お店『{_shareName}』への招待コードです。\n" +
            "「コピー」でクリップボードに貼り付け、メールやチャットでお友達に送ってください。\n" +
            "「シートで保存」を押すと、テキストファイルとして書き出せます。";
        TokenTextBox.Text = _tokenString;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenString))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_tokenString);
            StatusTextBlock.Text = "コピーしました。";
        }
        catch (Exception ex)
        {
            SwkLogger.Warn($"Clipboard copy failed: {ex.Message}");
            StatusTextBlock.Text = "コピーできませんでした。";
        }
    }

    private void SaveSheetButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenString))
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = $"ShareWorkin_招待_{_shareName}.txt",
            Filter = "テキストファイル (*.txt)|*.txt",
            Title = "招待シートを保存"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            StringBuilder sb = new();
            sb.AppendLine("ShareWorkin 招待シート");
            sb.AppendLine("======================");
            sb.AppendLine($"お店:    {_shareName}");
            sb.AppendLine($"場所:    \\\\{Environment.MachineName}\\{_shareName}");
            sb.AppendLine($"発行日時: {DateTime.Now:yyyy/MM/dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("【招待コード】");
            sb.AppendLine(_tokenString);
            sb.AppendLine();
            sb.AppendLine("【取り込み方】");
            sb.AppendLine("ShareWorkin の「お友達」→「招待を取り込む」から、上の招待コードを貼り付けてください。");
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            StatusTextBlock.Text = "シートを保存しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SwkLogger.Warn($"InviteSheet save failed: {ex.Message}");
            StatusTextBlock.Text = "シートを保存できませんでした。";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

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
        // 招待コードはお店の鍵を必要としない(パスワードは TLS 経路で別途渡る)が、
        // 開店していない状況で発行できないようにここで確認する。
        if (string.IsNullOrWhiteSpace(SecureStorage.Get(SecureStorage.KeySwkGuestPassword)))
        {
            HintTextBlock.Text = "招待コードを作れませんでした。";
            TokenTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            SaveSheetButton.IsEnabled = false;
            return;
        }

        string accessLevel = _accessRight == ShareAccessRight.Read ? "Read" : "Full";

        // InviteRegistry に未使用として記録し、招待 ID を払い出す。
        // この ID を相手が使うとき、BK が自動的に接続情報を返す。
        string inviteId = InviteRegistry.Issue(_shareName, _shareName, accessLevel);

        InviteTokenPayload payload = new()
        {
            HostMachineName = Environment.MachineName,
            ShareName = _shareName,
            UserName = SmbAccountManager.AccountName,
            AccessLevel = accessLevel,
            ProfileLabel = _shareName,
            IssuedAt = DateTime.UtcNow.ToString("o"),
            InviteId = inviteId,
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
            "お友達が取り込むと、接続情報はバックグラウンドで自動連携されます。\n" +
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

using Forms = System.Windows.Forms;

namespace ShareWorkinTray;

internal static class TrayPasswordDialog
{
    internal static string? Show(string prompt)
    {
        using var form = new Forms.Form
        {
            Text = "ShareWorkin",
            Width = 380,
            Height = 165,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var label = new Forms.Label { Text = prompt, Left = 12, Top = 12, Width = 340, Height = 40, AutoSize = false };
        var textBox = new Forms.TextBox { Left = 12, Top = 56, Width = 340, UseSystemPasswordChar = true };
        var okBtn = new Forms.Button { Text = "OK", Left = 196, Top = 88, Width = 72, DialogResult = Forms.DialogResult.OK };
        var cancelBtn = new Forms.Button { Text = "キャンセル", Left = 276, Top = 88, Width = 80, DialogResult = Forms.DialogResult.Cancel };
        form.Controls.AddRange([label, textBox, okBtn, cancelBtn]);
        form.AcceptButton = okBtn;
        form.CancelButton = cancelBtn;
        return form.ShowDialog() == Forms.DialogResult.OK ? textBox.Text : null;
    }
}

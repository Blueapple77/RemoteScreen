using RemoteScreen.Client.UI;

namespace RemoteScreen.Client;

internal static class Dialogs
{
    /// <summary>Jednoduché okno pro zadání textu. Vrací null při zrušení.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string label, string initial = "", bool password = false)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            TopMost = owner == null,
            Icon = Program.AppIcon,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = owner == null,
            ClientSize = new Size(420, 160),
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };
        form.HandleCreated += (_, _) => Theme.ApplyWindowChrome(form);

        int labelHeight = Math.Max(22, TextRenderer.MeasureText(label, Theme.Body, new Size(380, 0), TextFormatFlags.WordBreak).Height + 4);
        form.ClientSize = new Size(420, 138 + labelHeight);
        var caption = new Label { Text = label, AutoSize = false, Location = new Point(20, 18), Size = new Size(380, labelHeight), ForeColor = Theme.Text, BackColor = Color.Transparent };
        var text = new InputBox { Text = initial, Location = new Point(20, 26 + labelHeight), Width = 380, Password = password };
        var ok = new FlatButton { Text = "OK", Kind = ButtonKind.Primary, AutoFit = false, Size = new Size(100, 34), Location = new Point(196, 84 + labelHeight) };
        var cancel = new FlatButton { Text = "Zrušit", Kind = ButtonKind.Secondary, AutoFit = false, Size = new Size(100, 34), Location = new Point(300, 84 + labelHeight) };

        ok.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); };
        cancel.Click += (_, _) => { form.DialogResult = DialogResult.Cancel; form.Close(); };
        text.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; form.DialogResult = DialogResult.OK; form.Close(); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; form.DialogResult = DialogResult.Cancel; form.Close(); }
        };

        form.Controls.AddRange(new Control[] { caption, text, ok, cancel });
        form.Shown += (_, _) => { text.Box.SelectAll(); text.Box.Focus(); };

        return (owner == null ? form.ShowDialog() : form.ShowDialog(owner)) == DialogResult.OK ? text.Text : null;
    }
}

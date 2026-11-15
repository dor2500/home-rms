using System.Drawing;
using System.Windows.Forms;

namespace HomeRms.Agent.Ui;

/// <summary>Modal Allow/Deny prompt shown in the user's session before remote control.</summary>
internal static class ConsentPrompt
{
    public static bool Show(string operatorName, string action)
    {
        using var form = new Form
        {
            Text = "Home RMS — permission requested",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            ClientSize = new Size(460, 190),
            ShowInTaskbar = true
        };

        var icon = new Label
        {
            Text = "🔐",
            Font = new Font("Segoe UI Emoji", 28),
            AutoSize = true,
            Location = new Point(20, 24)
        };

        var message = new Label
        {
            Text = $"\"{operatorName}\" is asking to {action} on this computer.\n\n" +
                   "Allow only if you recognise this person and expected them to connect. " +
                   "A visible banner will stay on screen the whole time, and you can stop it any time.",
            Location = new Point(90, 20),
            Size = new Size(350, 110),
            Font = new Font("Segoe UI", 9.5f)
        };

        var allow = new Button { Text = "Allow", DialogResult = DialogResult.OK, Size = new Size(110, 32), Location = new Point(230, 140) };
        var deny = new Button { Text = "Deny", DialogResult = DialogResult.Cancel, Size = new Size(110, 32), Location = new Point(348, 140) };

        form.Controls.AddRange([icon, message, allow, deny]);
        form.AcceptButton = allow;
        form.CancelButton = deny;

        return form.ShowDialog() == DialogResult.OK;
    }
}

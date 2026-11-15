using System.Drawing;
using System.Windows.Forms;

namespace HomeRms.Agent.Ui;

/// <summary>Modal consent dialog shown in the user's session when an operator asks to view/control.</summary>
internal static class ConsentPrompt
{
    public static bool Show(string operatorName, string action)
    {
        using var form = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            Text = "Home RMS — Access Request",
            Icon = Theme.LoadAppIcon(),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            ClientSize = new Size(520, 270),
        };

        var title = new Label
        {
            Text = "Remote access request",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(28, 22),
        };

        var who = new Label
        {
            Text = $"\"{operatorName}\" wants to {action} on this computer.",
            Font = Theme.ValueFont,
            ForeColor = Theme.Accent,
            Location = new Point(28, 66),
            Size = new Size(464, 34),
        };

        var detail = new Label
        {
            Text = "Allow only if you expected this connection. A visible red banner will remain on your screen " +
                   "for the entire session, you can stop it at any time from the tray, and access expires " +
                   "automatically after one hour.",
            ForeColor = Theme.Muted,
            Font = Theme.UiFont,
            Location = new Point(28, 110),
            Size = new Size(464, 78),
        };

        var deny = Theme.Flat("Deny", Theme.Card, Theme.Text, (_, _) => form.DialogResult = DialogResult.Cancel, 125);
        var allow = Theme.Flat("Allow", Theme.Accent, Color.White, (_, _) => form.DialogResult = DialogResult.OK, 125);
        deny.Location = new Point(252, 208);
        allow.Location = new Point(386, 208);

        form.Controls.AddRange(new Control[] { title, who, detail, deny, allow });
        form.AcceptButton = allow;
        form.CancelButton = deny;

        return form.ShowDialog() == DialogResult.OK;
    }
}

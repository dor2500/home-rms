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
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            ClientSize = new Size(560, 300),
        };
        Theme.ApplyWindowChrome(form);
        form.Paint += (_, e) => Theme.PaintBackdrop(e.Graphics, form.ClientRectangle);

        var badge = new Label
        {
            Text = "ACCESS REQUEST",
            Font = Theme.CaptionFont,
            ForeColor = Theme.AccentLight,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(32, 26),
        };

        var title = new Label
        {
            Text = "Remote access request",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(30, 46),
        };

        var who = new Label
        {
            Text = $"\"{operatorName}\" wants to {action} on this computer.",
            Font = Theme.ValueFont,
            ForeColor = Theme.AccentLight,
            BackColor = Color.Transparent,
            Location = new Point(32, 92),
            Size = new Size(500, 30),
        };

        var detail = new Label
        {
            Text = "Allow only if you expected this connection. A visible red banner will remain on your screen " +
                   "for the entire session, you can stop it at any time from the tray, and access expires " +
                   "automatically after one hour.",
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont,
            Location = new Point(32, 132),
            Size = new Size(500, 80),
        };

        var deny = Theme.Flat("Deny", Theme.Card, Theme.Text, (_, _) => form.DialogResult = DialogResult.Cancel, 125);
        var allow = Theme.Flat("Allow", Theme.Accent, Color.White, (_, _) => form.DialogResult = DialogResult.OK, 125);
        deny.Location = new Point(288, 232);
        allow.Location = new Point(420, 232);

        form.Controls.AddRange(new Control[] { badge, title, who, detail, deny, allow });
        form.AcceptButton = allow;
        form.CancelButton = deny;

        return form.ShowDialog() == DialogResult.OK;
    }
}

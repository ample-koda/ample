using System.Drawing;
using System.Windows.Forms;
using Ample.VisualStudio.Remote;

namespace Ample.VisualStudio;

/// <summary>The window of "Connect to a server": address, how to connect, user, password and the folder for the local copy.</summary>
internal sealed class RemoteConnectDialog : Form
{
    private readonly TextBox url = new() { Text = "https://" };
    private readonly RadioButton direct = new() { Text = "Straight to the address (https, with sign-in)", Checked = true, AutoSize = true };
    private readonly RadioButton tunnel = new() { Text = "Through an SSH tunnel", AutoSize = true };
    private readonly TextBox ssh = new() { Enabled = false };
    private readonly TextBox user = new();
    private readonly TextBox password = new() { UseSystemPasswordChar = true };
    private readonly TextBox folder = new();

    public RemoteConnectDialog()
    {
        Text = "AMPLE: connect to a server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 330);
        folder.Text = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "AMPLE remote");
        int y = 12;
        Row("Address of the AMPLE (as the server sees it for a tunnel, for example http://127.0.0.1:55077)", url, ref y);
        direct.Location = new Point(12, y);
        Controls.Add(direct);
        y += 24;
        tunnel.Location = new Point(12, y);
        Controls.Add(tunnel);
        y += 28;
        Row("SSH target (user@host), for a tunnel", ssh, ref y);
        Row("User name of the AMPLE", user, ref y);
        Row("Password (kept in the Windows credential store)", password, ref y);
        Row("Folder for the local copy", folder, ref y);
        tunnel.CheckedChanged += (_, _) => ssh.Enabled = tunnel.Checked;
        Button ok = new() { Text = "Connect", DialogResult = DialogResult.OK, Location = new Point(344, 292), Size = new Size(80, 28) };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(430, 292), Size = new Size(80, 28) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public RemoteSettings Settings => new()
    {
        Url = url.Text.Trim(),
        Mode = tunnel.Checked ? RemoteMode.Tunnel : RemoteMode.Direct,
        SshHost = ssh.Text.Trim(),
        User = user.Text.Trim(),
    };

    public string Password => password.Text;

    public string Folder => folder.Text.Trim();

    private void Row(string label, TextBox box, ref int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(12, y), AutoSize = true });
        y += 18;
        box.Location = new Point(12, y);
        box.Width = 496;
        Controls.Add(box);
        y += 32;
    }
}

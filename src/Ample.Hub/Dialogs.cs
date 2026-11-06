using Ample.Hub.Core;

namespace Ample.Hub;

/// <summary>Small helpers for the look of the hub windows (the AMPLE colours and a flat, quiet style).</summary>
internal static class Look
{
    internal static readonly Color Back = Color.FromArgb(24, 22, 38);
    internal static readonly Color Panel = Color.FromArgb(36, 33, 56);
    internal static readonly Color Text = Color.FromArgb(235, 232, 248);
    internal static readonly Color Accent = Color.FromArgb(138, 91, 255);

    internal static void Style(Form form)
    {
        form.BackColor = Back;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 9.5f);
        form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        form.StartPosition = FormStartPosition.CenterScreen;
    }

    internal static Button Button(string text, DialogResult result = DialogResult.None)
    {
        Button button = new() { Text = text, DialogResult = result, FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Text, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        button.FlatAppearance.BorderColor = Accent;
        return button;
    }
}

/// <summary>Asks for the port of the main instance.</summary>
internal sealed class PortDialog : Form
{
    private readonly NumericUpDown number = new() { Minimum = 0, Maximum = 65535, Width = 100, BackColor = Look.Panel, ForeColor = Look.Text };

    internal PortDialog(int port)
    {
        Look.Style(this);
        Text = "AMPLE: port of the main instance";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(380, 150);
        number.Value = port;
        Label help = new() { Text = "The studio listens on this port, on this PC only.\n0 means a free port each time.", AutoSize = true, Location = new Point(16, 14) };
        number.Location = new Point(16, 64);
        Button ok = Look.Button("OK", DialogResult.OK);
        ok.Location = new Point(200, 104);
        Button cancel = Look.Button("Cancel", DialogResult.Cancel);
        cancel.Location = new Point(280, 104);
        Controls.AddRange([help, number, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    internal int Port => (int)number.Value;
}

/// <summary>The settings of the hub.</summary>
internal sealed class SettingsForm : Form
{
    private readonly CheckBox others = new() { Text = "Show and manage other AMPLE instances that run on this PC", AutoSize = true, ForeColor = Look.Text };
    private readonly CheckBox login = new() { Text = "The main instance asks for the user name and password (set with: ample auth set-password)", AutoSize = true, ForeColor = Look.Text };
    private readonly CheckBox start = new() { Text = "Start the hub when I sign in to Windows", AutoSize = true, ForeColor = Look.Text };
    private readonly HubSettings original;

    internal SettingsForm(HubSettings settings)
    {
        original = settings;
        Look.Style(this);
        Text = "AMPLE hub: settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, 190);
        others.Checked = settings.AcceptOthers;
        login.Checked = settings.MainLogin;
        start.Checked = settings.StartAtLogin;
        others.Location = new Point(16, 18);
        login.Location = new Point(16, 52);
        start.Location = new Point(16, 86);
        Button ok = Look.Button("OK", DialogResult.OK);
        ok.Location = new Point(380, 140);
        Button cancel = Look.Button("Cancel", DialogResult.Cancel);
        cancel.Location = new Point(460, 140);
        Controls.AddRange([others, login, start, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    internal HubSettings Result => original with { AcceptOthers = others.Checked, MainLogin = login.Checked, StartAtLogin = start.Checked };
}

/// <summary>The overview of the instances as plain text, with a button to copy it (for a bug report).</summary>
internal sealed class ReportForm : Form
{
    private readonly MainInstance main;
    private readonly TextBox box = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Look.Panel, ForeColor = Look.Text, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };

    internal ReportForm(MainInstance main)
    {
        this.main = main;
        Look.Style(this);
        Text = "AMPLE: overview of the instances";
        ClientSize = new Size(760, 360);
        FlowLayoutPanel bar = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        Button close = Look.Button("Close");
        close.Click += (_, _) => Close();
        Button copy = Look.Button("Copy as text");
        copy.Click += (_, _) => Clipboard.SetText(box.Text);
        Button refresh = Look.Button("Refresh");
        refresh.Click += async (_, _) => await LoadAsync();
        bar.Controls.AddRange([close, copy, refresh]);
        Controls.Add(box);
        Controls.Add(bar);
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        HubState state = await main.StateAsync(CancellationToken.None);
        box.Text = state.ToReport(DateTimeOffset.UtcNow).Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}

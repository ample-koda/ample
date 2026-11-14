using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Ample.Setup
{
    /// <summary>
    /// The wizard window. Modes: <c>fresh</c> (after a first install), <c>update</c> (after an upgrade) and <c>reconfigure</c> (from the Start menu or the tray hub).
    /// Pages: welcome, sign-in (off, on, or a new user name and password), a health check with fixes, what to do now, and the result with live progress.
    /// </summary>
    internal sealed partial class Wizard : Form
    {
        private static readonly Color Back = Color.FromArgb(24, 22, 38);
        private static readonly Color Card = Color.FromArgb(36, 33, 56);
        private static readonly Color Ink = Color.FromArgb(235, 232, 248);
        private static readonly Color Accent = Color.FromArgb(138, 91, 255);
        private static readonly Color Good = Color.FromArgb(94, 212, 140);
        private static readonly Color Warn = Color.FromArgb(255, 209, 102);
        private static readonly Color Bad = Color.FromArgb(255, 107, 129);

        private readonly string mode;
        private readonly bool runtimeOk;
        private readonly string installFolder;
        private readonly string ample;
        private readonly string hubPath;
        private readonly bool hubPresent;
        private readonly string home;
        private readonly bool hasCredentials;
        private readonly bool loginOn;
        private readonly List<Control> pages = new List<Control>();
        private Control signInPage;
        private Control healthPage;
        private Control actionsPage;
        private Control resultPage;
        private readonly Button back = new ThemedButton();
        private readonly Button next = new ThemedButton();
        private readonly Button cancel = new ThemedButton();
        private readonly RadioButton signInOff = new RadioButton();
        private readonly RadioButton signInStored = new RadioButton();
        private readonly RadioButton signInNew = new RadioButton();
        private readonly TextBox user = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly TextBox again = new TextBox();
        private readonly Label healthStatus = new Label();
        private readonly ProgressBar healthBar = new ProgressBar();
        private readonly FlowLayoutPanel healthRows = new FlowLayoutPanel();
        private readonly List<CheckBox> fixBoxes = new List<CheckBox>();
        private readonly CheckBox startAmple = new CheckBox();
        private readonly CheckBox startHub = new CheckBox();
        private readonly Label stepLabel = new Label();
        private readonly ProgressBar stepBar = new ProgressBar();
        private readonly TextBox results = new TextBox();
        private readonly Bitmap shield;
        private int page;
        private bool finished;
        private bool healthStarted;
        private bool healthBusy;

        internal Wizard(string mode, bool runtimeOk)
        {
            this.mode = mode;
            this.runtimeOk = runtimeOk;
            installFolder = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);   // Ample.Setup.exe sits next to ample.exe
            ample = Path.Combine(installFolder, "ample.exe");
            hubPath = Path.Combine(installFolder, "hub", "Ample.Hub.exe");
            hubPresent = File.Exists(hubPath);
            home = Environment.GetEnvironmentVariable("AMPLE_HOME");
            if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");
            hasCredentials = File.Exists(Path.Combine(home, "auth.json"));
            loginOn = hasCredentials && ReadLogin();
            shield = new Bitmap(SystemIcons.Shield.ToBitmap(), new Size(16, 16));

            Text = mode == "reconfigure" ? "AMPLE Tune-up" : "AMPLE setup";
            BackColor = Back;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 380);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { /* no icon is fine */ }

            Panel banner = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Card };
            using (Stream logo = typeof(Wizard).Assembly.GetManifestResourceStream("Ample.Setup.banner.png"))
            {
                if (logo != null) banner.Controls.Add(new PictureBox { Image = Image.FromStream(logo), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(150, 47), Location = new Point(14, 5), BackColor = Color.Transparent });
            }

            Controls.Add(banner);

            if (mode != "reconfigure") pages.Add(WelcomePage());
            signInPage = SignInPage(); pages.Add(signInPage);
            healthPage = HealthPage(); pages.Add(healthPage);
            packsPage = PacksPage(); pages.Add(packsPage);
            actionsPage = ActionsPage(); pages.Add(actionsPage);
            resultPage = ResultPage(); pages.Add(resultPage);
            foreach (Control p in pages) { p.Location = new Point(16, 68); p.Size = new Size(528, 258); p.Visible = false; Controls.Add(p); }

            Style(back, "Back"); Style(next, "Next"); Style(cancel, "Cancel");
            Place();
            back.Click += (s, e) => Go(page - 1);
            next.Click += async (s, e) => await OnNext();
            cancel.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { back, next, cancel });
            Show(0);
        }

        private void Style(Button button, string text)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Card;
            button.ForeColor = Ink;
            button.FlatAppearance.BorderColor = Accent;
            button.Height = 28;
            button.Top = 336;
            button.Width = 76;
        }

        // the Next button grows a little when it carries the UAC shield; the three buttons stay right-aligned
        private void Place()
        {
            cancel.Left = 464; next.Left = 464 - 8 - next.Width; back.Left = next.Left - 8 - back.Width;
        }

        private Control WelcomePage()
        {
            Panel p = new Panel();
            string version = "";
            try { version = FileVersionInfo.GetVersionInfo(ample).ProductVersion; } catch (Exception) { /* unknown version */ }
            p.Controls.Add(new Label
            {
                AutoSize = false, Size = new Size(528, 200), ForeColor = Ink,
                Text = (mode == "update" ? "AMPLE was updated" : "AMPLE is installed") + (version.Length > 0 ? " (version " + version + ")." : ".") + "\r\n\r\n" +
                       (mode == "update" ? "Apply the update to the running AMPLE and open the studio." : "Set up a sign-in, check your setup, start AMPLE and open the studio."),
            });
            return p;
        }

        private Control SignInPage()
        {
            Panel p = new Panel();
            p.Controls.Add(new Label { Text = "Sign-in for the AMPLE on this PC", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, ForeColor = Ink, Location = new Point(0, 0) });
            int y = 34;
            signInOff.Text = "No sign-in: AMPLE opens straight away (only people on this PC can reach it)";
            signInStored.Text = "Sign-in on, with the user name and password that are stored";
            signInNew.Text = hasCredentials ? "Sign-in on, with a new user name and password" : "Sign-in on, with a user name and password";
            foreach (RadioButton radio in hasCredentials ? new[] { signInOff, signInStored, signInNew } : new[] { signInOff, signInNew })
            {
                radio.AutoSize = true; radio.ForeColor = Ink; radio.Location = new Point(0, y); y += 28;
                radio.CheckedChanged += (s, e) => { user.Enabled = password.Enabled = again.Enabled = signInNew.Checked; };
                p.Controls.Add(radio);
            }

            (hasCredentials ? (loginOn ? signInStored : signInOff) : signInOff).Checked = true;
            y += 6;
            foreach (Tuple<string, TextBox> row in new[] { Tuple.Create("User name", user), Tuple.Create("Password (at least 8 characters)", password), Tuple.Create("Password again", again) })
            {
                p.Controls.Add(new Label { Text = row.Item1, AutoSize = true, Location = new Point(24, y + 3), ForeColor = Ink });
                row.Item2.Location = new Point(250, y); row.Item2.Width = 250; row.Item2.Enabled = signInNew.Checked;
                row.Item2.BackColor = Card; row.Item2.ForeColor = Ink;
                p.Controls.Add(row.Item2);
                y += 34;
            }

            password.UseSystemPasswordChar = true; again.UseSystemPasswordChar = true;
            return p;
        }

        private Control HealthPage()
        {
            Panel p = new Panel();
            p.Controls.Add(new Label { Text = "Check your setup", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, ForeColor = Ink, Location = new Point(0, 0) });
            healthStatus.AutoSize = false; healthStatus.Size = new Size(528, 20); healthStatus.Location = new Point(0, 28); healthStatus.ForeColor = Ink; healthStatus.Text = "Looking at what AMPLE needs on this PC...";
            healthBar.Style = ProgressBarStyle.Marquee; healthBar.MarqueeAnimationSpeed = 30; healthBar.Size = new Size(528, 8); healthBar.Location = new Point(0, 52);
            healthRows.Location = new Point(0, 66); healthRows.Size = new Size(528, 192); healthRows.AutoScroll = true; healthRows.FlowDirection = FlowDirection.TopDown; healthRows.WrapContents = false;
            p.Controls.AddRange(new Control[] { healthStatus, healthBar, healthRows });
            return p;
        }

        private Control ActionsPage()
        {
            Panel p = new Panel();
            startAmple.Text = mode == "fresh" ? "Start AMPLE and open the studio" : mode == "update" ? "Apply the update to the running AMPLE and open the studio" : "Restart AMPLE so the changes apply, and open the studio";
            startAmple.Checked = true; startAmple.AutoSize = true; startAmple.ForeColor = Ink; startAmple.Location = new Point(0, 8);
            startHub.Text = "Start the AMPLE tray hub (a tray icon to start, stop and restart AMPLE)";
            startHub.Checked = runtimeOk && hubPresent; startHub.Enabled = runtimeOk && hubPresent; startHub.AutoSize = true; startHub.ForeColor = Ink; startHub.Location = new Point(0, 44);
            p.Controls.Add(startAmple); p.Controls.Add(startHub);
            if (!runtimeOk && hubPresent)
                p.Controls.Add(new Label { AutoSize = false, Size = new Size(520, 60), Location = new Point(0, 76), ForeColor = Warn, Text = "The tray hub needs the .NET 9 Desktop Runtime, which is not installed. The check on the previous page can install it for all users (that asks for administrator permission); then start the hub from the Start menu." });
            return p;
        }

        private Control ResultPage()
        {
            Panel p = new Panel();
            stepLabel.AutoSize = false; stepLabel.Size = new Size(528, 22); stepLabel.Location = new Point(0, 0); stepLabel.ForeColor = Ink; stepLabel.Font = new Font("Segoe UI Semibold", 10f);
            stepBar.Size = new Size(528, 14); stepBar.Location = new Point(0, 26); stepBar.Style = ProgressBarStyle.Continuous;
            results.Multiline = true; results.ReadOnly = true; results.BorderStyle = BorderStyle.None; results.BackColor = Card; results.ForeColor = Ink;
            results.Location = new Point(0, 50); results.Size = new Size(528, 206); results.ScrollBars = ScrollBars.Vertical;
            p.Controls.AddRange(new Control[] { stepLabel, stepBar, results });
            return p;
        }

        private string Choice { get { return signInNew.Checked ? "new" : signInStored.Checked ? "stored" : "off"; } }

        private bool LoginChanged { get { return (Choice != "off") != loginOn || Choice == "new"; } }

        private List<Fix> SelectedFixes { get { return fixBoxes.Where(b => b.Checked).Select(b => (Fix)b.Tag).ToList(); } }

        private void Show(int index)
        {
            page = index;
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == index;
            Control current = pages[index];
            FitForPage(current);
            if (current == actionsPage && mode == "reconfigure" && true) { startAmple.Checked = LoginChanged; }
            if (current == healthPage && !healthStarted) { healthStarted = true; _ = RunDoctorAsync(); }
            if (current == packsPage && !packsLoaded && !packsLoading) { _ = LoadPacksAsync(); }
            back.Visible = index > 0 && !finished && current != resultPage;
            next.Text = finished ? "Close" : current == actionsPage ? "Finish" : "Next";
            next.Enabled = (current != resultPage || finished) && !(current == healthPage && healthBusy) && !(current == packsPage && packsLoading);
            cancel.Visible = !finished;
            UpdateShield();
        }

        // the Finish button carries the Windows UAC shield when a selected fix will ask for administrator permission
        private void UpdateShield()
        {
            bool admin = !finished && pages[page] == actionsPage && SelectedFixes.Any(f => f.Admin);
            next.Image = admin ? shield : null;
            next.ImageAlign = ContentAlignment.MiddleLeft;
            next.TextImageRelation = TextImageRelation.ImageBeforeText;
            next.Width = admin ? 104 : 76;
            Place();
        }

        private void Go(int index) { if (index >= 0 && index < pages.Count) Show(index); }

        private async Task OnNext()
        {
            if (finished) { Close(); return; }
            if (pages[page] == signInPage && !ValidSignIn()) return;
            if (pages[page] == packsPage && !ConfirmPackRemoval()) return;
            if (pages[page] != actionsPage) { Go(page + 1); return; }
            // the actions page: run the steps
            Go(pages.Count - 1);
            next.Enabled = false; back.Visible = false; cancel.Enabled = false;
            string choice = Choice, name = user.Text.Trim(), secret = password.Text;
            bool doAmple = startAmple.Checked, doHub = startHub.Checked && runtimeOk && hubPresent;
            List<Fix> fixes = SelectedFixes;
            fixes.AddRange(PackFixes());
            await Task.Run(() => RunSteps(choice, name, secret, fixes, doAmple, doHub));
            finished = true;
            cancel.Enabled = true;
            Show(pages.Count - 1);
        }

        private bool ValidSignIn()
        {
            if (!signInNew.Checked) return true;
            string problem = user.Text.Trim().Length == 0 ? "Type a user name." : password.Text.Length < 8 ? "The password needs at least 8 characters." : password.Text != again.Text ? "The two passwords differ." : null;
            if (problem != null) MessageBox.Show(this, problem, "AMPLE setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return problem == null;
        }

        // ---- the health check ----

        private async Task RunDoctorAsync()
        {
            healthBusy = true; next.Enabled = false;
            List<Finding> findings = await Task.Run(() => ReadFindings());
            healthRows.SuspendLayout();
            healthRows.Controls.Clear();
            fixBoxes.Clear();
            foreach (Finding f in findings) AddRow(f);
            healthRows.ResumeLayout();
            healthBar.Visible = false;
            // the sound packs are optional downloads (and removals): they are not "things to look at"
            Func<Finding, bool> isPack = x => x.Name.StartsWith("sound pack:", StringComparison.Ordinal);
            int open = findings.Count(f => f.Level != "ok" && !isPack(f)), fixable = findings.Count(f => f.Level != "ok" && f.Fix != null && !isPack(f));
            int packs = findings.Count(isPack);
            string packNote = packs > 0 ? " Sound packs: optional, below." : "";
            healthStatus.Text = (open == 0 ? "Everything AMPLE needs is in place." : open + " thing(s) to look at" + (fixable > 0 ? "; tick what AMPLE may fix for you." : ".")) + packNote;
            healthBusy = false;
            Show(page);
        }

        private List<Finding> ReadFindings()
        {
            List<Finding> list = new List<Finding>();
            try
            {
                string project = Directory.Exists(Path.Combine(home, "projects", "Welcome")) ? Path.Combine(home, "projects", "Welcome") : home;
                Directory.CreateDirectory(project);
                ProcessStartInfo start = new ProcessStartInfo(ample, "doctor \"" + project + "\" --env --assistant --json")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                };
                start.EnvironmentVariables["AMPLE_HOME"] = home;
                string json;
                using (Process process = Process.Start(start))
                {
                    json = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(60000);
                }

                foreach (object o in (object[])new JavaScriptSerializer().DeserializeObject(json))
                {
                    var d = (Dictionary<string, object>)o;
                    Finding f = new Finding { Level = Field(d, "level"), Name = Field(d, "name"), Detail = Field(d, "detail"), Hint = Field(d, "hint") };
                    if (d.ContainsKey("fix") && d["fix"] is Dictionary<string, object> fx)
                        f.Fix = new Fix { Kind = Field(fx, "kind"), Id = Field(fx, "id"), Title = Field(fx, "title"), Scope = Field(fx, "scope").Length > 0 ? Field(fx, "scope") : null, SizeMb = fx.ContainsKey("sizeMb") ? Convert.ToInt32(fx["sizeMb"]) : 0, Admin = fx.ContainsKey("admin") && Convert.ToBoolean(fx["admin"]) };
                    list.Add(f);
                }
            }
            catch (Exception ex)
            {
                list.Add(new Finding { Level = "missing", Name = "the check itself", Detail = "The check did not run: " + ex.Message, Hint = "" });
            }

            if (hubPresent && !runtimeOk)
                list.Add(new Finding
                {
                    Level = "missing", Name = ".NET 9 Desktop Runtime", Detail = "the tray hub needs it and it is not installed", Hint = "",
                    Fix = new Fix { Kind = "winget", Id = "Microsoft.DotNet.DesktopRuntime.9", Scope = "machine", Admin = true, SizeMb = 60, Title = "Install the .NET 9 Desktop Runtime for all users with winget" },
                });
            return list;
        }

        private static string Field(Dictionary<string, object> d, string key) { return d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : string.Empty; }

        private void AddRow(Finding f)
        {
            Color colour = f.Level == "ok" ? Good : f.Level == "note" ? Warn : Bad;
            string glyph = f.Level == "ok" ? "✓" : f.Level == "note" ? "i" : "✕";
            string detail = f.Detail.Length > 70 ? f.Detail.Substring(0, 70) + "..." : f.Detail;
            bool choice = f.Fix != null && (f.Level != "ok" || f.Fix.Kind == "pack-remove");   // a sound pack that is installed offers its removal (unticked)
            Panel row = new Panel { Width = 500, Height = choice ? 66 : 24, Margin = new Padding(0, 0, 0, 2) };
            row.Controls.Add(new Label { Text = glyph, ForeColor = colour, Font = new Font("Segoe UI Semibold", 10f), AutoSize = false, Size = new Size(20, 22), Location = new Point(0, 0) });
            row.Controls.Add(new Label { Text = f.Name + (detail.Length > 0 && f.Level != "ok" ? ": " + detail : ""), ForeColor = Ink, AutoSize = false, AutoEllipsis = true, Size = new Size(478, 22), Location = new Point(22, 1) });
            if (choice)
            {
                string size = f.Fix.SizeMb > 0 ? " (about " + f.Fix.SizeMb + " MB)" : "";
                CheckBox box = new CheckBox { Text = f.Fix.Title + size + (f.Fix.Admin ? "  [needs administrator permission]" : ""), Tag = f.Fix, AutoSize = false, Size = new Size(476, 40), Location = new Point(22, 24), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft, ForeColor = f.Fix.Admin ? Warn : Ink, Checked = f.Fix.Kind == "pack" && f.Fix.Id == "soundfont" };
                box.CheckedChanged += (s, e) => UpdateShield();
                fixBoxes.Add(box);
                row.Controls.Add(box);
            }

            healthRows.Controls.Add(row);
        }

        // ---- the steps, with live progress ----

        private void Say(string text) { Invoke((Action)(() => stepLabel.Text = text)); }

        private void Progress(int percent)
        {
            Invoke((Action)(() =>
            {
                if (percent < 0) { if (stepBar.Style != ProgressBarStyle.Marquee) { stepBar.Style = ProgressBarStyle.Marquee; stepBar.MarqueeAnimationSpeed = 30; } }
                else { if (stepBar.Style != ProgressBarStyle.Continuous) stepBar.Style = ProgressBarStyle.Continuous; stepBar.Value = Math.Max(0, Math.Min(100, percent)); }
            }));
        }

        private void Log(string line) { Invoke((Action)(() => results.AppendText(line + "\r\n"))); }

        private void RunSteps(string choice, string signInUser, string secret, List<Fix> fixes, bool doAmple, bool doHub)
        {
            bool wantLogin = loginOn;
            bool changeLogin = choice != "stored" || !loginOn;
            int total = (choice == "new" ? 1 : 0) + fixes.Count + (doAmple || doHub ? 1 : 0), done = 0;
            Func<string> counter = () => "Step " + (done + 1) + " of " + Math.Max(total, 1) + ": ";

            if (choice == "new")
            {
                Say(counter() + "storing the user name and password"); Progress(-1);
                string answer = RunAmple(ample, "auth set-password --user \"" + signInUser.Replace("\"", "") + "\"", secret + "\n" + secret + "\n", out int code);
                if (code == 0) { wantLogin = true; Log("OK  The user name and password for " + signInUser + " are stored."); }
                else Log("FAILED  The sign-in was not changed: " + answer.Trim());
                done++;
            }
            else wantLogin = choice == "stored";

            if (wantLogin != loginOn || choice == "new")
            {
                try { WriteLogin(wantLogin); Log("OK  Sign-in is " + (wantLogin ? "on." : "off.")); }
                catch (Exception ex) { Log("FAILED  The sign-in setting was not saved: " + ex.Message); }
            }

            // the fixes that need no administrator permission first, then the ones that do (one UAC prompt each)
            foreach (Fix fix in fixes.OrderBy(f => f.Admin))
            {
                string heading = counter() + (fix.Admin ? "(administrator permission) " : "") + fix.Title;
                Say(heading); Progress(fix.Kind == "pack" ? 0 : -1);
                Action<int, string> report = (percent, text) => { Progress(percent); Say(heading + "\r\n" + text); };
                string problem = fix.Admin ? FixRunner.RunElevated(fix, ample, home, report) : FixRunner.Run(fix, ample, home, report);
                Log(problem == null ? "OK  " + fix.Title : "FAILED  " + fix.Title + ": " + problem);
                done++;
            }

            if (doHub || doAmple)
            {
                Say(counter() + (doAmple ? (mode == "fresh" ? "starting AMPLE" : "restarting AMPLE") : "starting the tray hub")); Progress(-1);
                if (doHub)
                {
                    try
                    {
                        string arguments = doAmple ? (mode == "fresh" ? "--start-main --open-studio" : "--restart-main --open-studio") : string.Empty;
                        Process.Start(new ProcessStartInfo(hubPath, arguments) { UseShellExecute = false });
                        Log("OK  The tray hub started" + (doAmple ? ", and it " + (mode == "fresh" ? "starts" : "restarts") + " AMPLE and opens the studio." : "."));
                    }
                    catch (Exception ex) { Log("FAILED  The tray hub did not start: " + ex.Message); }
                }
                else
                {
                    try
                    {
                        string folder = Path.Combine(home, "projects", "Welcome");
                        Directory.CreateDirectory(folder);
                        string serveArguments = "serve --create --project \"" + folder + "\" --home \"" + home + "\"";
                        if (wantLogin) serveArguments += " --login";
                        ProcessStartInfo startInfo = new ProcessStartInfo(ample, serveArguments)
                        {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                        };
                        Process server = Process.Start(startInfo);
                        // the error text is read as it comes (an unread pipe would block a server that logs a lot); the server is not
                        // disposed here, it keeps running
                        StringBuilder serverErrors = new StringBuilder();
                        server.ErrorDataReceived += (sender, e) =>
                        {
                            if (e.Data != null) lock (serverErrors) serverErrors.AppendLine(e.Data);
                        };
                        server.BeginErrorReadLine();
                        // a server that fails to start (no project, port taken) exits within moments:
                        // a bounded wait reports that instead of
                        // a false success;
                        // a slow machine does not give a false failure (only an exit counts), at worst a late failure goes unseen
                        if (server.WaitForExit(6000))
                        {
                            server.WaitForExit();
                            string errors; lock (serverErrors) errors = serverErrors.ToString().Trim();
                            string stopped = "the server stopped at once (exit code " + server.ExitCode + ")";
                            throw new InvalidOperationException(stopped + (errors.Length > 0 ? ": " + errors : "."));
                        }

                        Log("OK  AMPLE started and the studio opens in your browser." + (mode == "fresh" ? string.Empty : " (A running AMPLE has to be stopped by you first: without the tray hub this setup cannot restart it.)"));
                    }
                    catch (Exception ex) { Log("FAILED  AMPLE did not start: " + ex.Message); }
                }

                done++;
            }

            if (done == 0 && !changeLogin) Log("Nothing was changed.");
            if (fixes.Any(f => f.Kind == "winget")) Log("A package that was just installed may need a new terminal before it is found.");
            Log(string.Empty);
            Log("You can run this setup again from the Start menu (AMPLE Tune-up).");
            Say("Done"); Progress(100);
        }

        // hub.json: the settings of the tray hub; this wizard only touches the sign-in field and keeps the others
        private string HubJson { get { return Path.Combine(home, "hub.json"); } }

        private bool ReadLogin()
        {
            try { return File.Exists(HubJson) && Regex.IsMatch(File.ReadAllText(HubJson), "\"mainLogin\"\\s*:\\s*true", RegexOptions.IgnoreCase); }
            catch (Exception) { return false; }
        }

        private void WriteLogin(bool on)
        {
            string text = File.Exists(HubJson) ? File.ReadAllText(HubJson) : string.Empty;
            string port = Regex.Match(text, "\"mainPort\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
            string others = Regex.Match(text, "\"acceptOthers\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase).Groups[1].Value;
            string atLogin = Regex.Match(text, "\"startAtLogin\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase).Groups[1].Value;
            Directory.CreateDirectory(home);
            string json = "{\n  \"mainPort\": " + (port.Length > 0 ? port : "0") + ",\n  \"mainLogin\": " + (on ? "true" : "false") + ",\n  \"acceptOthers\": " + (others.Length > 0 ? others.ToLowerInvariant() : "true") + ",\n  \"startAtLogin\": " + (atLogin.Length > 0 ? atLogin.ToLowerInvariant() : "true") + "\n}\n";
            string temporary = HubJson + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(HubJson)) File.Delete(HubJson);
            File.Move(temporary, HubJson);
        }

        private static string RunAmple(string exe, string arguments, string input, out int code)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process process = Process.Start(start))
                {
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();
                    string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    code = process.ExitCode;
                    return text;
                }
            }
            catch (Exception ex) { code = -1; return ex.Message; }
        }
    }

    /// <summary>A flat button that stays readable when it is disabled (WinForms paints a disabled flat button dark grey on dark).</summary>
    internal sealed class ThemedButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(Color.FromArgb(30, 28, 46));
            using (Pen pen = new Pen(Color.FromArgb(70, 64, 104))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(150, 146, 176), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

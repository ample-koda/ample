namespace Ample.Player;

/// <summary>
/// The player window Visual Studio starts on F5: the waveform, play/pause, stop, loop, volume and the output device. When the song ends without
/// the person having touched anything, it counts down and closes (which ends the debug session); any touch of a control keeps it open.
/// What happens is also written to the debug output (Trace), so Visual Studio shows it in its Output window.
/// </summary>
internal sealed class PlayerForm : Form
{
    private const int CountdownSeconds = 5;

    private readonly WavData _wav;
    private readonly AudioEngine _engine;
    private readonly PlayerSettings _settings = new();
    private readonly string _songKey;
    private readonly System.Collections.Generic.List<string> _devices = AudioEngine.DeviceNames();
    private readonly WaveView _waveView;
    private readonly IconButton _play = new() { Glyph = "" }, _stop = new() { Glyph = "" }, _loop = new() { Glyph = "" },
        _channels = new() { PlainText = true, GlyphSize = 11f };
    private readonly Slider _volume = new();
    private readonly ComboBox _device = new();
    private readonly Label _title = new(), _subtitle = new(), _time = new(), _loopInfo = new(), _status = new(), _volumeIcon = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer _countdown = new() { Interval = 1000 };
    private bool _loopOn, _touched, _finishedShown;
    private long _regionStart, _regionEnd;
    private int _lastLoopCount, _remaining;
    private System.DateTime _lastScrubLog = System.DateTime.MinValue;

    public PlayerForm(string wavPath, string song)
    {
        _wav = WavData.Load(wavPath);
        _songKey = "song." + song.ToLowerInvariant().Replace('=', '_');
        _engine = new AudioEngine(_wav);
        float k = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Text = "AMPLE Echo - " + song;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(720 * k), (int)(384 * k));
        _waveView = new WaveView(_wav);
        int m = (int)(22 * k), width = ClientSize.Width - 2 * m;

        Style(_title, song, new Font("Segoe UI Semibold", 15f), Theme.Text, m, (int)(12 * k), width, (int)(32 * k));
        Style(_subtitle, Describe(), Font, Theme.Dim, m, (int)(46 * k), width, (int)(22 * k));
        _waveView.SetBounds(m, (int)(78 * k), width, (int)(160 * k));
        Style(_time, "", new Font("Segoe UI", 10f), Theme.Text, m, (int)(244 * k), (int)(260 * k), (int)(24 * k));
        Style(_loopInfo, "", Font, Theme.Accent, m + (int)(260 * k), (int)(244 * k), width - (int)(260 * k), (int)(24 * k));
        _loopInfo.TextAlign = ContentAlignment.MiddleRight;
        _play.SetBounds(m, (int)(278 * k), (int)(56 * k), (int)(46 * k));
        _stop.SetBounds(m + (int)(64 * k), (int)(278 * k), (int)(56 * k), (int)(46 * k));
        _loop.SetBounds(m + (int)(128 * k), (int)(278 * k), (int)(56 * k), (int)(46 * k));
        Style(_volumeIcon, "", new Font("Segoe MDL2 Assets", 13f), Theme.Dim, m + (int)(272 * k), (int)(288 * k), (int)(28 * k), (int)(26 * k));
        _volume.SetBounds(m + (int)(304 * k), (int)(290 * k), (int)(120 * k), (int)(22 * k));
        _channels.SetBounds(m + (int)(192 * k), (int)(278 * k), (int)(64 * k), (int)(46 * k));
        _device.SetBounds(m + width - (int)(250 * k), (int)(290 * k), (int)(250 * k), (int)(28 * k));
        Style(_status, "", Font, Theme.Dim, m, (int)(340 * k), width, (int)(26 * k));
        StyleDevice();

        Controls.AddRange([_title, _subtitle, _waveView, _time, _loopInfo, _play, _stop, _loop, _channels, _volumeIcon, _volume, _device, _status]);
        _play.Click += (_, _) => TogglePlay();
        _stop.Click += (_, _) => { Touch(); _engine.Stop(); Log("stopped, back to the start"); };
        _loop.Click += (_, _) => { Touch(); SetLoop(!_loopOn); };
        _channels.Click += (_, _) => { Touch(); SetSplit(!_waveView.Split); };
        _volume.Changed += (_, _) => { Touch(); _engine.Volume = (float)_volume.Value; };
        _volume.Released += (_, _) => { _settings.SetNumber("volume", _volume.Value); Log("volume " + (int)(_volume.Value * 100) + "%"); };
        _device.SelectionChangeCommitted += (_, _) => ChangeDevice();
        _waveView.Seek += frame => { Touch(); _engine.Seek(frame); Log("jumped to " + Format(frame)); };
        _waveView.RegionPicked += (a, b) => { Touch(); _regionStart = a; _regionEnd = b; SetLoop(true); Log("loop region " + Format(a) + " - " + Format(b)); };
        _waveView.ClearRegion += () => { Touch(); _regionStart = 0; _regionEnd = 0; SetLoop(_loopOn); Log("loop region removed"); };
        _waveView.Scrub += seconds => Scrub(seconds);
        _tick.Tick += (_, _) => Tick();
        _countdown.Tick += (_, _) => CountDown();

        _channels.Visible = _wav.Channels == 2;
        SetSplit(_settings.Get("channels") != "together", false);
        _volume.Value = _settings.GetNumber("volume", 0.8);
        _engine.Volume = (float)_volume.Value;
        int device = _devices.IndexOf(_settings.Get("device") ?? "");
        _device.SelectedIndex = device < 0 ? 0 : device;
        _regionStart = (long)(_settings.GetNumber(_songKey + ".start", 0) * _wav.Rate);
        _regionEnd = (long)(_settings.GetNumber(_songKey + ".end", 0) * _wav.Rate);
        if (_regionEnd <= _regionStart || _regionEnd > _wav.Frames) { _regionStart = 0; _regionEnd = 0; }
        _loopOn = _settings.Get(_songKey + ".loop") == "1";
        Shown += (_, _) => Begin(song);
        FormClosing += (_, _) => { _tick.Stop(); _countdown.Stop(); _engine.Dispose(); };
    }

    private void Begin(string song)
    {
        try
        {
            _engine.Open(_device.SelectedIndex, _loopOn && _regionEnd > _regionStart ? _regionStart : 0);
        }
        catch (System.InvalidOperationException ex)
        {
            Log("cannot start: " + ex.Message);
            _status.ForeColor = Color.FromArgb(240, 120, 120);
            _status.Text = ex.Message;
            return;
        }

        SetLoop(_loopOn, false);
        _engine.Play();
        _tick.Start();
        Log("playing " + song + " (" + Describe() + ") on " + _device.Text + (_loopOn ? ", loop is on" : ""));
    }

    private string Describe() =>
        (_wav.Rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz, " + (_wav.Channels == 1 ? "mono" : "stereo")
        + ", " + Format(_wav.Frames);

    private string Format(long frame) => Format(frame / (double)_wav.Rate);

    private static string Format(double seconds)
    {
        int minutes = (int)(seconds / 60);
        return minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
            + (seconds - minutes * 60).ToString("00.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Log(string message) => System.Diagnostics.Trace.WriteLine("Echo: " + message);

    private static void Style(Label label, string text, Font font, Color colour, int x, int y, int width, int height)
    {
        label.Text = text;
        label.Font = font;
        label.ForeColor = colour;
        label.BackColor = Theme.Back;
        label.SetBounds(x, y, width, height);
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }

    private void StyleDevice()
    {
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        _device.FlatStyle = FlatStyle.Flat;
        _device.BackColor = Theme.Raised;
        _device.ForeColor = Theme.Text;
        _device.DrawMode = DrawMode.OwnerDrawFixed;
        _device.ItemHeight = (int)(22 * DeviceDpi / 96f);
        _device.Items.AddRange([.. _devices]);
        _device.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool hot = (e.State & DrawItemState.Selected) != 0;
            using SolidBrush back = new(hot ? Theme.Hover : Theme.Raised), ink = new(Theme.Text);
            e.Graphics.FillRectangle(back, e.Bounds);
            e.Graphics.DrawString(_devices[e.Index], _device.Font, ink, e.Bounds.X + 4, e.Bounds.Y + 2);
        };
    }

    /// <summary>The person did something: from now on the player stays open when the song ends.</summary>
    private void Touch()
    {
        _touched = true;
        if (_countdown.Enabled)
        {
            _countdown.Stop();
            _status.Text = "Playback finished";
            Log("closing cancelled");
        }
    }

    private void TogglePlay()
    {
        Touch();
        if (_engine.Finished || _engine.Paused)
        {
            _engine.Play();
            Log(_engine.Finished ? "play again from the start" : "play");
        }
        else
        {
            _engine.Pause();
            Log("paused at " + Format(_engine.PositionFrames));
        }
    }

    private void SetSplit(bool split, bool log = true)
    {
        _waveView.Split = split;
        _channels.Glyph = split ? "L | R" : "L + R";
        _channels.On = split;
        _channels.Invalidate();
        _waveView.Invalidate();
        _settings.Set("channels", split ? "split" : "together");
        if (log) Log(split ? "waveform: left and right apart" : "waveform: both channels together");
    }

    private void SetLoop(bool on, bool log = true)
    {
        _loopOn = on;
        _engine.SetLoop(on, _regionStart, _regionEnd);
        _settings.Set(_songKey + ".loop", on ? "1" : "0");
        _settings.SetNumber(_songKey + ".start", _regionStart / (double)_wav.Rate);
        _settings.SetNumber(_songKey + ".end", _regionEnd / (double)_wav.Rate);
        _lastLoopCount = _engine.LoopCount;
        if (!log) return;
        Log(on ? (_regionEnd > _regionStart ? "loop on, region " + Format(_regionStart) + " - " + Format(_regionEnd) : "loop on, whole song") : "loop off");
    }

    private void Scrub(double seconds)
    {
        Touch();
        _engine.Seek(_engine.PositionFrames + (long)(seconds * _wav.Rate));
        if ((System.DateTime.Now - _lastScrubLog).TotalMilliseconds > 400) Log("scrubbed to " + Format(_engine.PositionFrames));
        _lastScrubLog = System.DateTime.Now;
    }

    private void ChangeDevice()
    {
        Touch();
        try
        {
            _engine.ChangeDevice(_device.SelectedIndex);
            _settings.Set("device", _devices[_device.SelectedIndex]);
            Log("output device: " + _devices[_device.SelectedIndex]);
        }
        catch (System.InvalidOperationException ex)
        {
            _status.Text = ex.Message;
            Log("device change failed: " + ex.Message);
        }
    }

    private void Tick()
    {
        long position = _engine.PositionFrames;
        _waveView.Position = position;
        _waveView.RegionStart = _regionStart;
        _waveView.RegionEnd = _regionEnd;
        _waveView.LoopOn = _loopOn;
        _waveView.Invalidate();
        _time.Text = Format(position) + " / " + Format(_wav.Frames);
        _play.Glyph = _engine.Paused || _engine.Finished ? "" : "";
        _loop.On = _loopOn;
        _loop.Invalidate();
        int loops = _engine.LoopCount;
        _loopInfo.Text = _loopOn ? (_regionEnd > _regionStart ? "Looping " + Format(_regionStart) + " - " + Format(_regionEnd) : "Looping the song") + "   x" + loops : "";
        if (loops != _lastLoopCount)
        {
            _lastLoopCount = loops;
            Log("loop " + (loops + 1) + " started");
        }

        if (_engine.Finished && !_finishedShown)
        {
            _finishedShown = true;
            Log("playback finished");
            if (_touched)
            {
                _status.Text = "Playback finished";
            }
            else
            {
                _remaining = CountdownSeconds;
                _status.Text = "Playback finished - closing the player in " + _remaining + "...";
                _countdown.Start();
            }
        }
        else if (!_engine.Finished && _finishedShown)
        {
            _finishedShown = false;
            _status.Text = "";
        }
        else if (!_finishedShown && _status.Text.Length == 0)
        {
            _status.Text = "Click the waveform to jump, drag to loop a part, double-click to clear it.  Space = play/pause, L = loop.";
        }
    }

    private void CountDown()
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _countdown.Stop();
            Log("closing the player");
            Close();
            return;
        }

        _status.Text += " " + _remaining + "...";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_device.Focused) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.L: Touch(); SetLoop(!_loopOn); return true;
            case Keys.Left: Scrub(-5); return true;
            case Keys.Right: Scrub(5); return true;
            case Keys.Home: Touch(); _engine.Seek(_loopOn ? _regionStart : 0); Log("jumped to the start"); return true;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

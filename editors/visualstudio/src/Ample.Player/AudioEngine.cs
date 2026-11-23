using System.Runtime.InteropServices;

namespace Ample.Player;

/// <summary>
/// Plays a <see cref="WavData"/> through the Windows waveOut API on a chosen output device: pause, seek, volume, and a loop over the whole song or a
/// region. A feeder thread keeps a few short buffers queued, so a seek or a volume change is heard within about 0.25 s.
/// </summary>
internal sealed class AudioEngine : System.IDisposable
{
    private const int BufferCount = 6, ChunkMilliseconds = 40;
    private const uint WaveMapper = 0xFFFFFFFF, WhdrDone = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public System.IntPtr Data; public uint Length; public uint Recorded; public System.IntPtr User; public uint Flags; public uint Loops; public System.IntPtr Next; public System.IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, ExtraSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps { public ushort Manufacturer, Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmTime { public uint Type; public uint Value; public uint Pad1, Pad2; }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out System.IntPtr handle, uint device, ref WaveFormat format, System.IntPtr callback, System.IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutClose(System.IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(System.IntPtr handle, System.IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(System.IntPtr handle, System.IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(System.IntPtr handle, System.IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutPause(System.IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(System.IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutReset(System.IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(System.IntPtr handle, ref MmTime time, int size);
    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCaps(System.IntPtr device, ref WaveOutCaps caps, int size);

    /// <summary>Winmm calls whose failure changes nothing here: the result is only traced.</summary>
    private static void Ok(int result)
    {
        if (result != 0) System.Diagnostics.Trace.WriteLine("Echo: waveOut call failed, error " + result);
    }

    private struct Segment { public long PlayedStart; public long SongStart; public int Frames; }

    private readonly WavData _wav;
    private readonly object _gate = new();
    private readonly System.IntPtr[] _headers = new System.IntPtr[BufferCount];
    private readonly System.IntPtr[] _buffers = new System.IntPtr[BufferCount];
    private readonly bool[] _queued = new bool[BufferCount];
    private readonly System.Collections.Generic.Queue<Segment> _segments = new();
    private readonly int _chunkFrames;
    private System.IntPtr _device;
    private System.Threading.Thread? _feeder;
    private volatile bool _stopFeeder;
    private long _readFrame, _writtenFrames;
    private bool _feedingDone, _paused = true, _loopEnabled, _finished;
    private long _loopStart, _loopEnd;
    private float _gain = 1f;
    private int _loopCount;

    public AudioEngine(WavData wav)
    {
        _wav = wav;
        _chunkFrames = wav.Rate * ChunkMilliseconds / 1000;
        _loopEnd = wav.Frames;
    }

    /// <summary>The output devices: index 0 is the Windows default, then each device (Windows cuts the names at 31 characters).</summary>
    public static System.Collections.Generic.List<string> DeviceNames()
    {
        System.Collections.Generic.List<string> names = ["Default device"];
        int count = waveOutGetNumDevs();
        for (int i = 0; i < count; i++)
        {
            WaveOutCaps caps = new();
            names.Add(waveOutGetDevCaps((System.IntPtr)i, ref caps, Marshal.SizeOf<WaveOutCaps>()) == 0 ? caps.Name : "Device " + i);
        }

        return names;
    }

    /// <summary>True once the song was played to its end (not looping).</summary>
    public bool Finished { get { lock (_gate) return _finished; } }

    /// <summary>How many times the loop has started over.</summary>
    public int LoopCount { get { lock (_gate) return _loopCount; } }

    /// <summary>Whether playback is paused (also before the first Play and after Stop).</summary>
    public bool Paused { get { lock (_gate) return _paused; } }

    /// <summary>Opens the device with this index in <see cref="DeviceNames"/> (0 = default) and starts the feeder, paused at <paramref name="startFrame"/>.</summary>
    public void Open(int deviceIndex, long startFrame)
    {
        lock (_gate)
        {
            WaveFormat format = new()
            {
                Tag = 1, Channels = (ushort)_wav.Channels, Rate = (uint)_wav.Rate, Bits = 16,
                BlockAlign = (ushort)(_wav.Channels * 2), BytesPerSecond = (uint)(_wav.Rate * _wav.Channels * 2),
            };
            int result = waveOutOpen(out _device, deviceIndex <= 0 ? WaveMapper : (uint)(deviceIndex - 1), ref format, System.IntPtr.Zero, System.IntPtr.Zero, 0);
            if (result != 0) throw new System.InvalidOperationException("The output device could not be opened (error " + result + ").");
            int bufferBytes = _chunkFrames * _wav.Channels * 2;
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(bufferBytes);
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data = _buffers[i], Length = (uint)bufferBytes }, _headers[i], false);
                Ok(waveOutPrepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHeader>()));
                _queued[i] = false;
            }

            ResetLocked(startFrame);
            _stopFeeder = false;
            _feeder = new System.Threading.Thread(Feed) { IsBackground = true, Name = "Echo feeding the buffers" };
            _feeder.Start();
        }
    }

    private void CloseDevice()
    {
        _stopFeeder = true;
        _feeder?.Join();
        lock (_gate)
        {
            if (_device == System.IntPtr.Zero) return;
            Ok(waveOutReset(_device));
            for (int i = 0; i < BufferCount; i++)
            {
                Ok(waveOutUnprepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHeader>()));
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_buffers[i]);
            }

            Ok(waveOutClose(_device));
            _device = System.IntPtr.Zero;
        }
    }

    /// <summary>Moves to another output device and goes on from the same place.</summary>
    public void ChangeDevice(int deviceIndex)
    {
        long at = PositionFrames;
        bool wasPaused = Paused;
        CloseDevice();
        Open(deviceIndex, at);
        if (!wasPaused) Play();
    }

    /// <summary>Starts or resumes; after the end it starts again from the beginning.</summary>
    public void Play()
    {
        lock (_gate)
        {
            if (_finished) ResetLocked(_loopEnabled ? _loopStart : 0);
            _paused = false;
            Ok(waveOutRestart(_device));
        }
    }

    /// <summary>Pauses where it is.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            Ok(waveOutPause(_device));
        }
    }

    /// <summary>Back to the start and paused.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _paused = true;
            ResetLocked(0);
        }
    }

    /// <summary>Jumps to a frame.</summary>
    public void Seek(long frame)
    {
        lock (_gate) ResetLocked(System.Math.Max(0, System.Math.Min(frame, _wav.Frames - 1)));
    }

    /// <summary>The volume, 0..1 (applied to the samples with a squared curve).</summary>
    public float Volume { set { lock (_gate) _gain = value * value; } }

    /// <summary>The loop: an end after the start loops that part, otherwise the whole song.</summary>
    public void SetLoop(bool enabled, long regionStart, long regionEnd)
    {
        lock (_gate)
        {
            _loopEnabled = enabled;
            _loopStart = regionEnd > regionStart ? regionStart : 0;
            _loopEnd = regionEnd > regionStart ? regionEnd : _wav.Frames;
            if (enabled)
            {
                _feedingDone = false;
                _finished = false;
            }
        }
    }

    /// <summary>The frame being heard now.</summary>
    public long PositionFrames
    {
        get
        {
            lock (_gate)
            {
                if (_device == System.IntPtr.Zero) return _readFrame;
                MmTime time = new() { Type = 2 };
                Ok(waveOutGetPosition(_device, ref time, Marshal.SizeOf<MmTime>()));
                long played = time.Value;
                while (_segments.Count > 0 && _segments.Peek().PlayedStart + _segments.Peek().Frames <= played) _segments.Dequeue();
                if (_segments.Count == 0) return System.Math.Min(_readFrame, _wav.Frames);
                Segment s = _segments.Peek();
                return s.SongStart + System.Math.Max(0, played - s.PlayedStart);
            }
        }
    }

    private void ResetLocked(long frame)
    {
        Ok(waveOutReset(_device));
        if (_paused) Ok(waveOutPause(_device)); else Ok(waveOutRestart(_device));
        for (int i = 0; i < BufferCount; i++) _queued[i] = false;
        _segments.Clear();
        _writtenFrames = 0;
        _readFrame = frame;
        _feedingDone = false;
        _finished = false;
    }

    private void Feed()
    {
        int headerSize = Marshal.SizeOf<WaveHeader>();
        while (!_stopFeeder)
        {
            lock (_gate)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_queued[i] && (Marshal.PtrToStructure<WaveHeader>(_headers[i]).Flags & WhdrDone) != 0) _queued[i] = false;
                }

                for (int i = 0; i < BufferCount && !_feedingDone; i++)
                {
                    if (_queued[i]) continue;
                    int frames = FillLocked(_buffers[i]);
                    if (frames == 0) break;
                    WaveHeader header = Marshal.PtrToStructure<WaveHeader>(_headers[i]);
                    header.Length = (uint)(frames * _wav.Channels * 2);
                    Marshal.StructureToPtr(header, _headers[i], false);
                    Ok(waveOutWrite(_device, _headers[i], headerSize));
                    _queued[i] = true;
                }

                if (_feedingDone && !_finished && System.Array.TrueForAll(_queued, q => !q)) _finished = true;
            }

            System.Threading.Thread.Sleep(10);
        }
    }

    /// <summary>Copies the next piece of the song (volume applied) into a buffer; returns its length in frames, 0 at the end.</summary>
    private int FillLocked(System.IntPtr buffer)
    {
        long end = _loopEnabled ? _loopEnd : _wav.Frames;
        if (_readFrame >= end)
        {
            if (!_loopEnabled)
            {
                _feedingDone = true;
                return 0;
            }

            _readFrame = _loopStart;
            _loopCount++;
        }

        int frames = (int)System.Math.Min(_chunkFrames, end - _readFrame);
        int channels = _wav.Channels;
        short[] chunk = new short[frames * channels];
        for (int i = 0; i < chunk.Length; i++)
        {
            float v = _wav.Samples[_readFrame * channels + i] * _gain;
            chunk[i] = (short)System.Math.Max(-32768f, System.Math.Min(32767f, v));
        }

        Marshal.Copy(chunk, 0, buffer, chunk.Length);
        _segments.Enqueue(new Segment { PlayedStart = _writtenFrames, SongStart = _readFrame, Frames = frames });
        _writtenFrames += frames;
        _readFrame += frames;
        return frames;
    }

    /// <summary>Stops the feeder and closes the device.</summary>
    public void Dispose() => CloseDevice();
}

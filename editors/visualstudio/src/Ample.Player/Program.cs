namespace Ample.Player;

/// <summary>
/// The Echo: <c>Ample.Echo.exe file.wav [--song name]</c>. Shows a window with the waveform and the controls; the song name keys the
/// loop settings that are remembered per song.
/// </summary>
internal static class Program
{
    [System.STAThread]
    private static int Main(string[] args)
    {
        string? wav = null, song = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--song" && i + 1 < args.Length) song = args[++i];
            else wav ??= args[i];
        }

        if (wav is null || !System.IO.File.Exists(wav)) return 2;
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            System.Windows.Forms.Application.Run(new PlayerForm(wav, song ?? System.IO.Path.GetFileNameWithoutExtension(wav)));
            return 0;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine("Echo: cannot read " + wav + ": " + ex.Message);
            return 3;
        }
    }
}

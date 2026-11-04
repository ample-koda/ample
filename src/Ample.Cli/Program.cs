using Ample.Cli;

string? logFile = null;
bool visible = false;
if (args.Length > 0 && args[0] == "serve")
{
    for (int i = 1; i + 1 < args.Length; i++)
    {
        if (args[i] == "--log-file") logFile = args[i + 1];
        if (args[i] == "--visible") visible = true;
    }
}

if (logFile is null) return CliApplication.Run(args, Console.Out);

using RotatingLogWriter log = new(logFile, visible ? Console.Out : null);
Console.SetOut(log);
Console.SetError(log);
return CliApplication.Run(args, log);

using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Ample.VisualStudio;

internal static class AmpleInstanceDiscovery
{
    internal static async Task<IReadOnlyList<AmpleLocalInstance>> ListAsync()
    {
        ProcessStartInfo start = new(AmpleCli.For(null), "instances --json")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start) ?? throw new System.InvalidOperationException("The AMPLE CLI did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task exitTask = Task.Run(() => process.WaitForExit());
        if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(10))) != exitTask)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await exitTask;
            throw new InvalidOperationException("AMPLE instance discovery timed out.");
        }
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0) throw new System.InvalidOperationException($"AMPLE instance discovery failed (exit code {process.ExitCode}): {error.Trim()}");
        using System.IO.MemoryStream json = new(System.Text.Encoding.UTF8.GetBytes(output));
        return (IReadOnlyList<AmpleLocalInstance>?)new DataContractJsonSerializer(typeof(List<AmpleLocalInstance>)).ReadObject(json)
            ?? System.Array.Empty<AmpleLocalInstance>();
    }
}

[DataContract]
internal sealed class AmpleLocalInstance
{
    [DataMember(Name = "projectId")] public string ProjectId { get; set; } = string.Empty;
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "folder")] public string Folder { get; set; } = string.Empty;
    [DataMember(Name = "pid")] public int ProcessId { get; set; }
    [DataMember(Name = "port")] public int Port { get; set; }
    [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
    [DataMember(Name = "host")] public string Host { get; set; } = string.Empty;
    [DataMember(Name = "startedBy")] public string StartedBy { get; set; } = string.Empty;
}

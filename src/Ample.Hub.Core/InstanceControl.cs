using System.Diagnostics;
using Ample.Storage.Projects;

namespace Ample.Hub.Core;

/// <summary>Stops and starts AMPLE servers: the hub asks a server to stop itself (it kills nothing) and starts a server from its own recipe.</summary>
public static class InstanceControl
{
    /// <summary>Gets the session token from a launch URL (<c>http://127.0.0.1:port/#token=...</c>), or null.</summary>
    public static string? TokenOf(string? launchUrl)
    {
        const string marker = "#token=";
        int at = launchUrl?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : launchUrl![(at + marker.Length)..];
    }

    /// <summary>Asks a running instance to stop (POST /api/shutdown) and waits until its process is gone. Returns null when it stopped, else the reason.</summary>
    public static async Task<string?> StopAsync(InstanceRegistry registry, InstanceRecord instance, HttpClient http, TimeSpan wait, CancellationToken cancellationToken)
    {
        string? token = TokenOf(registry.ReadLaunchUrl(instance.ProjectId));
        if (token is null) return "The instance has no launch token, so it cannot be asked to stop.";
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{instance.Port}/api/shutdown");
        request.Headers.Add("X-Ample-Token", token);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return $"The instance refused to stop ({(int)response.StatusCode}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "The instance did not answer: " + ex.Message;
        }

        DateTime until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (!IsRunning(instance)) return null;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return "The instance was asked to stop but is still running.";
    }

    /// <summary>Starts a process hidden from a recipe (the program and its arguments).</summary>
    public static void Launch(InstanceRelaunch recipe)
    {
        ProcessStartInfo start = new(recipe.Exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in recipe.Args) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    private static bool IsRunning(InstanceRecord instance)
    {
        try
        {
            using Process process = Process.GetProcessById(instance.Pid);
            return !process.HasExited && Math.Abs((new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) - instance.ProcessStart).TotalSeconds) < 5;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

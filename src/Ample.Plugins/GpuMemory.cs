using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ample.Plugins;

/// <summary>
/// Reads the memory totals of the first GPU: free and total, in megabytes. Nothing else: no process list, no names, no arguments. It uses NVML
/// (<c>nvmlDeviceGetMemoryInfo</c>) when the driver library can be loaded and falls back to <c>nvidia-smi --query-gpu</c> (totals only).
/// </summary>
public static class GpuMemory
{
    private static readonly object Gate = new();
    private static bool nvmlTried;
    private static NvmlMemoryInfoDelegate? nvmlMemory;
    private static IntPtr nvmlDevice;

    private delegate int NvmlInitDelegate();

    private delegate int NvmlDeviceByIndexDelegate(uint index, out IntPtr device);

    private delegate int NvmlMemoryInfoDelegate(IntPtr device, out NvmlMemory memory);

    /// <summary>Reads the totals, or null when no NVIDIA GPU or driver can be reached.</summary>
    public static GpuTotals? Read() => ReadNvml() ?? ReadSmi();

    private static GpuTotals? ReadNvml()
    {
        lock (Gate)
        {
            if (!nvmlTried)
            {
                nvmlTried = true;
                TryLoadNvml();
            }

            if (nvmlMemory is null)
            {
                return null;
            }

            try
            {
                return nvmlMemory(nvmlDevice, out NvmlMemory memory) == 0
                    ? new GpuTotals((long)(memory.Free / (1024 * 1024)), (long)(memory.Total / (1024 * 1024)), "nvml")
                    : null;
            }
            catch (Exception ex) when (ex is MarshalDirectiveException or SEHException or AccessViolationException)
            {
                nvmlMemory = null;
                return null;
            }
        }
    }

    private static void TryLoadNvml()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["nvml.dll", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")]
            : ["libnvidia-ml.so.1", "libnvidia-ml.so"];
        foreach (string name in candidates)
        {
            if (!NativeLibrary.TryLoad(name, out IntPtr library))
            {
                continue;
            }

            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out IntPtr init)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out IntPtr byIndex)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetMemoryInfo", out IntPtr info))
            {
                continue;
            }

            if (Marshal.GetDelegateForFunctionPointer<NvmlInitDelegate>(init)() != 0)
            {
                continue;
            }

            if (Marshal.GetDelegateForFunctionPointer<NvmlDeviceByIndexDelegate>(byIndex)(0, out IntPtr device) != 0)
            {
                continue;
            }

            nvmlDevice = device;
            nvmlMemory = Marshal.GetDelegateForFunctionPointer<NvmlMemoryInfoDelegate>(info);
            return;
        }
    }

    private static GpuTotals? ReadSmi()
    {
        try
        {
            ProcessStartInfo info = new("nvidia-smi", "--query-gpu=memory.free,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            string first = process.StandardOutput.ReadLine() ?? string.Empty;
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }

            string[] parts = first.Split(',', StringSplitOptions.TrimEntries);
            return parts.Length >= 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long free)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long total)
                ? new GpuTotals(free, total, "nvidia-smi")
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }
}

/// <summary>Free and total memory of the GPU in megabytes, and where the numbers came from (<c>nvml</c> or <c>nvidia-smi</c>).</summary>
/// <param name="FreeMb">Free memory.</param>
/// <param name="TotalMb">Total memory.</param>
/// <param name="Source">The reader that produced the numbers.</param>
public sealed record GpuTotals(long FreeMb, long TotalMb, string Source)
{
    /// <summary>Gets the memory in use by all programs together.</summary>
    public long UsedMb => TotalMb - FreeMb;
}

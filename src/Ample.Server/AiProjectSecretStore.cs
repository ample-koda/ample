using System.Security.Cryptography;
using System.Text;

namespace Ample.Server;

internal static class AiProjectSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AMPLE project secrets v1");

    internal static bool IsConfigured(string home, Guid projectId) => File.Exists(PathFor(home, projectId));

    internal static string? Read(string home, Guid projectId)
    {
        string path = PathFor(home, projectId);
        if (!File.Exists(path) || !OperatingSystem.IsWindows()) return null;
        byte[] cipher = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
    }

    internal static void Save(string home, Guid projectId, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Project-specific Brave keys currently require Windows DPAPI.");
        string path = PathFor(home, projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
    }

    internal static void Clear(string home, Guid projectId)
    {
        string path = PathFor(home, projectId);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string home, Guid projectId) => Path.Combine(home, "project-secrets", projectId.ToString("N") + ".bin");
}

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Ample.Plugins;

/// <summary>Machine-local Faer connection details; project files contain only capability routes.</summary>
public sealed record FaerProfile(Uri Endpoint, string TokenFile, string CertificateFile)
{
    /// <summary>Loads the current user's Faer profile, if configured.</summary>
    public static FaerProfile? LoadLocal()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "faer");
        string file = Path.Combine(folder, "profile.json");
        if (!File.Exists(file)) return null;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = document.RootElement;
        Uri endpoint = new(root.GetProperty("endpoint").GetString()!, UriKind.Absolute);
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("A public Faer profile requires HTTPS");
        string token = LocalFile(folder, root.GetProperty("tokenFile").GetString()!);
        string certificate = LocalFile(folder, root.GetProperty("certificateFile").GetString()!);
        return new FaerProfile(endpoint, token, certificate);
    }

    private static string LocalFile(string folder, string name)
    {
        if (Path.GetFileName(name) != name || name is "." or "..") throw new InvalidDataException("Faer profile file name is invalid");
        return Path.Combine(folder, name);
    }

    /// <summary>Creates a client that pins the server certificate and reads the private bearer token.</summary>
    public FaerClient CreateClient()
    {
        using X509Certificate2 pinned = X509Certificate2.CreateFromPem(File.ReadAllText(CertificateFile));
        byte[] expected = SHA256.HashData(pinned.RawData);
        HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            if (request.RequestUri?.Host != Endpoint.Host || certificate is null || errors is not (SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors)) return false;
            return CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(certificate.RawData));
        };
        HttpClient http = new(handler) { Timeout = TimeSpan.FromMinutes(60) };
        return new FaerClient(http, Endpoint, File.ReadAllText(TokenFile).Trim());
    }
}

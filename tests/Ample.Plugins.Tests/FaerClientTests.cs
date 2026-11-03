using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Ample.Plugins.Tests;

public sealed class FaerClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object value) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void ProfilePinsPublicCertificatePemWithoutRequiringItsPrivateKey()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-faer-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using ECDsa key = ECDsa.Create();
            CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            string certificatePath = Path.Combine(folder, "server.crt");
            string tokenPath = Path.Combine(folder, "token");
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(tokenPath, "synthetic-token");
            using FaerClient client = new FaerProfile(new Uri("https://localhost:31146"), tokenPath, certificatePath).CreateClient();
            Assert.NotNull(client);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task UploadResumesAtRemoteOffsetAndSkipsVerifiedCopy()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-faer-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "synthetic.bin");
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic upload content");
        await File.WriteAllBytesAsync(file, bytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        int received = 4;
        List<int> sent = [];
        using HttpClient http = new(new StubHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("/v1/assets/" + hash, request.RequestUri?.AbsolutePath);
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, new { present = received == bytes.Length, received, size = received == bytes.Length ? (long?)bytes.Length : null });
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(received.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Headers.GetValues("X-Faer-Offset").Single());
            byte[] chunk = await request.Content!.ReadAsByteArrayAsync();
            sent.Add(chunk.Length);
            Assert.Equal(bytes.AsSpan(received).ToArray(), chunk);
            received += chunk.Length;
            return Json(HttpStatusCode.Created, new { present = true, received });
        }));
        try
        {
            FaerClient client = new(http, new Uri("http://127.0.0.1:3888"), "synthetic-token");
            List<long> progress = [];
            Assert.Equal(hash, await client.UploadFileAsync(file, (done, _) => progress.Add(done), CancellationToken.None));
            Assert.Equal([4, bytes.Length], progress);
            Assert.Equal([bytes.Length - 4], sent);
            Assert.Equal(hash, await client.UploadFileAsync(file, null, CancellationToken.None));
            Assert.Single(sent);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadPublishesOnlyVerifiedArtifact(bool corrupt)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-faer-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "result.bin");
        byte[] expected = Encoding.UTF8.GetBytes("synthetic result content");
        string hash = Convert.ToHexStringLower(SHA256.HashData(expected));
        byte[] served = corrupt ? Encoding.UTF8.GetBytes("synthetic result contene") : expected;
        using HttpClient http = new(new StubHandler(request =>
        {
            Assert.Equal("/v1/assets/" + hash + "/bytes", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) });
        }));
        try
        {
            FaerClient client = new(http, new Uri("http://127.0.0.1:3888"), null);
            FaerOutput output = new("audio", hash, expected.Length);
            if (corrupt)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(output, destination, null, CancellationToken.None));
                Assert.False(File.Exists(destination));
                Assert.Empty(Directory.GetFiles(folder));
            }
            else
            {
                await client.DownloadFileAsync(output, destination, null, CancellationToken.None);
                Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

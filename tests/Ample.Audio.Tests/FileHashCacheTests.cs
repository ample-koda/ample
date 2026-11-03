using System.Security.Cryptography;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class FileHashCacheTests
{
    [Fact]
    public void The_hash_is_the_sha256_of_the_bytes_and_follows_a_changed_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ample-hash-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] first = [1, 2, 3, 4, 5, 6, 7, 8];
            File.WriteAllBytes(path, first);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(first)), FileHashCache.Sha256Hex(path));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(first)), FileHashCache.Sha256Hex(path));

            byte[] second = [8, 7, 6, 5, 4, 3, 2, 1];   // same length, another content and last-write time
            File.WriteAllBytes(path, second);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(second)), FileHashCache.Sha256Hex(path));

            File.WriteAllBytes(path, [9, 9, 9]);   // another length
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 9, 9, 9 })), FileHashCache.Sha256Hex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

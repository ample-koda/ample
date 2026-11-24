namespace Ample.Server.Tests;

/// <summary>The file with the password hash is for its owner only (on Unix), is replaced as a whole, and leaves nothing behind.</summary>
public sealed class AuthFileTests
{
    [Fact]
    public void The_hash_file_is_private_replaced_whole_and_leaves_no_temporary_file()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-authfile-" + Guid.NewGuid().ToString("N"));
        try
        {
            AuthService.SetPassword(home, "koda", "a-long-password");
            string file = AuthService.FilePath(home);
            Assert.NotNull(AuthService.Load(home));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

            AuthService.SetPassword(home, "koda", "another-long-password");
            AuthService second = AuthService.Load(home)!;
            Assert.NotNull(second.TryLogin("203.0.113.1", "koda", "another-long-password", out bool locked));
            Assert.False(locked);
            Assert.Equal([file], Directory.GetFiles(home));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            try { Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }
}

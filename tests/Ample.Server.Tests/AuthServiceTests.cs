
namespace Ample.Server.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-auth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    [Fact]
    public void Nothing_is_loaded_until_a_password_was_set()
    {
        Assert.Null(AuthService.Load(home));
    }

    [Fact]
    public void A_short_password_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AuthService.SetPassword(home, "koda", "short"));
    }

    [Fact]
    public void Only_a_hash_is_stored_and_the_right_credentials_give_a_session()
    {
        AuthService.SetPassword(home, "koda", "a-long-password");
        Assert.DoesNotContain("a-long-password", File.ReadAllText(AuthService.FilePath(home)));
        AuthService auth = AuthService.Load(home)!;
        string? token = auth.TryLogin("c1", "koda", "a-long-password", out bool locked);
        Assert.NotNull(token);
        Assert.False(locked);
        Assert.True(auth.IsSession(token!));
        auth.Logout(token!);
        Assert.False(auth.IsSession(token!));
        Assert.False(auth.IsSession("not-a-session"));
    }

    [Fact]
    public void Wrong_credentials_fail_and_five_failures_lock_the_caller_out()
    {
        AuthService.SetPassword(home, "koda", "a-long-password");
        AuthService auth = AuthService.Load(home)!;
        Assert.Null(auth.TryLogin("c1", "koda", "wrong-password", out _));
        Assert.Null(auth.TryLogin("c1", "other", "a-long-password", out _));
        for (int i = 0; i < 3; i++) Assert.Null(auth.TryLogin("c1", "koda", "wrong-password", out _));
        Assert.Null(auth.TryLogin("c1", "koda", "a-long-password", out bool locked));
        Assert.True(locked);
        Assert.NotNull(auth.TryLogin("c2", "koda", "a-long-password", out _));
    }
}

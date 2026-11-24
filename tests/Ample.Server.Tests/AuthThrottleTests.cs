namespace Ample.Server.Tests;

/// <summary>The lockout after wrong passwords counts an attempt before the slow hash is computed, so a burst of parallel guesses cannot get past it.</summary>
public sealed class AuthThrottleTests
{
    private static AuthService Make(out string home)
    {
        home = Path.Combine(Path.GetTempPath(), "ample-auth-" + Guid.NewGuid().ToString("N"));
        AuthService.SetPassword(home, "koda", "a-long-password");
        return AuthService.Load(home)!;
    }

    [Fact]
    public async Task A_burst_of_parallel_wrong_guesses_gets_only_five_real_attempts()
    {
        AuthService auth = Make(out string home);
        try
        {
            int attempted = 0;
            await Parallel.ForAsync(0, 60, new ParallelOptions { MaxDegreeOfParallelism = 60 }, (_, _) =>
            {
                string? token = auth.TryLogin("203.0.113.9", "koda", "wrong-guess", out bool locked);
                Assert.Null(token);
                if (!locked) Interlocked.Increment(ref attempted);
                return ValueTask.CompletedTask;
            });

            Assert.Equal(5, attempted);
            // locked now: even the right password is refused for a minute
            Assert.Null(auth.TryLogin("203.0.113.9", "koda", "a-long-password", out bool stillLocked));
            Assert.True(stillLocked);
        }
        finally
        {
            try { Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void A_right_password_signs_in_and_clears_the_count_and_another_address_is_not_locked()
    {
        AuthService auth = Make(out string home);
        try
        {
            for (int i = 0; i < 4; i++) Assert.Null(auth.TryLogin("198.51.100.1", "koda", "nope", out _));
            string? token = auth.TryLogin("198.51.100.1", "koda", "a-long-password", out bool locked);
            Assert.NotNull(token);
            Assert.False(locked);
            for (int i = 0; i < 4; i++) Assert.Null(auth.TryLogin("198.51.100.1", "koda", "nope", out _));   // the count started again
            Assert.NotNull(auth.TryLogin("198.51.100.1", "koda", "a-long-password", out _));

            for (int i = 0; i < 6; i++) auth.TryLogin("198.51.100.2", "koda", "nope", out _);
            Assert.NotNull(auth.TryLogin("198.51.100.3", "koda", "a-long-password", out bool other));
            Assert.False(other);
            Assert.True(auth.IsSession(token!));
        }
        finally
        {
            try { Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }
}

using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Tests;

public sealed class SecureCookieStoreTests
{
    [Fact]
    public async Task save_read_and_clear_round_trips_cookie_header()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cookie-secrets.json");
        var store = new SecureCookieStore(path, new ReversingSecretProtector());

        await store.SaveCookieHeaderAsync("Claude", "Cookie: sessionKey=abc; other=123");

        Assert.Equal("sessionKey=abc; other=123", await store.ReadCookieHeaderAsync("claude"));

        await store.ClearCookieHeaderAsync("claude");

        Assert.Null(await store.ReadCookieHeaderAsync("claude"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task saved_cookie_file_does_not_contain_plaintext_secret()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cookie-secrets.json");
        var store = new SecureCookieStore(path, new ReversingSecretProtector());

        await store.SaveCookieHeaderAsync("opencode", "auth=secret-value");

        var storedText = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-value", storedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task save_rejects_empty_cookie_header()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cookie-secrets.json");
        var store = new SecureCookieStore(path, new ReversingSecretProtector());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveCookieHeaderAsync("claude", "   "));
    }

    private sealed class ReversingSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
        {
            return plaintext.Reverse().ToArray();
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return protectedData.Reverse().ToArray();
        }
    }
}

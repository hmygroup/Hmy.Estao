using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UserMessageTests
{
    [Fact]
    public void CodesAndArgumentsSurviveSerializationWithoutDependingOnCulture()
    {
        var message = new UserMessage("error.http", "Codex", 401);
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error.http", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(401, doc.RootElement.GetProperty("arguments")[1].GetInt32());
        Assert.DoesNotContain("returned", json);
    }

    [Fact]
    public void ExceptionsKeepTheirDiagnosticTypeAndProvideSeparatePresentationMetadata()
    {
        var exception = new InvalidDataException("Diagnostic detail").WithUserMessage("config.version", 7);
        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal("Diagnostic detail", exception.Message);
        Assert.Equal("config.version", UserMessage.FromException(exception).Code);
        Assert.Equal(7, UserMessage.FromException(exception).Arguments[0]);
        Assert.Equal("error.network", UserMessage.FromException(new HttpRequestException("untrusted body")).Code);
        Assert.Empty(UserMessage.FromException(new Exception("secret")).Arguments);
    }

    [Fact]
    public void ValidationHasStructuredMessagesAndKeepsLegacyDiagnostics()
    {
        var config = new EstaoConfig { Version = 2, Providers = [new() { Id = "codex", Source = "invalid", CookieSource = "invalid" }] };
        var messages = ConfigValidation.ValidateMessages(config);
        Assert.Equal(ConfigValidation.Validate(config).Count, messages.Count);
        Assert.Equal(new[] { "config.version", "config.source", "config.cookie_source" }, messages.Select(m => m.Code));
        Assert.Equal("codex", messages[1].Arguments[0]);
    }

    [Fact]
    public async Task LanguagePreferenceRoundTripsAndOldConfigurationsDefaultToSystem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-language-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(Path.Combine(directory, "config.json"));
            Assert.Equal("system", (await store.LoadAsync()).Language);
            var config = ConfigStore.CreateDefaultConfig();
            config.Language = "es";
            await store.SaveAsync(config);
            Assert.Equal("es", (await store.LoadAsync()).Language);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UsageFailuresAndWindowLabelsHaveLanguageIndependentMessages()
    {
        var snapshot = UsageSnapshot.Failure("codex", "cli", "English diagnostic");
        Assert.Equal("error.provider", snapshot.ErrorMessage!.Code);
        Assert.Equal("English diagnostic", snapshot.Error);
        Assert.Equal("window.weekly", new RateWindow("weekly", "Weekly", .2, null).TitleMessage.Code);
        Assert.Equal("window.premium", new RateWindow("premium_interactions", "Premium", .2, null).TitleMessage.Code);
    }
}

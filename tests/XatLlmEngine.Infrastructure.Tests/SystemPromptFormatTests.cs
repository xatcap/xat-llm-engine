using System.Net;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.Anthropic;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests that every provider format obeys the same system prompt rules. The local model uses the OpenAI format.</summary>
public class SystemPromptFormatTests
{
    /// <summary>A correct request body.</summary>
    private const string ValidBody = """{"messages":[{"role":"user","content":"Hi"}]}""";

    /// <summary>The provider formats to test.</summary>
    public static TheoryData<string> Formats => ["anthropic", "openai"];

    /// <summary>Every format refuses a request that sets its own prompt, or that has no message list.</summary>
    /// <param name="format">The provider format.</param>
    /// <param name="body">The request body.</param>
    /// <param name="stored">The stored prompt.</param>
    /// <param name="message">Text that the error must have.</param>
    [Theory]
    [InlineData("anthropic", """{"system":"x","messages":[]}""", null, "system prompt")]
    [InlineData("openai", """{"system":null,"messages":[]}""", null, "system prompt")]
    [InlineData("anthropic", """{"messages":[{"role":"system","content":"x"}]}""", null, "system prompt")]
    [InlineData("openai", """{"messages":[{"role":"system","content":"x"}]}""", null, "system prompt")]
    [InlineData("openai", """{"messages":[{"role":"developer","content":"x"}]}""", null, "system prompt")]
    [InlineData("openai", """{"messages":[{"role":"System","content":"x"}]}""", null, "system prompt")]
    [InlineData("openai", """{"messages":[{"role":"user","content":"a"},{"role":"system","content":"x"}]}""", "stored", "system prompt")]
    [InlineData("anthropic", """{"messages":"Hi"}""", "stored", "'messages'")]
    [InlineData("openai", """{"messages":"Hi"}""", "stored", "'messages'")]
    [InlineData("openai", """{"messages":{"role":"user","content":"Hi"}}""", null, "'messages'")]
    [InlineData("anthropic", """{"temperature":1}""", null, "'messages'")]
    [InlineData("openai", """{"temperature":1}""", "stored", "'messages'")]
    public void Apply_BadRequest_Fails(string format, string body, string? stored, string message)
    {
        var result = Provider(format).ApplySystemPrompt(Parse(body), stored);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Contains(message, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>A message with an unusual role goes to the provider, which decides whether that role is valid.</summary>
    /// <param name="format">The provider format.</param>
    [Theory]
    [MemberData(nameof(Formats))]
    public void Apply_UnusualMessages_AreAccepted(string format)
    {
        var result = Provider(format).ApplySystemPrompt(Parse("""{"messages":[{"role":5,"content":"x"},"text"]}"""), null);

        Assert.True(result.IsSuccess);
    }

    /// <summary>With no stored prompt, every format sends the caller's body unchanged, so a model's own default applies.</summary>
    /// <param name="format">The provider format.</param>
    [Theory]
    [MemberData(nameof(Formats))]
    public void Apply_NoStoredPrompt_LeavesBodyUnchanged(string format)
    {
        var body = Parse(ValidBody);

        var result = Provider(format).ApplySystemPrompt(body, null);

        Assert.Equal(ValidBody, result.Value!.ToJsonString());
    }

    /// <summary>The Anthropic format sends the prompt in the <c>system</c> field, and the caller's body stays unchanged.</summary>
    [Fact]
    public void Apply_Anthropic_SetsSystemField()
    {
        var body = Parse(ValidBody);

        var result = Provider("anthropic").ApplySystemPrompt(body, "Be brief.");

        Assert.Equal("Be brief.", result.Value!["system"]!.GetValue<string>());
        Assert.Equal(ValidBody, body.ToJsonString());
    }

    /// <summary>The OpenAI format sends the prompt as the first message, and the caller's body stays unchanged.</summary>
    [Fact]
    public void Apply_OpenAi_InsertsFirstMessage()
    {
        var body = Parse(ValidBody);

        var messages = Provider("openai").ApplySystemPrompt(body, "Be brief.").Value!["messages"]!.AsArray();

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("Be brief.", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal(ValidBody, body.ToJsonString());
    }

    /// <summary>Parses a request body.</summary>
    /// <param name="body">The JSON.</param>
    /// <returns>The object.</returns>
    private static JsonObject Parse(string body) => JsonNode.Parse(body)!.AsObject();

    /// <summary>Builds a provider of one format.</summary>
    /// <param name="format">The provider format.</param>
    /// <returns>The provider.</returns>
    private static IChatProvider Provider(string format)
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, "{}");
        var options = new ProviderOptions { BaseUrl = new Uri("https://api.example.com"), ApiKey = "secret", RequestFields = ["messages"] };
        return format == "anthropic"
            ? new AnthropicChatProvider("test", options, http, "test")
            : new OpenAiCompatibleChatProvider("test", options, new OpenAiClient("test", http, "test"));
    }
}

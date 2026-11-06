using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the portable fields, which let a caller change the provider with only a change of 'model'. See ADR 0012.</summary>
public class PortableFieldTests
{
    /// <summary>The default template of the local model.</summary>
    private static readonly string[] LocalFields = ["messages", "temperature", "top_p", "max_tokens", "stop", "stream_options", "top_k", "min_p", "repeat_penalty", "seed", "frequency_penalty", "presence_penalty", "response_format", "grammar"];

    /// <summary>The default template of OpenAI.</summary>
    private static readonly string[] OpenAiFields = ["messages", "temperature", "top_p", "max_completion_tokens", "stop", "stream_options", "seed", "frequency_penalty", "presence_penalty", "response_format", "reasoning_effort", "user"];

    /// <summary>The default template of Mistral.</summary>
    private static readonly string[] MistralFields = ["messages", "temperature", "top_p", "max_tokens", "stop", "random_seed", "safe_prompt", "frequency_penalty", "presence_penalty", "response_format"];

    /// <summary>The default template of Anthropic.</summary>
    private static readonly string[] AnthropicFields = ["messages", "system", "max_tokens", "temperature", "top_p", "top_k", "stop_sequences", "metadata"];

    /// <summary>A body with only portable fields.</summary>
    private const string PortableBody = """
        {"messages":[{"role":"user","content":"Hi"}],"temperature":0.2,"top_p":0.9,"stop":["\n"],"frequency_penalty":0.1,"presence_penalty":0.1,"response_format":{"type":"json_object"},"max_tokens":100,"seed":7}
        """;

    /// <summary>One portable body is accepted by each OpenAI-format provider, and each one gets its own native names with the same values.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="tokenField">The native token field.</param>
    /// <param name="seedField">The native seed field.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("local", "max_tokens", "seed")]
    [InlineData("openai", "max_completion_tokens", "seed")]
    [InlineData("mistral", "max_tokens", "random_seed")]
    public async Task PortableBody_EachProvider_GetsNativeNames(string provider, string tokenField, string seedField)
    {
        var fake = Provider(provider);

        var result = await CompleteAsync(fake, PortableBody);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        var sent = fake.LastRequest!.Body;
        Assert.Equal(100, sent[tokenField]!.GetValue<int>());
        Assert.Equal(7, sent[seedField]!.GetValue<int>());
        Assert.Equal(0.2, sent["temperature"]!.GetValue<double>());
        Assert.Equal("""{"type":"json_object"}""", sent["response_format"]!.ToJsonString());
        Assert.All(sent.Select(field => field.Key), name => Assert.Contains(name, fake.RequestFields.Append("model")));
    }

    /// <summary>The OpenAI library name of the token field also reaches local and Mistral as 'max_tokens'.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("local")]
    [InlineData("mistral")]
    public async Task MaxCompletionTokens_ToMaxTokensProvider_IsRenamed(string provider)
    {
        var fake = Provider(provider);

        var result = await CompleteAsync(fake, """{"messages":[],"max_completion_tokens":100}""");

        Assert.True(result.IsSuccess);
        Assert.Equal(100, fake.LastRequest!.Body["max_tokens"]!.GetValue<int>());
        Assert.False(fake.LastRequest.Body.ContainsKey("max_completion_tokens"));
    }

    /// <summary>A request with only native names reaches the provider exactly as the caller sent it.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="body">A native body.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("local", """{"messages":[],"max_tokens":5,"seed":1,"top_k":40,"grammar":"root ::= \"a\""}""")]
    [InlineData("openai", """{"messages":[],"max_completion_tokens":5,"seed":1,"reasoning_effort":"low"}""")]
    [InlineData("mistral", """{"messages":[],"max_tokens":5,"random_seed":1,"safe_prompt":true}""")]
    [InlineData("anthropic", """{"messages":[],"system":"s","max_tokens":5,"stop_sequences":["x"]}""")]
    public async Task NativeBody_IsUnchanged(string provider, string body)
    {
        var fake = Provider(provider);

        var result = await CompleteAsync(fake, body);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(JsonNode.Parse(body)!.ToJsonString(), Body(fake).ToJsonString());
    }

    /// <summary>Two names for one setting in one request are refused, and the error names both.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="first">The first field.</param>
    /// <param name="second">The second field.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("mistral", "seed", "random_seed")]
    [InlineData("openai", "max_tokens", "max_completion_tokens")]
    [InlineData("local", "max_completion_tokens", "max_tokens")]
    public async Task TwoNamesForOneSetting_AreRefused(string provider, string first, string second)
    {
        var fake = Provider(provider);

        var result = await CompleteAsync(fake, $$"""{"messages":[],"{{first}}":5,"{{second}}":5}""");

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Contains($"'{first}'", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains($"'{second}'", result.Error.Message, StringComparison.Ordinal);
        Assert.Null(fake.LastRequest);
    }

    /// <summary>A template that lists both token names still forwards both, as before, because no rename takes place.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task TemplateWithBothTokenNames_ForwardsBoth()
    {
        var fake = new FakeChatProvider("custom") { AcceptsPortableFields = true, RequestFields = ["messages", "max_tokens", "max_completion_tokens"] };

        var result = await CompleteAsync(fake, """{"messages":[],"max_tokens":5,"max_completion_tokens":6}""");

        Assert.True(result.IsSuccess);
        Assert.Equal(5, fake.LastRequest!.Body["max_tokens"]!.GetValue<int>());
        Assert.Equal(6, fake.LastRequest.Body["max_completion_tokens"]!.GetValue<int>());
    }

    /// <summary>A field that only one provider has is refused by another provider, with a message that it is not portable.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="field">A field of another provider.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("openai", "grammar")]
    [InlineData("mistral", "reasoning_effort")]
    [InlineData("local", "safe_prompt")]
    [InlineData("openai", "random_seed")]
    public async Task OtherProvidersField_IsRefusedAsNotPortable(string provider, string field)
    {
        var result = await CompleteAsync(Provider(provider), $$"""{"messages":[],"{{field}}":1}""");

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Contains($"The field '{field}' is not portable", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Portable fields: messages", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>The list of supported fields in an error also names the portable fields that the provider accepts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task NotSupportedMessage_ListsAcceptedPortableNames()
    {
        var result = await CompleteAsync(Provider("mistral"), """{"messages":[],"top_k":1}""");

        var supported = result.Error!.Message.Split("Supported fields: ")[1].Split('.')[0];
        Assert.Contains("seed", supported.Split(", "));
        Assert.Contains("max_completion_tokens", supported.Split(", "));
        Assert.Contains("stream_options", supported.Split(", "));
    }

    /// <summary>Anthropic does not accept portable names, and its error message is the same as before.</summary>
    /// <param name="field">A portable field that the Anthropic template does not list.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("max_completion_tokens")]
    [InlineData("seed")]
    [InlineData("stream_options")]
    public async Task Anthropic_PortableName_IsRefusedAsBefore(string field)
    {
        var result = await CompleteAsync(Provider("anthropic"), $$$"""{"messages":[],"{{{field}}}":{"include_usage":true}}""");

        Assert.Equal(
            $"The provider 'anthropic' does not support the field '{field}'. Supported fields: model, stream, {string.Join(", ", AnthropicFields)}.",
            result.Error?.Message);
    }

    /// <summary>The token limit applies after the rename, and its errors name the field that the caller sent.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Limit_RenamedField_ErrorNamesSentField()
    {
        var fake = Provider("openai", maxTokensLimit: 50);

        var above = await CompleteAsync(fake, """{"messages":[],"max_tokens":100}""");
        var notNumber = await CompleteAsync(fake, """{"messages":[],"max_tokens":"x"}""");
        var below = await CompleteAsync(fake, """{"messages":[],"max_tokens":40}""");

        Assert.Equal("The provider 'openai' allows at most 50 tokens in 'max_tokens'.", above.Error?.Message);
        Assert.Equal("The field 'max_tokens' must be a number.", notNumber.Error?.Message);
        Assert.True(below.IsSuccess);
        Assert.Equal(40, fake.LastRequest!.Body["max_completion_tokens"]!.GetValue<int>());
    }

    /// <summary>A request with no token field gets the limit under the native name.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Limit_NoTokenField_AddsNativeName()
    {
        var fake = Provider("openai", maxTokensLimit: 50);

        await CompleteAsync(fake, """{"messages":[]}""");

        Assert.Equal(50, fake.LastRequest!.Body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(fake.LastRequest.Body.ContainsKey("max_tokens"));
    }

    /// <summary>For a template with no 'stream_options', the engine removes the caller's 'stream_options', and the caller's 'include_usage' decides.</summary>
    /// <param name="streamOptions">The caller's stream options, or an empty value for none.</param>
    /// <param name="expected">Whether the caller gets the token counts.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{"include_usage":true}""", true)]
    [InlineData("""{"include_usage":false}""", false)]
    [InlineData("", true)]
    public async Task Mistral_StreamOptions_CallerDecidesUsage(string streamOptions, bool expected)
    {
        var fake = Provider("mistral");
        var body = new JsonObject { ["messages"] = new JsonArray() };
        if (streamOptions.Length > 0)
        {
            body["stream_options"] = JsonNode.Parse(streamOptions);
        }

        var result = await new StreamChatHandler(new ProviderRegistry([fake])).HandleAsync(new("mistral/m", body), TestContext.Current.CancellationToken);
        await using var answer = result.Value!;

        Assert.Equal(expected, answer.ForwardUsage);
        Assert.False(fake.LastRequest!.Body.ContainsKey("stream_options"));
    }

    /// <summary>The engine refuses other content in 'stream_options' for a template with none, because it would have no effect.</summary>
    /// <param name="streamOptions">The caller's stream options.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{"include_usage":true,"include_obfuscation":false}""")]
    [InlineData("""{"include_usage":"yes"}""")]
    [InlineData("true")]
    public async Task Mistral_OtherStreamOptions_AreRefused(string streamOptions)
    {
        var result = await CompleteAsync(Provider("mistral"), $$"""{"messages":[],"stream_options":{{streamOptions}}}""");

        Assert.Equal("The provider 'mistral' accepts 'stream_options' only with the one field 'include_usage', with the value true or false.", result.Error?.Message);
    }

    /// <summary>A null value is renamed with its value unchanged. The vendor decides about it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Rename_NullValue_KeepsNull()
    {
        var fake = Provider("mistral");

        var result = await CompleteAsync(fake, """{"messages":[],"seed":null}""");

        Assert.True(result.IsSuccess);
        Assert.True(fake.LastRequest!.Body.ContainsKey("random_seed"));
        Assert.Null(fake.LastRequest.Body["random_seed"]);
    }

    /// <summary>A complete request with 'stream_options' to Mistral is accepted, and the field does not reach Mistral.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Mistral_StreamOptionsInCompleteRequest_IsRemoved()
    {
        var fake = Provider("mistral");

        var result = await CompleteAsync(fake, """{"messages":[],"stream_options":{"include_usage":true}}""");

        Assert.True(result.IsSuccess);
        Assert.False(fake.LastRequest!.Body.ContainsKey("stream_options"));
    }

    /// <summary>The engine never changes the caller's body, also when it renames fields.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Rename_DoesNotChangeCallersBody()
    {
        var body = JsonNode.Parse(PortableBody)!.AsObject();
        var before = body.ToJsonString();

        await new CompleteChatHandler(new ProviderRegistry([Provider("mistral")])).HandleAsync(new("mistral/m", body), TestContext.Current.CancellationToken);

        Assert.Equal(before, body.ToJsonString());
    }

    /// <summary>Builds a provider with a default template.</summary>
    /// <param name="name">The provider's name, which selects the template.</param>
    /// <param name="maxTokensLimit">The token limit.</param>
    /// <returns>The provider.</returns>
    private static FakeChatProvider Provider(string name, int maxTokensLimit = 0) => new(name)
    {
        RequestFields = name switch
        {
            "local" => LocalFields,
            "openai" => OpenAiFields,
            "mistral" => MistralFields,
            _ => AnthropicFields,
        },
        AcceptsPortableFields = name != "anthropic",
        MaxTokensLimit = maxTokensLimit,
    };

    /// <summary>Sends a complete request to a provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="body">The body.</param>
    /// <returns>The result.</returns>
    private static Task<Result<(ChatCompletion, string)>> CompleteAsync(FakeChatProvider provider, string body) =>
        new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new($"{provider.Name}/m", JsonNode.Parse(body)!.AsObject()), TestContext.Current.CancellationToken);

    /// <summary>Gives the body that the provider got.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The body.</returns>
    private static JsonObject Body(FakeChatProvider provider) => provider.LastRequest!.Body;
}

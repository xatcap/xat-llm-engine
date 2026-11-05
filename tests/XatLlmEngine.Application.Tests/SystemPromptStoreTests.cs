using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the stored system prompts and the use cases that change them.</summary>
public class SystemPromptStoreTests
{
    /// <summary>Runs the replace use case.</summary>
    /// <param name="store">The stored prompts.</param>
    /// <param name="provider">The provider's name.</param>
    /// <param name="prompt">The new prompt.</param>
    /// <returns>The result.</returns>
    private static Result<string> Replace(SystemPromptStore store, string provider, string prompt) =>
        new ReplaceSystemPromptHandler(store).Handle(new SystemPromptCommand(provider, prompt));

    /// <summary>Runs the append use case.</summary>
    /// <param name="store">The stored prompts.</param>
    /// <param name="provider">The provider's name.</param>
    /// <param name="text">The text to add.</param>
    /// <returns>The result.</returns>
    private static Result<string> Append(SystemPromptStore store, string provider, string text) =>
        new AppendSystemPromptHandler(store).Handle(new SystemPromptCommand(provider, text));

    /// <summary>The store starts with the configured prompt, and a blank value means no prompt.</summary>
    [Fact]
    public void Get_ConfiguredPrompts_StartValues()
    {
        var store = new SystemPromptStore(
        [
            new FakeChatProvider("set") { InitialSystemPrompt = "Be brief." },
            new FakeChatProvider("blank") { InitialSystemPrompt = string.Empty },
            new FakeChatProvider("missing"),
        ]);

        Assert.Equal("Be brief.", store.Get("set"));
        Assert.Null(store.Get("blank"));
        Assert.Null(store.Get("missing"));
        Assert.Null(store.Get("unknown"));
    }

    /// <summary>Replace changes the prompt, and an empty prompt clears it.</summary>
    [Fact]
    public void Replace_KnownProvider_ChangesPrompt()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud") { InitialSystemPrompt = "Old." }]);

        Assert.Equal("New.", Replace(store, "cloud", "New.").Value);
        Assert.Equal("New.", store.Get("cloud"));
        Assert.Equal(string.Empty, Replace(store, "cloud", string.Empty).Value);
        Assert.Null(store.Get("cloud"));
        Replace(store, "cloud", "  \n");
        Assert.Null(store.Get("cloud"));
    }

    /// <summary>Append uses a newline only between two parts, and empty text changes nothing.</summary>
    [Fact]
    public void Append_KnownProvider_AddsToEnd()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud")]);

        Assert.Equal(string.Empty, Append(store, "cloud", string.Empty).Value);
        Assert.Equal("One.", Append(store, "cloud", "One.").Value);
        Assert.Equal("One.\nTwo.", Append(store, "cloud", "Two.").Value);
        Assert.Equal("One.\nTwo.", Append(store, "cloud", string.Empty).Value);
    }

    /// <summary>Replace accepts a prompt at the limit, and rejects a longer prompt without a change.</summary>
    [Fact]
    public void Replace_OverLimit_FailsAndKeepsPrompt()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud") { MaxSystemPromptLength = 5 }]);

        Assert.Equal("12345", Replace(store, "cloud", "12345").Value);
        var result = Replace(store, "cloud", "123456");

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Contains("5 characters", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("12345", store.Get("cloud"));
    }

    /// <summary>The limit counts the separator, and a failed append leaves the prompt unchanged.</summary>
    [Fact]
    public void Append_OverLimit_FailsAndKeepsPrompt()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud") { MaxSystemPromptLength = 5 }]);
        Replace(store, "cloud", "12");

        Assert.Equal("12\n45", Append(store, "cloud", "45").Value);
        Assert.Equal(ErrorKind.InvalidRequest, Append(store, "cloud", "x").Error!.Kind);
        Assert.Equal("12\n45", store.Get("cloud"));
    }

    /// <summary>Appends at the same time cannot pass the limit together.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Append_AtSameTime_KeepsLimit()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud") { MaxSystemPromptLength = 100 }]);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Append(store, "cloud", "x"), TestContext.Current.CancellationToken)));

        Assert.Equal(99, store.Get("cloud")!.Length);
        Assert.Equal(50, results.Count(result => result.IsSuccess));
    }

    /// <summary>Provider names ignore case, like configuration keys.</summary>
    [Fact]
    public void Replace_NameInOtherCase_ChangesSamePrompt()
    {
        var store = new SystemPromptStore([new FakeChatProvider("OpenAI")]);

        Replace(store, "openai", "Be brief.");

        Assert.Equal("Be brief.", store.Get("OPENAI"));
    }

    /// <summary>A change to an unknown provider fails as not found, and does not add the provider.</summary>
    [Fact]
    public void Changes_UnknownProvider_FailAsNotFound()
    {
        var store = new SystemPromptStore(Array.Empty<IChatProvider>());

        Assert.Equal(ErrorKind.NotFound, Replace(store, "nobody", "x").Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, Append(store, "nobody", "x").Error!.Kind);
        Assert.Null(store.Get("nobody"));
    }

    /// <summary>Appends at the same time do not lose text.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Append_AtSameTime_KeepsAllText()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud")]);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() => Append(store, "cloud", $"{index}"), TestContext.Current.CancellationToken)));

        var parts = store.Get("cloud")!.Split('\n');
        Assert.Equal(200, parts.Distinct(StringComparer.Ordinal).Count());
    }
}

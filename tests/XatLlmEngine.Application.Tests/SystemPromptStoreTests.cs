using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the stored system prompts.</summary>
public class SystemPromptStoreTests
{
    /// <summary>The store starts with the configured prompt, and a blank value means no prompt.</summary>
    [Fact]
    public void Get_ConfiguredPrompts_StartValues()
    {
        var store = new SystemPromptStore(
        [
            new FakeChatProvider("set") { SystemPrompt = "Be brief." },
            new FakeChatProvider("blank") { SystemPrompt = string.Empty },
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
        var store = new SystemPromptStore([new FakeChatProvider("cloud") { SystemPrompt = "Old." }]);

        Assert.Equal("New.", store.Replace("cloud", "New.").Value);
        Assert.Equal("New.", store.Get("cloud"));
        Assert.Equal(string.Empty, store.Replace("cloud", string.Empty).Value);
        Assert.Null(store.Get("cloud"));
        store.Replace("cloud", "  \n");
        Assert.Null(store.Get("cloud"));
    }

    /// <summary>Append uses a newline only between two parts, and empty text changes nothing.</summary>
    [Fact]
    public void Append_KnownProvider_AddsToEnd()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud")]);

        Assert.Equal(string.Empty, store.Append("cloud", string.Empty).Value);
        Assert.Equal("One.", store.Append("cloud", "One.").Value);
        Assert.Equal("One.\nTwo.", store.Append("cloud", "Two.").Value);
        Assert.Equal("One.\nTwo.", store.Append("cloud", string.Empty).Value);
    }

    /// <summary>Provider names ignore case, like configuration keys.</summary>
    [Fact]
    public void Replace_NameInOtherCase_ChangesSamePrompt()
    {
        var store = new SystemPromptStore([new FakeChatProvider("OpenAI")]);

        store.Replace("openai", "Be brief.");

        Assert.Equal("Be brief.", store.Get("OPENAI"));
    }

    /// <summary>A change to an unknown provider fails as not found, and does not add the provider.</summary>
    [Fact]
    public void Changes_UnknownProvider_FailAsNotFound()
    {
        var store = new SystemPromptStore(Array.Empty<IChatProvider>());

        Assert.Equal(ErrorKind.NotFound, store.Replace("nobody", "x").Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, store.Append("nobody", "x").Error!.Kind);
        Assert.Null(store.Get("nobody"));
    }

    /// <summary>Appends at the same time do not lose text.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Append_AtSameTime_KeepsAllText()
    {
        var store = new SystemPromptStore([new FakeChatProvider("cloud")]);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() => store.Append("cloud", $"{index}"), TestContext.Current.CancellationToken)));

        var parts = store.Get("cloud")!.Split('\n');
        Assert.Equal(200, parts.Distinct(StringComparer.Ordinal).Count());
    }
}

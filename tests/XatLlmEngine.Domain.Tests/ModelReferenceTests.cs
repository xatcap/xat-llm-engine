using XatLlmEngine.Domain;

namespace XatLlmEngine.Domain.Tests;

/// <summary>Tests the <c>provider/model</c> naming rule.</summary>
public class ModelReferenceTests
{
    /// <summary>The first slash separates the parts, and later slashes belong to the model name.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="provider">The expected provider.</param>
    /// <param name="model">The expected model.</param>
    [Theory]
    [InlineData("local/ministral-3-3b", "local", "ministral-3-3b")]
    [InlineData("groq/meta/llama-4", "groq", "meta/llama-4")]
    public void Parse_ValidValue_SplitsAtFirstSlash(string value, string provider, string model)
    {
        var result = ModelReference.Parse(value);

        Assert.True(result.IsSuccess);
        Assert.Equal(provider, result.Value!.Provider);
        Assert.Equal(model, result.Value.Model);
        Assert.Equal(value, result.Value.ToString());
    }

    /// <summary>A value without both parts is an invalid request.</summary>
    /// <param name="value">The text to parse.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-provider")]
    [InlineData("/model")]
    [InlineData("provider/")]
    public void Parse_InvalidValue_FailsAsInvalidRequest(string? value)
    {
        var result = ModelReference.Parse(value);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
    }
}

namespace XatLlmEngine.Domain;

/// <summary>Names a model and the provider that serves it, written as <c>provider/model</c>.</summary>
public sealed record ModelReference
{
    /// <summary>Initializes a new instance of the <see cref="ModelReference"/> class.</summary>
    /// <param name="provider">The provider name.</param>
    /// <param name="model">The model name inside that provider.</param>
    private ModelReference(string provider, string model)
    {
        Provider = provider;
        Model = model;
    }

    /// <summary>Gets the provider name, which is the text before the first slash.</summary>
    public string Provider { get; }

    /// <summary>Gets the model name, which is all the text after the first slash. It may contain more slashes.</summary>
    public string Model { get; }

    /// <summary>Parses a model reference. Only the first slash separates the two parts, because model names can contain slashes.</summary>
    /// <param name="value">The text sent by the caller, for example <c>local/ministral-3-3b</c>.</param>
    /// <returns>The parsed reference, or an invalid-request failure when either part is missing.</returns>
    public static Result<ModelReference> Parse(string? value)
    {
        var separator = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (value is null || separator <= 0 || separator == value.Length - 1)
        {
            return Result<ModelReference>.Fail(new EngineError(
                ErrorKind.InvalidRequest,
                "Write the model as 'provider/model', for example 'local/ministral-3-3b'."));
        }

        return Result<ModelReference>.Ok(new ModelReference(value[..separator], value[(separator + 1)..]));
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Provider}/{Model}";
}

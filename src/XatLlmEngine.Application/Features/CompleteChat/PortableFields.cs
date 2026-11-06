using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>
/// The portable fields: names that an OpenAI-format provider accepts when its template lists the field or one of its other
/// names. A caller can then change the provider with only a change of <c>model</c>. The engine renames a portable field to the
/// name in the provider's template. It never changes a value. See ADR 0012.
/// </summary>
internal static class PortableFields
{
    /// <summary>The name of the stream options field.</summary>
    internal const string StreamOptions = "stream_options";

    /// <summary>The only field in <c>stream_options</c> that the engine can apply itself.</summary>
    private const string IncludeUsage = "include_usage";

    /// <summary>The portable fields, in the order of the documentation.</summary>
    internal static readonly ImmutableArray<string> Names =
        ["messages", "temperature", "top_p", "stop", "frequency_penalty", "presence_penalty", "response_format", "max_tokens", "max_completion_tokens", "seed", StreamOptions];

    /// <summary>
    /// The native names of each portable field that has more than one, in order of preference. The first name that the
    /// template lists is the target. The table goes one way only: a native name such as <c>random_seed</c> is not portable.
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> NativeNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["max_tokens"] = ["max_tokens", "max_completion_tokens"],
        ["max_completion_tokens"] = ["max_completion_tokens", "max_tokens"],
        ["seed"] = ["seed", "random_seed"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Checks the fields of a request against the provider's template, and finds the renames.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <param name="body">The caller's body. This method does not change it.</param>
    /// <returns>The renames, or an invalid-request failure.</returns>
    public static Result<FieldMap> Resolve(IChatProvider provider, JsonObject body)
    {
        var renamed = new List<(string Sent, string Native)>();
        var nativeToSent = new Dictionary<string, string>(StringComparer.Ordinal);
        var dropStreamOptions = false;
        foreach (var (name, value) in body)
        {
            if (name is "model" or "stream")
            {
                continue;
            }

            string native;
            if (Lists(provider, name))
            {
                native = name;
            }
            else if (!provider.AcceptsPortableFields)
            {
                return Result<FieldMap>.Fail(NotSupported(provider, name, hint: null));
            }
            else if (Target(provider, name) is { } target)
            {
                native = target;
                renamed.Add((name, target));
            }
            else if (name == StreamOptions)
            {
                if (StreamOptionsProblem(provider, value) is { } problem)
                {
                    return Result<FieldMap>.Fail(problem);
                }

                dropStreamOptions = true;
                continue;
            }
            else
            {
                var hint = Names.Contains(name, StringComparer.Ordinal)
                    ? null
                    : $" The field '{name}' is not portable, so it works only with the providers that list it. Portable fields: {string.Join(", ", Names)}.";
                return Result<FieldMap>.Fail(NotSupported(provider, name, hint));
            }

            if (!nativeToSent.TryAdd(native, name))
            {
                return Result<FieldMap>.Fail(new EngineError(
                    ErrorKind.InvalidRequest,
                    $"The fields '{nativeToSent[native]}' and '{name}' are the same setting for the provider '{provider.Name}'. Send only one of them."));
            }
        }

        return Result<FieldMap>.Ok(new FieldMap(renamed, dropStreamOptions));
    }

    /// <summary>Tells whether the template lists a field.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="name">The field name.</param>
    /// <returns><see langword="true"/> when the field is native to the provider.</returns>
    private static bool Lists(IChatProvider provider, string name) => provider.RequestFields.Contains(name, StringComparer.Ordinal);

    /// <summary>Finds the native name of a portable field that the template does not list.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="name">The field name that the caller sent.</param>
    /// <returns>The native name, or <see langword="null"/> when the field has no other name in the template.</returns>
    private static string? Target(IChatProvider provider, string name) =>
        NativeNames.TryGetValue(name, out var natives) ? natives.FirstOrDefault(native => Lists(provider, native)) : null;

    /// <summary>Checks the <c>stream_options</c> of a template with none. The engine removes the field and applies <c>include_usage</c> itself.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="value">The caller's value.</param>
    /// <returns>The problem, or <see langword="null"/> when the engine can apply the value. Any other content would have no effect, so the engine refuses it.</returns>
    private static EngineError? StreamOptionsProblem(IChatProvider provider, JsonNode? value) =>
        HasOnlyIncludeUsage(value)
            ? null
            : new EngineError(
                ErrorKind.InvalidRequest,
                $"The provider '{provider.Name}' accepts '{StreamOptions}' only with the one field '{IncludeUsage}', with the value true or false.");

    /// <summary>Tells whether a <c>stream_options</c> value holds only <c>include_usage</c> with a true or false value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the engine can apply the value itself.</returns>
    private static bool HasOnlyIncludeUsage(JsonNode? value) =>
        value is JsonObject options
        && options.Count == 1
        && options[IncludeUsage] is JsonValue include
        && include.TryGetValue<bool>(out _);

    /// <summary>Builds the error for a field that the provider does not accept.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="name">The field name.</param>
    /// <param name="hint">More text for a provider that accepts portable fields, or <see langword="null"/>.</param>
    /// <returns>The error.</returns>
    private static EngineError NotSupported(IChatProvider provider, string name, string? hint)
    {
        var supported = provider.AcceptsPortableFields
            ? provider.RequestFields.Concat(Names.Where(portable => Target(provider, portable) is not null || portable == StreamOptions)).Distinct(StringComparer.Ordinal)
            : provider.RequestFields;
        return new EngineError(
            ErrorKind.InvalidRequest,
            $"The provider '{provider.Name}' does not support the field '{name}'. Supported fields: model, stream, {string.Join(", ", supported)}.{hint}");
    }
}

/// <summary>The renames of one request. A request that uses only native names has no renames, so its body does not change.</summary>
/// <param name="Renamed">Each field that the engine renames, with the name that the caller sent and the native name.</param>
/// <param name="DropStreamOptions">Whether the engine removes <c>stream_options</c>, because the template does not list it.</param>
internal sealed record FieldMap(IReadOnlyList<(string Sent, string Native)> Renamed, bool DropStreamOptions)
{
    /// <summary>Applies the renames to the request's own copy of the body. The values move unchanged.</summary>
    /// <param name="body">The copy of the body.</param>
    public void ApplyTo(JsonObject body)
    {
        foreach (var (sent, native) in Renamed)
        {
            var value = body[sent];
            body.Remove(sent);
            body[native] = value;
        }

        if (DropStreamOptions)
        {
            body.Remove(PortableFields.StreamOptions);
        }
    }

    /// <summary>Gives the name that the caller sent for a native field, so that an error names the caller's own field.</summary>
    /// <param name="native">The native name.</param>
    /// <returns>The sent name.</returns>
    public string SentName(string native) =>
        Renamed.FirstOrDefault(entry => entry.Native == native) is { Sent: { } sent } ? sent : native;

    /// <summary>Describes the renames for the trace, with names only, never values.</summary>
    /// <returns>For example <c>seed&gt;random_seed</c>, or <see langword="null"/> when nothing changed.</returns>
    public string? Describe()
    {
        var parts = Renamed.Select(entry => $"{entry.Sent}>{entry.Native}").ToList();
        if (DropStreamOptions)
        {
            parts.Add($"{PortableFields.StreamOptions}>(removed)");
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }
}

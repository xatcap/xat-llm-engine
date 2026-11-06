using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Applies a provider's limit on output tokens. See ADR 0011.</summary>
internal static class TokenLimitPolicy
{
    /// <summary>The request fields that set the output token limit, in order of preference. Each protocol uses one of them.</summary>
    internal static readonly string[] LimitFields = ["max_tokens", "max_completion_tokens"];

    /// <summary>Refuses a request that asks for more tokens than the limit, and adds the limit to a request that has none.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <param name="body">The request's own copy of the body, after the renames of portable fields. This method can change it.</param>
    /// <param name="sentName">Gives the name that the caller sent for a native field, so that an error names the caller's own field.</param>
    /// <returns>The problem, or <see langword="null"/> when the request can go ahead.</returns>
    public static EngineError? Apply(IChatProvider provider, JsonObject body, Func<string, string> sentName)
    {
        var limit = provider.MaxTokensLimit;
        var fields = LimitFields.Where(field => provider.RequestFields.Contains(field, StringComparer.Ordinal)).ToList();
        if (limit <= 0 || fields.Count == 0)
        {
            return null;
        }

        var sent = false;
        foreach (var field in fields.Where(body.ContainsKey))
        {
            sent = true;

            // A value that is not a number could pass the limit, so the engine refuses it.
            if (body[field] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
                || !decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var asked))
            {
                return new EngineError(ErrorKind.InvalidRequest, $"The field '{sentName(field)}' must be a number.");
            }

            // A value below 1 can mean "no limit" to a server, for example -1 to llama-server.
            if (asked < 1 || asked != decimal.Truncate(asked))
            {
                return new EngineError(ErrorKind.InvalidRequest, $"The field '{sentName(field)}' must be a whole number of at least 1.");
            }

            if (asked > limit)
            {
                return new EngineError(ErrorKind.InvalidRequest, $"The provider '{provider.Name}' allows at most {limit} tokens in '{sentName(field)}'.");
            }
        }

        if (!sent)
        {
            body[fields[0]] = limit;
        }

        return null;
    }
}

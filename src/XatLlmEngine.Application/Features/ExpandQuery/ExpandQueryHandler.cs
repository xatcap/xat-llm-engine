using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.ExpandQuery;

/// <summary>Turns one search query into several, with the small query-expansion model.</summary>
/// <remarks>The handler builds the prompt and reads the answer, so callers never write a prompt. It uses <see cref="CompleteChatHandler"/>, so the token limit, the metrics and the error mapping are the same as for chat.</remarks>
/// <param name="providers">The enabled providers.</param>
/// <param name="chat">The use case that asks a provider for an answer.</param>
public sealed partial class ExpandQueryHandler(ProviderRegistry providers, CompleteChatHandler chat)
{
    /// <summary>The number of queries to write when the caller gives no number.</summary>
    public const int DefaultCount = 3;

    /// <summary>The largest number of queries that a caller can ask for.</summary>
    public const int MaxCount = 10;

    /// <summary>The longest query, in characters. The model has a small context.</summary>
    public const int MaxQueryLength = 1000;

    /// <summary>Asks the model for other queries, and reads the answer.</summary>
    /// <param name="command">The caller's request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>The new queries, or the expected failure.</returns>
    public async Task<Result<ExpandedQuery>> HandleAsync(ExpandQueryCommand command, CancellationToken cancellationToken)
    {
        var query = command.Query.Trim();
        if (query.Length == 0 || query.Length > MaxQueryLength)
        {
            return Fail(ErrorKind.InvalidRequest, $"The 'query' field must have from 1 to {MaxQueryLength} characters.");
        }

        if (command.Count is < 1 or > MaxCount)
        {
            return Fail(ErrorKind.InvalidRequest, $"The 'count' field must be a number from 1 to {MaxCount}.");
        }

        if (!providers.TryGet(ProviderNames.QueryExpansion, out var provider) || provider.Models is not [var modelName, ..])
        {
            return Fail(ErrorKind.Unavailable, "The query-expansion model is turned off. Set QueryExpansionModel:Enabled to true, then restart the engine.");
        }

        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = BuildInstruction(command.Count) },
                new JsonObject { ["role"] = "user", ["content"] = query }),
        };
        var answer = await chat.HandleAsync(new CompleteChatCommand($"{provider.Name}/{modelName}", body), cancellationToken).ConfigureAwait(false);
        if (!answer.IsSuccess)
        {
            return Result<ExpandedQuery>.Fail(answer.Error);
        }

        var completion = answer.Value.Completion;
        var queries = ReadQueries(completion.Content, query, command.Count, cutOff: completion.FinishReason == FinishReason.Length);
        return queries.Count == 0
            ? Fail(ErrorKind.Upstream, "The query-expansion model returned no new queries. Try again, or change the query.")
            : Result<ExpandedQuery>.Ok(new ExpandedQuery(queries, answer.Value.Model));
    }

    /// <summary>Builds the instruction for the model.</summary>
    /// <param name="count">The number of queries to write.</param>
    /// <returns>The instruction.</returns>
    private static string BuildInstruction(int count) =>
        $"You write search queries. Write {count} different search queries that have the same meaning as the query of the user, or that cover a related part of it. " +
        "Write one query on each line. Do not number the lines. Do not explain. Write the queries in the language of the user's query.";

    /// <summary>Reads the queries from the answer of the model. The answer should have one query on each line, but small models add numbers, bullets, quotes or a thinking block.</summary>
    /// <param name="text">The text of the answer.</param>
    /// <param name="original">The query of the caller.</param>
    /// <param name="count">The largest number of queries to keep.</param>
    /// <param name="cutOff">Tells whether the model stopped at the token limit. Then the last line can be incomplete, so the handler drops it.</param>
    /// <returns>The new queries in order, with no repeats, and with no copy of the original.</returns>
    internal static List<string> ReadQueries(string text, string original, int count, bool cutOff = false)
    {
        var lines = ThinkingBlock().Replace(text, string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (cutOff && lines.Length > 0)
        {
            lines = lines[..^1];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { original };
        return lines
            .Select(line => ListMarker().Replace(line, string.Empty).Trim().Trim('"', '\'', '`').Trim())

            // A small model often starts with a line such as "Here are 3 queries:". A search query does not end with a colon.
            .Where(query => query.Length > 0 && !query.EndsWith(':'))
            .Where(seen.Add)
            .Take(count)
            .ToList();
    }

    /// <summary>Builds a failed result.</summary>
    /// <param name="kind">The category of the failure.</param>
    /// <param name="message">The message for the caller.</param>
    /// <returns>The failed result.</returns>
    private static Result<ExpandedQuery> Fail(ErrorKind kind, string message) =>
        Result<ExpandedQuery>.Fail(new EngineError(kind, message));

    /// <summary>Matches a thinking block, which some models write before the answer.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThinkingBlock();

    /// <summary>Matches a bullet or a number at the start of a line, such as <c>-</c>, <c>*</c>, <c>1.</c> or <c>2)</c>.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^(\d+[.)]|[-*•])\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListMarker();
}

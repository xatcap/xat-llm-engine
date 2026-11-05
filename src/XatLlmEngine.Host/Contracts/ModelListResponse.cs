namespace XatLlmEngine.Host.Contracts;

/// <summary>The model list in the OpenAI format.</summary>
/// <param name="Object">Always <c>list</c>.</param>
/// <param name="Data">The models.</param>
internal sealed record ModelListResponse(string Object, IReadOnlyList<ModelResponse> Data);

/// <summary>One model.</summary>
/// <param name="Id">The value to send as <c>model</c>, written as <c>provider/model</c>.</param>
/// <param name="Object">Always <c>model</c>.</param>
/// <param name="OwnedBy">The provider's name.</param>
internal sealed record ModelResponse(string Id, string Object, string OwnedBy);

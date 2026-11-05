using System.Diagnostics.CodeAnalysis;

namespace XatLlmEngine.Domain;

/// <summary>The outcome of an operation that can fail in an expected way.</summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Result<T>.Ok and Result<T>.Fail are the factory pattern in docs/dotnet/csharp-best-practices.md.")]
public sealed record Result<T>
{
    /// <summary>Initializes a new instance of the <see cref="Result{T}"/> class.</summary>
    /// <param name="value">The value on success.</param>
    /// <param name="error">The failure, or <see langword="null"/> on success.</param>
    private Result(T? value, EngineError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Gets the value. It is set only when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets the failure. It is set only when <see cref="IsSuccess"/> is <see langword="false"/>.</summary>
    public EngineError? Error { get; }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value produced by the operation.</param>
    /// <returns>A successful result holding <paramref name="value"/>.</returns>
    public static Result<T> Ok(T value) => new(value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The reason for the failure.</param>
    /// <returns>A failed result holding <paramref name="error"/>.</returns>
    public static Result<T> Fail(EngineError error) => new(default, error);
}

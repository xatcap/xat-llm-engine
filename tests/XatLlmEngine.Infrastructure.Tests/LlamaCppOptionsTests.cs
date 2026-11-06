using System.ComponentModel.DataAnnotations;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for the local model settings.</summary>
public class LlamaCppOptionsTests
{
    /// <summary>Every allowed flag passes the check, with a value only when it takes one.</summary>
    [Fact]
    public void Validate_EveryAllowedFlag_Passes()
    {
        foreach (var (flag, takesValue) in LlamaCppOptions.AllowedFlags)
        {
            // The context size has a larger minimum than the other numbers.
            string[] arguments = takesValue ? [flag, flag is "-c" or "--ctx-size" ? "512" : "8"] : [flag];
            Assert.Empty(Validate(Options(arguments)));
        }
    }

    /// <summary>A flag that is not in the allowed list stops the engine at startup. This includes a flag from a later llama.cpp version and another spelling of a flag.</summary>
    /// <param name="flag">The flag that is not allowed.</param>
    [Theory]
    [InlineData("--log-file")]
    [InlineData("--log_file")]
    [InlineData("--threads=4")]
    [InlineData("--webui")]
    [InlineData("--ui")]
    [InlineData("--host")]
    [InlineData("-hf")]
    [InlineData("--models-preset")]
    [InlineData("--tools")]
    [InlineData("-v")]
    [InlineData("--flag-from-a-later-version")]
    public void Validate_FlagNotAllowed_Fails(string flag)
    {
        var result = Assert.Single(Validate(Options("--threads", "4", flag, "value")));

        Assert.Contains(flag, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal([nameof(LlamaCppOptions.ExtraArguments)], result.MemberNames);
    }

    /// <summary>An empty entry, or an entry with spaces at the ends, stops the engine at startup, because llama-server does not accept it.</summary>
    /// <param name="argument">The entry.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" --log-file")]
    [InlineData("--threads ")]
    public void Validate_EmptyOrSpaces_Fails(string argument)
    {
        Assert.Single(Validate(Options("--threads", "4", argument)));
    }

    /// <summary>A context size or a number of parallel slots in the extra arguments must be in the same range as the setting.</summary>
    /// <param name="flag">The flag.</param>
    /// <param name="value">The value after the flag, or <see langword="null"/> for no value.</param>
    [Theory]
    [InlineData("-c", "0")]
    [InlineData("--ctx-size", "100")]
    [InlineData("--ctx_size", "2000000")]
    [InlineData("-c", "auto")]
    [InlineData("-c", null)]
    [InlineData("-np", "0")]
    [InlineData("--parallel", "-1")]
    [InlineData("--parallel", "300")]
    public void Validate_ValueOutOfRange_Fails(string flag, string? value)
    {
        string[] arguments = value is null ? [flag] : [flag, value];
        Assert.Single(Validate(Options(arguments)));
    }

    /// <summary>A value with no flag, a flag with no value, or a value that starts with '-' and is not a number stops the engine at startup.</summary>
    /// <param name="arguments">The extra arguments, separated by '|'.</param>
    [Theory]
    [InlineData("4")]
    [InlineData("--threads|4|8")]
    [InlineData("--threads")]
    [InlineData("--jinja|--threads")]
    [InlineData("--threads|--log-file")]
    [InlineData("--device|-c")]
    public void Validate_ValueInWrongPlace_Fails(string arguments)
    {
        Assert.Single(Validate(Options(arguments.Split('|'))));
    }

    /// <summary>Values pass, also negative numbers and values that hold other characters.</summary>
    [Fact]
    public void Validate_Values_Pass()
    {
        Assert.Empty(Validate(Options("--threads", "4", "--n-predict", "-1", "--cache-type-k", "q8_0", "-ot", "exps=CPU", "-c", "4096", "-np", "2", "-ngl", "auto", "--jinja", "--threads-batch", "2")));
    }

    /// <summary>Builds valid settings with the given extra arguments.</summary>
    /// <param name="extraArguments">The extra arguments.</param>
    /// <returns>The settings.</returns>
    private static LlamaCppOptions Options(params string[] extraArguments) =>
        new() { RequestFields = ["messages"], ExtraArguments = extraArguments };

    /// <summary>Runs the same validation that the engine runs at startup.</summary>
    /// <param name="options">The settings.</param>
    /// <returns>The validation failures.</returns>
    private static List<ValidationResult> Validate(LlamaCppOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

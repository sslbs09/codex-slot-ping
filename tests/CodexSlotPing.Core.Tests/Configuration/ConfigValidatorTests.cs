using CodexSlotPing.Core.Configuration;

namespace CodexSlotPing.Core.Tests.Configuration;

public sealed class ConfigValidatorTests
{
    private static readonly IReadOnlySet<string> SupportedEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
        "ultra"
    };

    [Fact]
    public void Defaults_PreserveLegacySlotDiagnostics()
    {
        var config = AppConfigDefaults.Create();

        Assert.Equal(2, config.SchemaVersion);
        Assert.True(config.Enabled);
        Assert.Equal(new[] { "07:00", "12:01", "17:01", "22:01" }, config.LegacyFixedSlots);
        Assert.Equal("SYSTEM", config.TimeZone);
        Assert.Equal("gpt-5.6-luna", config.Model);
        Assert.Equal("low", config.ReasoningEffort);
        Assert.Equal("Hi.", config.Prompt);
        Assert.False(config.WakeToRun);
        Assert.Equal(3, config.StrictSlotWindowMinutes);
        Assert.False(config.CaptureRawLogs);
    }

    [Fact]
    public void ValidDefaultConfiguration_IsAcceptedAndPromptIsTrimmed()
    {
        var candidate = AppConfigDefaults.Create() with { Prompt = "  Hi.  " };

        var result = ConfigValidator.Validate(candidate, "Taipei Standard Time", SupportedEfforts);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.NormalizedConfig);
        Assert.Equal("Hi.", result.NormalizedConfig.Prompt);
        Assert.Equal("gpt-5.6-luna", result.NormalizedConfig.Model);
    }

    [Fact]
    public void ExactlyFourUniqueValidTimes_AreRequired()
    {
        var tooFew = With(slots: ["07:00", "12:00", "18:00"]);
        var tooMany = With(slots: ["01:00", "07:00", "12:00", "18:00", "23:00"]);
        var duplicate = With(slots: ["07:00", "12:00", "12:00", "18:00"]);
        var invalidTime = With(slots: ["00:00", "06:00", "12:00", "24:00"]);
        var nonPaddedTime = With(slots: ["7:00", "12:00", "18:00", "23:00"]);

        AssertInvalid(tooFew);
        AssertInvalid(tooMany);
        AssertInvalid(duplicate);
        AssertInvalid(invalidTime);
        AssertInvalid(nonPaddedTime);
    }

    [Fact]
    public void ValidTimes_AreCheckedChronologicallyAndAcrossMidnight()
    {
        var unorderedButValid = With(slots: ["18:00", "00:00", "12:00", "06:00"]);
        var exactSixMinuteMidnightGap = With(slots: ["00:00", "06:00", "12:00", "23:54"]);
        var gapWithinDay = With(slots: ["07:00", "07:05", "12:00", "18:00"]);
        var gapAcrossMidnight = With(slots: ["00:00", "06:00", "12:00", "23:56"]);

        Assert.True(Validate(unorderedButValid).IsValid);
        Assert.True(Validate(exactSixMinuteMidnightGap).IsValid);
        AssertInvalid(gapWithinDay);
        AssertInvalid(gapAcrossMidnight);
    }

    [Fact]
    public void AnyNonemptyModelString_IsKeptAsASelectionRequest()
    {
        var candidate = With(model: "account-specific-model-alias");

        var result = Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal("account-specific-model-alias", result.NormalizedConfig!.Model);
    }

    [Fact]
    public void NullModelString_IsRejected()
    {
        var candidate = AppConfigDefaults.Create() with { Model = null! };

        AssertInvalid(candidate);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void EmptyModelString_IsRejected(string? model)
    {
        AssertInvalid(With(model: model));
    }

    [Fact]
    public void ReasoningEffort_MustBeInSuppliedCompatibilitySet()
    {
        Assert.True(Validate(With(reasoningEffort: "high"), supportedEfforts: new HashSet<string>(StringComparer.Ordinal) { "low", "high" }).IsValid);
        AssertInvalid(With(reasoningEffort: "medium"), supportedEfforts: new HashSet<string>(StringComparer.Ordinal) { "low", "high" });
        AssertInvalid(With(reasoningEffort: "LOW"), supportedEfforts: new HashSet<string>(StringComparer.Ordinal) { "low", "high" });
    }

    [Fact]
    public void PromptMustContainAtMostTwoHundredUnicodeScalars()
    {
        var exactlyTwoHundredAstralScalars = string.Concat(Enumerable.Repeat("😀", 200));
        var twoHundredOneAstralScalars = string.Concat(Enumerable.Repeat("😀", 201));

        var accepted = Validate(With(prompt: exactlyTwoHundredAstralScalars));
        var rejected = Validate(With(prompt: twoHundredOneAstralScalars));

        Assert.True(accepted.IsValid);
        Assert.Equal(400, accepted.NormalizedConfig!.Prompt.Length);
        AssertInvalid(With(prompt: twoHundredOneAstralScalars));
        AssertInvalid(With(prompt: " \t\r\n "));
    }

    [Fact]
    public void PromptAllowsInternalNewlines()
    {
        var result = Validate(With(prompt: "line one\nline two\r\nline three"));

        Assert.True(result.IsValid);
        Assert.Equal("line one\nline two\r\nline three", result.NormalizedConfig!.Prompt);
    }

    [Fact]
    public void StrictSlotWindow_IsFixedAtThreeMinutes()
    {
        Assert.True(Validate(With(strictSlotWindowMinutes: 3)).IsValid);
        AssertInvalid(With(strictSlotWindowMinutes: 2));
        AssertInvalid(With(strictSlotWindowMinutes: 4));
    }

    [Fact]
    public void ExplicitTimezoneLock_MustMatchCurrentWindowsTimezoneId()
    {
        Assert.True(Validate(With(timeZone: "Taipei Standard Time"), "Taipei Standard Time").IsValid);
        Assert.True(Validate(With(timeZone: "taipei standard time"), "Taipei Standard Time").IsValid);
        AssertInvalid(With(timeZone: "Pacific Standard Time"), "Taipei Standard Time");
        Assert.True(Validate(With(timeZone: "SYSTEM"), "Taipei Standard Time").IsValid);
    }

    private static AppConfig With(
        IReadOnlyList<string>? slots = null,
        string? timeZone = null,
        string? model = null,
        string? reasoningEffort = null,
        string? prompt = null,
        int? strictSlotWindowMinutes = null)
    {
        var defaults = AppConfigDefaults.Create();
        return defaults with
        {
            LegacyFixedSlots = slots ?? defaults.LegacyFixedSlots,
            TimeZone = timeZone ?? defaults.TimeZone,
            Model = model ?? defaults.Model,
            ReasoningEffort = reasoningEffort ?? defaults.ReasoningEffort,
            Prompt = prompt ?? defaults.Prompt,
            StrictSlotWindowMinutes = strictSlotWindowMinutes ?? defaults.StrictSlotWindowMinutes
        };
    }

    private static ConfigValidationResult Validate(AppConfig candidate, string currentTimeZone = "Taipei Standard Time", IReadOnlySet<string>? supportedEfforts = null)
    {
        return ConfigValidator.Validate(candidate, currentTimeZone, supportedEfforts ?? SupportedEfforts);
    }

    private static void AssertInvalid(AppConfig candidate, string currentTimeZone = "Taipei Standard Time", IReadOnlySet<string>? supportedEfforts = null)
    {
        var result = Validate(candidate, currentTimeZone, supportedEfforts);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.NormalizedConfig);
    }
}

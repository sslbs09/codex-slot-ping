using System.Globalization;
using CodexSlotPing.App;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class UiTextTests
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    public void ChineseUiCultureUsesSimplifiedChinese(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal("zh-CN", UiText.ResolveCulture(culture).Name);
        Assert.Equal("模型", UiText.Get("Main.Model", culture));
    }

    [Fact]
    public void OtherUiCulturesFallBackToEnglish()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal("en-US", UiText.ResolveCulture(culture).Name);
        Assert.Equal("Model", UiText.Get("Main.Model", culture));
        Assert.Equal("○ Pending", UiText.FormatSlotStatus(FormalSlotStatus.PENDING, culture));
        Assert.Equal("○ 待执行", UiText.FormatSlotStatus(FormalSlotStatus.PENDING, Chinese));
    }

    [Fact]
    public void CanonicalSettingValuesAreOnlyLocalizedForDisplay()
    {
        const string canonicalValue = "low";

        var display = UiText.FormatReasoningEffort(canonicalValue, Chinese);

        Assert.Equal("low", canonicalValue);
        Assert.Equal("低", display);

        const string timezoneValue = "SYSTEM";
        Assert.Equal("跟随系统", UiText.FormatTimeZone(timezoneValue, Chinese));
        Assert.Equal("SYSTEM", timezoneValue);
        Assert.Equal("Pacific Standard Time", UiText.FormatTimeZone("Pacific Standard Time", Chinese));
    }

    [Fact]
    public void SchedulerStateUsesLocalizedUiProjection()
    {
        Assert.Equal("未启用", UiText.FormatSchedulerState(SchedulerState.OFF, Chinese));
        Assert.Equal("OFF", UiText.FormatSchedulerState(SchedulerState.OFF, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(
            "Windows 时区已变化。保存前请确认更新时区设置和本应用拥有的计划任务。",
            UiText.FormatDiagnostic("timezone-confirmation-required", Chinese));
    }

    [Fact]
    public void TestNowConfirmationUsesConsistentChineseText()
    {
        Assert.Equal("确认立即测试", UiText.Get("Main.TestNowConfirmTitle", Chinese));
        Assert.Equal(
            "这会将当前配置的提示词作为真实请求发送给 Codex，并可能计入使用额度。是否继续？",
            UiText.Get("Main.TestNowConfirmBody", Chinese));
    }

    [Fact]
    public void EligibilityDecisionProjection_LocalizesCoreReasonAndConfidence()
    {
        var decision = new EligibilityDecision(
            EligibilityDecision.CurrentSchemaVersion,
            EligibilityIntent.Scheduled,
            EligibilityStatus.Eligible,
            EligibilityReason.Ready,
            new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero),
            Confidence.Medium,
            ObservationSource.Unknown,
            false,
            EligibilityReason.ExternalManualUsageUnknown);

        var display = EligibilityDecisionProjection.Create(decision, Chinese);
        var details = EligibilityDecisionProjection.FormatDetails(decision, Chinese);
        var confirmationDecision = new EligibilityDecision(
            EligibilityDecision.CurrentSchemaVersion,
            EligibilityIntent.TestNow,
            EligibilityStatus.ConfirmationRequired,
            EligibilityReason.TestNowConfirmationRequired,
            null,
            Confidence.Unknown,
            ObservationSource.Unknown,
            true,
            EligibilityReason.ExternalManualUsageUnknown);
        var confirmation = EligibilityDecisionProjection.FormatTestNowConfirmation(confirmationDecision, Chinese);

        Assert.Equal("本地策略允许尝试", display.Status);
        Assert.Equal("本地策略允许进行尝试", display.Reason);
        Assert.Equal("中", display.Confidence);
        Assert.NotNull(display.NextEligibleAt);
        Assert.Contains("下次计划尝试", details);
        Assert.Contains("依据本地调度策略计算", details);
        Assert.Contains("无法观察此应用之外的 Codex 使用情况", details);
        Assert.DoesNotContain("重置", details);
        Assert.DoesNotContain(nameof(EligibilityReason.ExternalManualUsageUnknown), details);
        Assert.Equal("本地尝试计划", UiText.Get("Main.EligibilityTitle", Chinese));
        var englishDetails = EligibilityDecisionProjection.FormatDetails(
            decision,
            CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains("Next planned attempt:", englishDetails);
        Assert.Contains("Based on local scheduling policy", englishDetails);
        Assert.DoesNotContain("quota reset", englishDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("状态：需要确认", confirmation);
        Assert.Contains("原因：需要确认一次真实的立即测试请求", confirmation);
        Assert.Contains("可信度：未知", confirmation);
        Assert.Contains("下次计划尝试：—", confirmation);
        Assert.Contains("说明：依据本地调度策略计算", confirmation);
    }

    [Fact]
    public void TrayMenuLabelsResolveFromLocalizationResources()
    {
        var currentCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = Chinese;
            Assert.Equal(["打开", "立即测试", "计划任务", "唤醒电脑", "打开日志", "退出"], TrayIconController.MenuEntryNames);
        }
        finally
        {
            CultureInfo.CurrentUICulture = currentCulture;
        }

        Assert.Equal("计划任务：未启用", UiText.FormatTraySchedulerState(SchedulerState.OFF, Chinese));
    }
}

using System.Globalization;
using CodexSlotPing.App;
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

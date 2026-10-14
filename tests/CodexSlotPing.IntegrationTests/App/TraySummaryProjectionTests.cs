using System.Globalization;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class TraySummaryProjectionTests
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    [Fact]
    public void Ready_UsesEligibilityAndPlannedWakeSummary()
    {
        var view = TraySummaryProjection.Create(CreateState(), Chinese);

        Assert.Equal(TraySummaryStatus.Ready, view.Status);
        Assert.Equal("当前条件满足 · 等待计划唤醒", view.StatusSummary);
        Assert.Equal(view.StatusSummary, view.Menu.StatusSummary);
        Assert.Contains("当前条件满足", view.TooltipText);
        Assert.DoesNotContain("正在运行", view.TooltipText);
    }

    [Fact]
    public void Waiting_UsesExistingPlannedAttemptTime()
    {
        var state = CreateState(eligibilityStatus: EligibilityViewStatus.NotYetEligible);
        var expectedTime = state.Schedule.NextAttemptAtUtc!.Value.ToLocalTime().ToString("HH:mm", Chinese);

        var view = TraySummaryProjection.Create(state, Chinese);

        Assert.Equal(TraySummaryStatus.Waiting, view.Status);
        Assert.Equal($"等待下一次计划检查 · {expectedTime}", view.StatusSummary);
    }

    [Fact]
    public void Blocked_UsesProjectedReasonText()
    {
        var view = TraySummaryProjection.Create(
            CreateState(eligibilityStatus: EligibilityViewStatus.Blocked, reason: "使用限制"), Chinese);

        Assert.Equal(TraySummaryStatus.Blocked, view.Status);
        Assert.Equal("自动调度被阻止 · 使用限制", view.StatusSummary);
    }

    [Fact]
    public void Unknown_UsesProjectedUnknownStatus()
    {
        var view = TraySummaryProjection.Create(
            CreateState(eligibilityStatus: EligibilityViewStatus.Unknown), Chinese);

        Assert.Equal(TraySummaryStatus.Unknown, view.Status);
        Assert.Equal("当前无法确认条件", view.StatusSummary);
    }

    [Fact]
    public void Paused_IsIndependentFromDisabledConfiguration()
    {
        var view = TraySummaryProjection.Create(
            CreateState(enabled: true, paused: true), Chinese);

        Assert.Equal(TraySummaryStatus.Paused, view.Status);
        Assert.Equal("自动调度已暂停", view.StatusSummary);
    }

    [Fact]
    public void Disabled_IsShownSeparatelyFromPaused()
    {
        var view = TraySummaryProjection.Create(
            CreateState(enabled: false), Chinese);

        Assert.Equal(TraySummaryStatus.Disabled, view.Status);
        Assert.Equal("自动调度未启用", view.StatusSummary);
    }

    [Fact]
    public void DisabledAutomationWithUninstalledScheduler_IsNotReportedAsError()
    {
        var view = TraySummaryProjection.Create(
            CreateState(enabled: false, schedulerState: SchedulerViewState.NotInstalled, schedulerIsEnabled: false),
            Chinese);

        Assert.Equal(TraySummaryStatus.Disabled, view.Status);
        Assert.Equal("自动调度未启用", view.StatusSummary);
    }

    [Fact]
    public void SchedulerError_HasPriorityOverPauseAndEligibility()
    {
        var view = TraySummaryProjection.Create(
            CreateState(
                eligibilityStatus: EligibilityViewStatus.Blocked,
                schedulerState: SchedulerViewState.Error,
                paused: true), Chinese);

        Assert.Equal(TraySummaryStatus.SchedulerError, view.Status);
        Assert.Equal("调度器异常", view.StatusSummary);
    }

    [Fact]
    public void EnabledAutomationWithDisabledScheduler_IsReportedAsSchedulerError()
    {
        var view = TraySummaryProjection.Create(
            CreateState(schedulerState: SchedulerViewState.Disabled, schedulerIsEnabled: false), Chinese);

        Assert.Equal(TraySummaryStatus.SchedulerError, view.Status);
    }

    [Fact]
    public void MenuStateContainsProjectedSchedulerAndWakeFacts()
    {
        var state = CreateState(effectiveWakeToRun: false);

        var view = TraySummaryProjection.Create(state, Chinese);

        Assert.Equal("计划任务：已启用", view.Menu.SchedulerText);
        Assert.False(view.Menu.WakeToRunChecked);
    }

    [Fact]
    public void SameAppScreenStateProducesStableTrayOutput()
    {
        var state = CreateState(eligibilityStatus: EligibilityViewStatus.NotYetEligible);

        var first = TraySummaryProjection.Create(state, Chinese);
        var second = TraySummaryProjection.Create(state, Chinese);

        Assert.Equal(first, second);
        Assert.InRange(first.TooltipText.Length, 1, 63);
    }

    private static AppScreenState CreateState(
        EligibilityViewStatus eligibilityStatus = EligibilityViewStatus.Eligible,
        string reason = "本地策略允许进行尝试",
        bool enabled = true,
        bool paused = false,
        SchedulerViewState schedulerState = SchedulerViewState.Enabled,
        bool schedulerIsEnabled = true,
        bool effectiveWakeToRun = true)
    {
        var plannedAttempt = new DateTimeOffset(2026, 10, 14, 6, 30, 0, TimeSpan.Zero);
        var eligibility = new EligibilityView(
            eligibilityStatus,
            "状态文本",
            reason,
            "中",
            null,
            null,
            null,
            false,
            eligibilityStatus == EligibilityViewStatus.Eligible,
            "详情",
            "确认文本")
        {
            UserFacingStatus = eligibilityStatus switch
            {
                EligibilityViewStatus.Eligible => "当前条件满足，等待计划唤醒",
                EligibilityViewStatus.NotYetEligible => "等待下一次计划检查",
                EligibilityViewStatus.Blocked => $"自动调度被阻止：{reason}",
                EligibilityViewStatus.Unknown => "当前无法确认条件",
                EligibilityViewStatus.ConfirmationRequired => "需要确认",
                _ => "当前无法确认条件"
            },
            NextPlannedAttemptText = plannedAttempt.ToLocalTime().ToString("g", Chinese)
        };

        return new AppScreenState(
            new AutomationView(
                enabled,
                [],
                "SYSTEM",
                "gpt-5.6",
                "medium",
                "prompt",
                true,
                10,
                false)
            {
                IsPaused = paused
            },
            eligibility,
            new ScheduleView(plannedAttempt),
            new SchedulerView(
                schedulerState,
                true,
                true,
                schedulerIsEnabled,
                effectiveWakeToRun,
                null,
                []),
            new CodexEnvironmentView(true, null, null, null, [], []),
            null,
            [],
            null,
            new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
            "Taipei Standard Time",
            "logs");
    }
}

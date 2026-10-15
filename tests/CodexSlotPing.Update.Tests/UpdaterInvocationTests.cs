using System.Diagnostics;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdaterInvocationTests
{
    [Fact]
    public void ArgumentList_RoundTripsAbsolutePathsWithSpacesAndAppIdentity()
    {
        var invocation = new UpdaterInvocation(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            SemVersion.Parse("0.3.1"),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codex Slot Ping", "install")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codex Slot Ping", "staging")),
            new ProcessIdentity(4123, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero)));
        var startInfo = new ProcessStartInfo();

        invocation.AppendArgumentsTo(startInfo);
        var parsed = UpdaterInvocationParser.TryParse(startInfo.ArgumentList.ToArray(), out var roundTrip);

        Assert.True(parsed);
        Assert.Equal(invocation.OperationId, roundTrip!.OperationId);
        Assert.Equal(invocation.TargetVersion.ToString(), roundTrip.TargetVersion.ToString());
        Assert.Equal(invocation.InstallRoot, roundTrip.InstallRoot);
        Assert.Equal(invocation.VerifiedExtractRoot, roundTrip.VerifiedExtractRoot);
        Assert.Equal(invocation.AppIdentity, roundTrip.AppIdentity);
    }

    [Fact]
    public void Parser_RejectsDuplicateUnknownAndMissingArguments()
    {
        var valid = new UpdaterInvocation(
            Guid.NewGuid(), SemVersion.Parse("0.3.1"),
            Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Path.GetTempPath()),
            new ProcessIdentity(1, DateTimeOffset.UnixEpoch));
        var startInfo = new ProcessStartInfo();
        valid.AppendArgumentsTo(startInfo);

        Assert.False(UpdaterInvocationParser.TryParse(startInfo.ArgumentList.Append("--app-pid").Append("9").ToArray(), out _));
        Assert.False(UpdaterInvocationParser.TryParse(startInfo.ArgumentList.Append("--unexpected").Append("x").ToArray(), out _));
        Assert.False(UpdaterInvocationParser.TryParse(startInfo.ArgumentList.Take(startInfo.ArgumentList.Count - 2).ToArray(), out _));
    }
}

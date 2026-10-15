using CodexSlotPing.Update;
using CodexSlotPing.Runner;

namespace CodexSlotPing.IntegrationTests.Runner;

public sealed class RunnerUpdateGateTests
{
    [Fact]
    public void ExistingRunnerDrainsWhileNewRunnerStopsOnIntent()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths) = CreatePaths(temp.Path, "install-a");
        var store = new UpdateIntentStore(paths);
        var runnerA = RunnerUpdateGate.TryEnter(paths, store);

        Assert.True(runnerA.IsAdmitted);
        var intent = CreateIntent(identity, Guid.NewGuid());
        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(intent));
        Assert.Equal(InstallLeaseAcquireStatus.Busy, ExclusiveInstallLease.TryAcquire(paths, out var whileRunnerRuns));
        Assert.Null(whileRunnerRuns);

        using var runnerB = RunnerUpdateGate.TryEnter(paths, store);
        Assert.False(runnerB.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.IntentPresentOrInvalid, runnerB.BlockReason);
        Assert.True(runnerA.IsAdmitted);

        runnerA.Dispose();
        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(paths, out var afterDrain));
        afterDrain!.Dispose();
    }

    [Fact]
    public async Task IntentPublishedBetweenChecks_BlocksRunnerAndReleasesItsSharedLease()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths) = CreatePaths(temp.Path, "install-a");
        var store = new UpdateIntentStore(paths);
        using var firstCheckReached = new ManualResetEventSlim(false);
        using var continueAfterFirstCheck = new ManualResetEventSlim(false);
        var controlledReader = new FirstReadBarrierReader(paths, store, firstCheckReached, continueAfterFirstCheck);
        var gateTask = Task.Run(() => RunnerUpdateGate.TryEnter(paths, controlledReader));

        try
        {
            Assert.True(firstCheckReached.Wait(TimeSpan.FromSeconds(10)), "Runner did not reach its first intent check.");
            Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(CreateIntent(identity, Guid.NewGuid())));
        }
        finally
        {
            continueAfterFirstCheck.Set();
        }

        using var admission = await gateTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(admission.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.IntentPresentOrInvalid, admission.BlockReason);
        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(paths, out var exclusive));
        exclusive!.Dispose();
    }

    [Fact]
    public void RunnerAlreadyPastSecondCheck_IsAllowedToFinishBeforeExclusiveLease()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths) = CreatePaths(temp.Path, "install-a");
        var store = new UpdateIntentStore(paths);
        var runner = RunnerUpdateGate.TryEnter(paths, store);

        Assert.True(runner.IsAdmitted);
        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(CreateIntent(identity, Guid.NewGuid())));
        Assert.Equal(InstallLeaseAcquireStatus.Busy, ExclusiveInstallLease.TryAcquire(paths, out var whileRunnerRuns));
        Assert.Null(whileRunnerRuns);
        Assert.True(runner.IsAdmitted);

        runner.Dispose();
        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(paths, out var afterRunnerExit));
        afterRunnerExit!.Dispose();
    }

    [Fact]
    public void IntentInOnePortableInstall_DoesNotBlockAnotherInstall()
    {
        using var temp = new TemporaryDirectory();
        var (identityA, pathsA) = CreatePaths(temp.Path, "install-a");
        var (_, pathsB) = CreatePaths(temp.Path, "install-b");
        Assert.Equal(UpdateIntentCreateStatus.Created,
            new UpdateIntentStore(pathsA).Create(CreateIntent(identityA, Guid.NewGuid())));

        using var runnerB = RunnerUpdateGate.TryEnter(pathsB, new UpdateIntentStore(pathsB));

        Assert.True(runnerB.IsAdmitted);
    }

    [Fact]
    public void InvalidOrUnreadableIntent_FailsClosedBeforeAdmission()
    {
        using var temp = new TemporaryDirectory();
        var (_, paths) = CreatePaths(temp.Path, "install-a");

        using var malformed = RunnerUpdateGate.TryEnter(paths, new FixedIntentReader(UpdateIntentReadResult.Invalid("bad-schema")));
        using var unreadable = RunnerUpdateGate.TryEnter(paths, new ThrowingIntentReader());

        Assert.False(malformed.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.IntentPresentOrInvalid, malformed.BlockReason);
        Assert.False(unreadable.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.InfrastructureFailure, unreadable.BlockReason);
        Assert.False(File.Exists(paths.LeaseFilePath));
    }

    [Fact]
    public void SecondIntentReadFailure_FailsClosedAndReleasesSharedLease()
    {
        using var temp = new TemporaryDirectory();
        var (_, paths) = CreatePaths(temp.Path, "install-a");

        using var admission = RunnerUpdateGate.TryEnter(paths, new ThrowOnSecondReadReader());

        Assert.False(admission.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.InfrastructureFailure, admission.BlockReason);
        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(paths, out var exclusive));
        exclusive!.Dispose();
    }

    [Fact]
    public void LeasePathInfrastructureFailure_FailsClosed()
    {
        using var temp = new TemporaryDirectory();
        var (_, paths) = CreatePaths(temp.Path, "install-a");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DirectoryPath)!);
        File.WriteAllText(paths.DirectoryPath, "directory path is occupied by a file");

        using var admission = RunnerUpdateGate.TryEnter(paths, new FixedIntentReader(UpdateIntentReadResult.Missing()));

        Assert.False(admission.IsAdmitted);
        Assert.Equal(RunnerUpdateGateBlockReason.InfrastructureFailure, admission.BlockReason);
    }

    private static (InstallIdentity Identity, UpdateControlPaths Paths) CreatePaths(string root, string installName)
    {
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(root, installName));
        return (identity, UpdateControlPaths.ForInstall(Path.Combine(root, "local-app-data"), identity));
    }

    private static UpdateIntent CreateIntent(InstallIdentity identity, Guid operationId) => new(
        1,
        "CodexSlotPing",
        identity.Key,
        operationId,
        Environment.ProcessId,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class FirstReadBarrierReader(
        UpdateControlPaths paths,
        IUpdateIntentReader inner,
        ManualResetEventSlim firstReadReached,
        ManualResetEventSlim continueAfterFirstRead) : IUpdateIntentReader
    {
        private int _reads;

        public UpdateIntentReadResult Read()
        {
            var result = inner.Read();
            var readNumber = Interlocked.Increment(ref _reads);
            if (readNumber == 1)
            {
                firstReadReached.Set();
                if (!continueAfterFirstRead.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The gate test did not release the first intent check.");
                }
            }
            else if (readNumber == 2)
            {
                var status = ExclusiveInstallLease.TryAcquire(paths, out var exclusive);
                exclusive?.Dispose();
                Assert.Equal(InstallLeaseAcquireStatus.Busy, status);
            }

            return result;
        }
    }

    private sealed class FixedIntentReader(UpdateIntentReadResult result) : IUpdateIntentReader
    {
        public UpdateIntentReadResult Read() => result;
    }

    private sealed class ThrowingIntentReader : IUpdateIntentReader
    {
        public UpdateIntentReadResult Read() => throw new IOException("synthetic update-control read failure");
    }

    private sealed class ThrowOnSecondReadReader : IUpdateIntentReader
    {
        private int _reads;

        public UpdateIntentReadResult Read() => Interlocked.Increment(ref _reads) == 1
            ? UpdateIntentReadResult.Missing()
            : throw new IOException("synthetic second update-control read failure");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "CodexSlotPing.IntegrationTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

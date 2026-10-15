using System.Globalization;
using System.Text.Json;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateIntentStoreTests
{
    [Fact]
    public void Create_AtomicallyPersistsIntentThatReadValidatesForItsInstall()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, store) = CreateStore(temp.Path);
        var intent = CreateIntent(identity, Guid.NewGuid(), processId: 1234);

        var result = store.Create(intent);
        var read = store.Read();

        Assert.Equal(UpdateIntentCreateStatus.Created, result);
        Assert.Equal(UpdateIntentReadStatus.Active, read.Status);
        Assert.Equal(intent, read.Intent);
        Assert.True(File.Exists(paths.IntentFilePath));
    }

    [Fact]
    public void Create_DoesNotOverwriteAnExistingIntent()
    {
        using var temp = new TemporaryDirectory();
        var (identity, _, store) = CreateStore(temp.Path);
        var winner = CreateIntent(identity, Guid.NewGuid(), processId: 1234);
        var later = CreateIntent(identity, Guid.NewGuid(), processId: 5678);

        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(winner));
        Assert.Equal(UpdateIntentCreateStatus.AlreadyExists, store.Create(later));
        Assert.Equal(winner, store.Read().Intent);
    }

    [Fact]
    public async Task ConcurrentCreators_ExactlyOneWinsWithoutReplacingWinner()
    {
        using var temp = new TemporaryDirectory();
        var (identity, _, store) = CreateStore(temp.Path);
        var intents = new[]
        {
            CreateIntent(identity, Guid.NewGuid(), processId: 1234),
            CreateIntent(identity, Guid.NewGuid(), processId: 5678)
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var creates = intents.Select(intent => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyCount) == intents.Length)
            {
                ready.TrySetResult();
            }
            await start.Task;
            return (Intent: intent, Result: store.Create(intent));
        })).ToArray();
        await ready.Task;
        start.SetResult();
        var results = await Task.WhenAll(creates);

        Assert.Equal(1, results.Count(result => result.Result == UpdateIntentCreateStatus.Created));
        Assert.Equal(1, results.Count(result => result.Result == UpdateIntentCreateStatus.AlreadyExists));
        var winner = results.Single(result => result.Result == UpdateIntentCreateStatus.Created);
        Assert.Equal(winner.Intent, store.Read().Intent);
    }

    [Fact]
    public void Read_DistinguishesMissingIntentFromInvalidIntent()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, store) = CreateStore(temp.Path);
        Assert.Equal(UpdateIntentReadStatus.Missing, store.Read().Status);

        Directory.CreateDirectory(paths.DirectoryPath);
        File.WriteAllText(paths.IntentFilePath, "{\"schemaVersion\":\"1\"}");

        var read = store.Read();
        Assert.Equal(UpdateIntentReadStatus.Invalid, read.Status);
        Assert.Null(read.Intent);
    }

    [Theory]
    [InlineData("\"schemaVersion\":\"1\"")]
    [InlineData("\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("ADD_UNKNOWN_PROPERTY")]
    [InlineData("\"productId\":\"Other\"")]
    [InlineData("\"installIdentity\":\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    public void Read_ClassifiesMalformedOrWrongOwnerAsInvalid(string replacement)
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, store) = CreateStore(temp.Path);
        Directory.CreateDirectory(paths.DirectoryPath);
        File.WriteAllText(paths.IntentFilePath, IntentJson(identity, replacement));

        Assert.Equal(UpdateIntentReadStatus.Invalid, store.Read().Status);
    }

    [Fact]
    public void TryDelete_OnlyDeletesTheMatchingOperationAndCannotDeleteANewerIntent()
    {
        using var temp = new TemporaryDirectory();
        var (identity, _, store) = CreateStore(temp.Path);
        var first = CreateIntent(identity, Guid.NewGuid(), processId: 1234);
        var second = CreateIntent(identity, Guid.NewGuid(), processId: 5678);

        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(first));
        Assert.False(store.TryDelete(second.OperationId));
        Assert.True(store.TryDelete(first.OperationId));
        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(second));
        Assert.False(store.TryDelete(first.OperationId));
        Assert.Equal(second, store.Read().Intent);
    }

    private static (InstallIdentity Identity, UpdateControlPaths Paths, UpdateIntentStore Store) CreateStore(string root)
    {
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(root, "portable-install"));
        var paths = UpdateControlPaths.ForInstall(Path.Combine(root, "local-app-data"), identity);
        return (identity, paths, new UpdateIntentStore(paths));
    }

    private static UpdateIntent CreateIntent(InstallIdentity identity, Guid operationId, int processId) => new(
        1,
        "CodexSlotPing",
        identity.Key,
        operationId,
        processId,
        new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 15, 4, 6, 7, TimeSpan.Zero));

    private static string IntentJson(InstallIdentity identity, string replacement)
    {
        var timestamp = new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var created = new DateTimeOffset(2026, 10, 15, 4, 6, 7, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = "CodexSlotPing",
            installIdentity = identity.Key,
            operationId = "01234567-89ab-cdef-0123-456789abcdef",
            updaterPid = 1234,
            updaterStartedUtc = timestamp,
            createdUtc = created
        });
        if (replacement == "ADD_UNKNOWN_PROPERTY")
        {
            return json[..^1] + ",\"unexpected\":true}";
        }

        var replacementProperty = replacement.Split(':', 2)[0].Trim('"');
        var propertyToken = replacementProperty switch
        {
            "schemaVersion" => "\"schemaVersion\":1",
            "productId" => "\"productId\":\"CodexSlotPing\"",
            "installIdentity" => "\"installIdentity\":\"" + identity.Key + "\"",
            _ => throw new InvalidOperationException("Unknown test replacement.")
        };
        return json.Replace(propertyToken, replacement, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

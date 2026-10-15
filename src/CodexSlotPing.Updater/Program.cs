using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!UpdaterInvocationParser.TryParse(args, out var invocation) || invocation is null)
        {
            Console.Error.WriteLine(nameof(UpdaterResultCode.InvalidArguments));
            return (int)UpdaterResultCode.InvalidArguments;
        }

        try
        {
            var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, invocation.InstallRoot);
            var paths = UpdateControlPaths.ForCurrentUser(identity);
            var intents = new UpdateIntentStore(paths);
            var journals = new UpdateTransactionJournalStore(paths);
            var acknowledgements = new AppStartupAcknowledgementStore(paths);
            var runtime = new SystemUpdateTransactionRuntime();
            var fileSystem = new SystemUpdateTransactionFileSystem();
            var service = new UpdateTransactionService(
                AppContext.BaseDirectory,
                paths,
                intents,
                journals,
                runtime,
                fileSystem,
                new FileStartupAcknowledgementWaiter(acknowledgements),
                new UpdateHandoffReadyStore(paths));
            var recovery = new UpdateTransactionRecoveryService(
                paths,
                intents,
                journals,
                acknowledgements,
                runtime,
                fileSystem);
            var command = new UpdaterCommandRunner(journals, service, recovery);
            var result = await command.RunAsync(invocation).ConfigureAwait(false);
            Console.Error.WriteLine(FormatResult(result, paths, invocation));
            return (int)result.Code;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(UpdaterResultCode.RecoveryRequired);
            return (int)UpdaterResultCode.RecoveryRequired;
        }
        catch (Exception)
        {
            Console.Error.WriteLine(UpdaterResultCode.RecoveryRequired);
            return (int)UpdaterResultCode.RecoveryRequired;
        }
    }

    internal static string FormatResult(UpdateTransactionResult result, UpdateControlPaths paths, UpdaterInvocation invocation)
    {
        var summary = $"{result.Code}: {result.Detail}";
        if (result.Code != UpdaterResultCode.RecoveryRequired)
        {
            return summary;
        }

        var siblings = UpdateTransactionPaths.ForInstall(invocation.InstallRoot, invocation.OperationId);
        return string.Join(Environment.NewLine,
            summary,
            $"Install: {siblings.InstallRoot}",
            $"Journal: {paths.JournalFilePath(invocation.OperationId)}",
            $"Backup: {siblings.BackupSiblingPath}",
            $"New sibling: {siblings.NewSiblingPath}");
    }
}

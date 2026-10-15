using System.Text.Json;

namespace CodexSlotPing.Update;

public sealed class UpdateIntentStore(UpdateControlPaths paths) : IUpdateIntentLifecycle
{
    private static readonly HashSet<string> RequiredProperties = new(
        ["schemaVersion", "productId", "installIdentity", "operationId", "updaterPid", "updaterStartedUtc", "createdUtc"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<string, Stream> _createNew = OpenCreateNew;
    private readonly Action<Stream> _flushToDisk = FlushToDisk;
    private readonly Action<string> _deleteFile = File.Delete;

    internal UpdateIntentStore(
        UpdateControlPaths paths,
        Func<string, Stream> createNew,
        Action<Stream> flushToDisk,
        Action<string> deleteFile) : this(paths)
    {
        _createNew = createNew ?? throw new ArgumentNullException(nameof(createNew));
        _flushToDisk = flushToDisk ?? throw new ArgumentNullException(nameof(flushToDisk));
        _deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
    }

    public UpdateIntentCreateStatus Create(UpdateIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateForThisInstall(intent);

        return WithMutationLock(() =>
        {
            try
            {
                Directory.CreateDirectory(paths.DirectoryPath);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return UpdateIntentCreateStatus.StorageFailure;
            }

            var temporary = paths.IntentFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Stream stream;
            try
            {
                stream = _createNew(temporary);
            }
            catch (IOException exception) when (IsAlreadyExists(exception))
            {
                return File.Exists(paths.IntentFilePath)
                    ? UpdateIntentCreateStatus.AlreadyExists
                    : UpdateIntentCreateStatus.StorageFailure;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return UpdateIntentCreateStatus.StorageFailure;
            }

            try
            {
                using (stream)
                {
                    JsonSerializer.Serialize(stream, intent, WriteOptions);
                    _flushToDisk(stream);
                }
                File.Move(temporary, paths.IntentFilePath);
                return UpdateIntentCreateStatus.Created;
            }
            catch (IOException exception) when (IsAlreadyExists(exception) && File.Exists(paths.IntentFilePath))
            {
                TryDeletePartialMarker(temporary);
                return UpdateIntentCreateStatus.AlreadyExists;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                TryDeletePartialMarker(temporary);
                return UpdateIntentCreateStatus.StorageFailure;
            }
        });
    }

    public UpdateIntentReadResult Read()
    {
        try
        {
            using var stream = new FileStream(
                paths.IntentFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement);
        }
        catch (FileNotFoundException)
        {
            return UpdateIntentReadResult.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return UpdateIntentReadResult.Missing();
        }
        catch (JsonException)
        {
            return UpdateIntentReadResult.Invalid("malformed-json");
        }
        catch (UnauthorizedAccessException)
        {
            return UpdateIntentReadResult.IoError("access-denied");
        }
        catch (IOException)
        {
            return UpdateIntentReadResult.IoError("read-failed");
        }
    }

    public bool TryDelete(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return false;
        }

        return WithMutationLock(() =>
        {
            var read = Read();
            if (read.Status != UpdateIntentReadStatus.Active || read.Intent?.OperationId != operationId)
            {
                return false;
            }

            File.Delete(paths.IntentFilePath);
            return true;
        });
    }

    public bool TryDelete(Guid operationId, ProcessIdentity expectedUpdater)
    {
        ArgumentNullException.ThrowIfNull(expectedUpdater);
        if (operationId == Guid.Empty || expectedUpdater.ProcessId <= 0 || expectedUpdater.StartedUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        return WithMutationLock(() =>
        {
            var read = Read();
            if (read.Status != UpdateIntentReadStatus.Active || read.Intent?.OperationId != operationId ||
                !ProcessIdentityMatcher.Matches(read.Intent, expectedUpdater))
            {
                return false;
            }

            File.Delete(paths.IntentFilePath);
            return true;
        });
    }

    public bool TryUpdateOwner(Guid operationId, ProcessIdentity updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (operationId == Guid.Empty || updater.ProcessId <= 0 || updater.StartedUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        return WithMutationLock(() =>
        {
            var read = Read();
            if (read.Status != UpdateIntentReadStatus.Active || read.Intent?.OperationId != operationId)
            {
                return false;
            }

            var replacement = read.Intent with
            {
                UpdaterPid = updater.ProcessId,
                UpdaterStartedUtc = updater.StartedUtc
            };
            var temporary = paths.IntentFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = _createNew(temporary))
                {
                    JsonSerializer.Serialize(stream, replacement, WriteOptions);
                    _flushToDisk(stream);
                }
                File.Move(temporary, paths.IntentFilePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                TryDeletePartialMarker(temporary);
                return false;
            }
        });
    }

    private UpdateIntentReadResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return UpdateIntentReadResult.Invalid("root-not-object");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!RequiredProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            {
                return UpdateIntentReadResult.Invalid("property-set-invalid");
            }
        }
        if (values.Count != RequiredProperties.Count ||
            !TryInt(values, "schemaVersion", out var schemaVersion) || schemaVersion != 1 ||
            !TryString(values, "productId", out var productId) ||
            !TryString(values, "installIdentity", out var installIdentity) ||
            !TryString(values, "operationId", out var operationText) ||
            !Guid.TryParseExact(operationText, "D", out var operationId) || operationId == Guid.Empty ||
            !TryInt(values, "updaterPid", out var updaterPid) || updaterPid <= 0 ||
            !TryUtc(values, "updaterStartedUtc", out var updaterStartedUtc) ||
            !TryUtc(values, "createdUtc", out var createdUtc))
        {
            return UpdateIntentReadResult.Invalid("schema-invalid");
        }

        if (!string.Equals(productId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) ||
            !string.Equals(installIdentity, paths.InstallIdentity, StringComparison.Ordinal))
        {
            return UpdateIntentReadResult.Invalid("owner-mismatch");
        }

        return UpdateIntentReadResult.Active(new UpdateIntent(
            schemaVersion,
            productId,
            installIdentity,
            operationId,
            updaterPid,
            updaterStartedUtc,
            createdUtc));
    }

    private void ValidateForThisInstall(UpdateIntent intent)
    {
        if (intent.SchemaVersion != 1 ||
            !string.Equals(intent.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) ||
            !string.Equals(intent.InstallIdentity, paths.InstallIdentity, StringComparison.Ordinal) ||
            intent.OperationId == Guid.Empty ||
            intent.UpdaterPid <= 0 ||
            intent.UpdaterStartedUtc.Offset != TimeSpan.Zero ||
            intent.CreatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The update intent is invalid or belongs to another install.", nameof(intent));
        }
    }

    private void TryDeletePartialMarker(string temporary)
    {
        try
        {
            _deleteFile(temporary);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // A remaining partial marker is invalid and causes Runner admission to fail closed.
        }
    }

    private static Stream OpenCreateNew(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.WriteThrough);

    private static void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);

    private static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xFFFF) is 17 or 80 or 183;

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;

    private T WithMutationLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, paths.IntentMutationMutexName);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited. The mutex is acquired by this thread.
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool TryInt(IReadOnlyDictionary<string, JsonElement> values, string name, out int result)
    {
        result = default;
        return values.TryGetValue(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out result);
    }

    private static bool TryString(IReadOnlyDictionary<string, JsonElement> values, string name, out string result)
    {
        result = string.Empty;
        if (!values.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = value.GetString()!;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool TryUtc(IReadOnlyDictionary<string, JsonElement> values, string name, out DateTimeOffset result)
    {
        result = default;
        return values.TryGetValue(name, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               value.TryGetDateTimeOffset(out result) &&
               result.Offset == TimeSpan.Zero;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.Persistence;

public sealed class AtomicJsonFile
{
    private readonly IAtomicFileOperations _fileOperations;
    private readonly JsonSerializerOptions _serializerOptions;

    public AtomicJsonFile(IAtomicFileOperations fileOperations, JsonSerializerOptions? serializerOptions = null)
    {
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _serializerOptions = new JsonSerializerOptions(serializerOptions ?? CreateDefaultSerializerOptions());
    }

    public async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_fileOperations.FileExists(path))
        {
            return null;
        }

        try
        {
            await using var stream = _fileOperations.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return value ?? throw new InvalidDataException("The JSON document must not contain null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The JSON document is malformed or incomplete.", exception);
        }
    }

    public async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination must include a parent directory.", nameof(path));
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var temporaryFileCreated = false;

        _fileOperations.CreateDirectory(directory);
        try
        {
            await using (var stream = _fileOperations.CreateNew(temporaryPath))
            {
                temporaryFileCreated = true;
                await stream.WriteAsync(jsonBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                _fileOperations.FlushToDisk(stream);
            }

            if (_fileOperations.FileExists(fullPath))
            {
                _fileOperations.Replace(temporaryPath, fullPath);
            }
            else
            {
                _fileOperations.Move(temporaryPath, fullPath);
            }

            temporaryFileCreated = false;
        }
        finally
        {
            if (temporaryFileCreated && _fileOperations.FileExists(temporaryPath))
            {
                _fileOperations.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateDefaultSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

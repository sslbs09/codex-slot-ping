using System.Net;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdatePackageDownloaderTests
{
    private static readonly SemVersion Version = SemVersion.Parse("0.3.0");
    private const string ZipName = "CodexSlotPing-v0.3.0-win-x64.zip";

    [Fact]
    public async Task DownloadAssetAsync_StreamsSuccessfulDownloadToPartFile()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        var source = new ProbeStream(bytes, 17_000);
        string? userAgent = null;
        var handler = new DelegateHandler((request, _) =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(Response(HttpStatusCode.OK, new StreamContent(source)));
        });
        using var client = new HttpClient(handler);
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");
        var progress = new RecordingProgress<PackageDownloadProgress>();

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl(), bytes.Length), Version, UpdatePackageAssetKind.Archive, destination, progress);

        Assert.True(result.Succeeded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.True(source.ReadCount > 2);
        Assert.Contains(progress.Values, value => value.BytesReceived > 0);
        Assert.Equal("CodexSlotPing/0.3", userAgent);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task DownloadAssetAsync_ReturnsTypedFailureForHttpError()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl()), Version, UpdatePackageAssetKind.Archive, destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.HttpFailure, result.Failure);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("https://example.com/sslbs09/codex-slot-ping/releases/download/v0.3.0/CodexSlotPing-v0.3.0-win-x64.zip")]
    [InlineData("https://github.com/other/repository/releases/download/v0.3.0/CodexSlotPing-v0.3.0-win-x64.zip")]
    [InlineData("https://github.com/sslbs09/codex-slot-ping/releases/download/v0.3.1/CodexSlotPing-v0.3.0-win-x64.zip")]
    [InlineData("https://github.com/sslbs09/codex-slot-ping/releases/download/v0.3.0/wrong.zip")]
    public async Task DownloadAssetAsync_RejectsWrongInitialUrl(string url)
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => throw new InvalidOperationException("network must not be used")));
        using var temp = new TemporaryDirectory();

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, new Uri(url)), Version, UpdatePackageAssetKind.Archive, Path.Combine(temp.Path, "package.zip.part"));

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.InvalidAsset, result.Failure);
    }

    [Fact]
    public async Task DownloadAssetAsync_RejectsMismatchedAssetFilename()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => throw new InvalidOperationException("network must not be used")));
        using var temp = new TemporaryDirectory();

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset("renamed.zip", ZipUrl()), Version, UpdatePackageAssetKind.Archive, Path.Combine(temp.Path, "package.zip.part"));

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.InvalidAsset, result.Failure);
    }

    [Fact]
    public async Task DownloadAssetAsync_AllowsHttpsGitHubCdnRedirect()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            if (requests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/package.zip");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3])));
        });
        using var client = new HttpClient(handler);
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl(), size: 3), Version, UpdatePackageAssetKind.Archive, destination);

        Assert.True(result.Succeeded);
        Assert.Equal(2, requests.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAssetAsync_RejectsRedirectOutsideGitHubAssetHosts()
    {
        var handler = new DelegateHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://example.com/asset.zip");
            return Task.FromResult(redirect);
        });
        using var client = new HttpClient(handler);
        using var temp = new TemporaryDirectory();

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl()), Version, UpdatePackageAssetKind.Archive, Path.Combine(temp.Path, "package.zip.part"));

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.RedirectRejected, result.Failure);
    }

    [Fact]
    public async Task DownloadAssetAsync_RemovesPartialFileAfterStreamFailure()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new StreamContent(new FailingAfterFirstReadStream()))));
        using var client = new HttpClient(handler);
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");

        var result = await new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl()), Version, UpdatePackageAssetKind.Archive, destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.NetworkFailure, result.Failure);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAssetAsync_RemovesPartialFileAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new DelegateHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new StreamContent(new BlockingAfterFirstReadStream()))));
        using var client = new HttpClient(handler);
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdatePackageDownloader(client).DownloadAssetAsync(
            Asset(ZipName, ZipUrl()), Version, UpdatePackageAssetKind.Archive, destination, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAssetAsync_ReportsTimeoutAndRemovesPartialFile()
    {
        using var client = new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "package.zip.part");

        var result = await new UpdatePackageDownloader(client, TimeSpan.FromMilliseconds(30)).DownloadAssetAsync(
            Asset(ZipName, ZipUrl()), Version, UpdatePackageAssetKind.Archive, destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageDownloadFailure.Timeout, result.Failure);
        Assert.False(File.Exists(destination));
    }

    private static GitHubReleaseAsset Asset(string name, Uri uri, long size = 100) => new(name, uri, size);
    private static Uri ZipUrl() => new($"https://github.com/sslbs09/codex-slot-ping/releases/download/v{Version}/{ZipName}");

    private static HttpResponseMessage Response(HttpStatusCode status, HttpContent content) => new(status) { Content = content };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class ProbeStream(byte[] bytes, int chunkSize) : Stream
    {
        private int _offset;
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingAfterFirstReadStream : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 2;
        public override long Position { get => _read ? 1 : 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("simulated interrupted response");
            _read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingAfterFirstReadStream : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 2;
        public override long Position { get => _read ? 1 : 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) await Task.Delay(Timeout.Infinite, cancellationToken);
            _read = true;
            buffer.Span[0] = 1;
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

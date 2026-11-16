using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public sealed class TwinWebClientPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jw-twin-web-" + Guid.NewGuid().ToString("N"));
    private readonly string _webPath;
    private readonly RecordingStore _store = new();
    private readonly TwinWebClientPublisher _publisher;

    public TwinWebClientPublisherTests()
    {
        _webPath = Path.Combine(_root, "web");
        Directory.CreateDirectory(Path.Combine(_webPath, "themes", "dark"));
        File.WriteAllText(Path.Combine(_webPath, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_webPath, "main.abc.js"), "1");
        File.WriteAllText(Path.Combine(_webPath, "themes", "dark", "theme.css"), "body{}");

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_root, "data"));
        _publisher = new TwinWebClientPublisher(paths.Object, NullLogger<TwinWebClientPublisher>.Instance);
    }

    [Fact]
    public async Task Publish_FirstRun_SendsEveryFileWithThePageLast()
    {
        var (published, removed) = await _publisher.PublishAsync(_store, "t", _webPath, TestContext.Current.CancellationToken);

        Assert.Equal(3, published);
        Assert.Equal(0, removed);
        Assert.Equal(["web/main.abc.js", "web/themes/dark/theme.css"], _store.Puts.Take(2).Order());
        Assert.Equal("web/index.html", _store.Puts[^1]);
        Assert.Equal("text/css", _store.ContentTypes["web/themes/dark/theme.css"]);
    }

    [Fact]
    public async Task Publish_SecondRun_SendsOnlyWhatChangedAndRemovesWhatIsGone()
    {
        await _publisher.PublishAsync(_store, "t", _webPath, TestContext.Current.CancellationToken);
        _store.Puts.Clear();

        Assert.Equal((0, 0), await _publisher.PublishAsync(_store, "t", _webPath, TestContext.Current.CancellationToken));
        Assert.Empty(_store.Puts);

        await File.WriteAllTextAsync(Path.Combine(_webPath, "main.abc.js"), "changed", TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(_webPath, "themes", "dark", "theme.css"));

        Assert.Equal((1, 1), await _publisher.PublishAsync(_store, "t", _webPath, TestContext.Current.CancellationToken));
        Assert.Equal(["web/main.abc.js"], _store.Puts);
        Assert.Equal(["web/themes/dark/theme.css"], _store.Deletes);
    }

    [Fact]
    public async Task Publish_DifferentStore_StartsFromNothing()
    {
        await _publisher.PublishAsync(_store, "first", _webPath, TestContext.Current.CancellationToken);
        _store.Puts.Clear();

        var (published, _) = await _publisher.PublishAsync(_store, "second", _webPath, TestContext.Current.CancellationToken);

        Assert.Equal(3, published);
    }

    [Fact]
    public async Task Publish_NoWebClient_DoesNothing()
    {
        File.Delete(Path.Combine(_webPath, "index.html"));

        Assert.Equal((0, 0), await _publisher.PublishAsync(_store, "t", _webPath, TestContext.Current.CancellationToken));
        Assert.Empty(_store.Puts);
        Assert.Empty(_store.Deletes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class RecordingStore : IObjectStore
    {
        public List<string> Puts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Dictionary<string, string> ContentTypes { get; } = [];

        public Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken)
        {
            Puts.Add(key);
            ContentTypes[key] = contentType;
            return Task.CompletedTask;
        }

        public Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
        {
            Puts.Add(key);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Deletes.Add(key);
            return Task.CompletedTask;
        }

        public Task<(byte[] Content, string Tag)?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult<(byte[] Content, string Tag)?>(null);

        public Task<bool> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? tag, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}

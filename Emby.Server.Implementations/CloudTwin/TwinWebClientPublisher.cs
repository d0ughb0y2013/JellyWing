using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Publishes the web client to the twin's store under <c>web/</c>, so the twin can serve it
/// and a browser pointed at the twin gets the same client as at home.
/// </summary>
public class TwinWebClientPublisher
{
    private const string Prefix = "web/";
    private const string EntryPage = "index.html";
    private const int SaveEvery = 100;

    private static readonly Dictionary<string, string> _contentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".js"] = "text/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf",
        [".wasm"] = "application/wasm"
    };

    private readonly ILogger<TwinWebClientPublisher> _logger;
    private readonly string _statePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="TwinWebClientPublisher"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public TwinWebClientPublisher(IApplicationPaths applicationPaths, ILogger<TwinWebClientPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _logger = logger;
        _statePath = Path.Combine(applicationPaths.DataPath, "cloud-twin", "web-state.json");
    }

    /// <summary>
    /// Sends the files that are new or changed, and removes the ones that are gone.
    /// </summary>
    /// <param name="store">Where to publish.</param>
    /// <param name="target">A name for the store, so a change of store starts from nothing.</param>
    /// <param name="webPath">The folder holding the built web client.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many files were sent and how many were removed.</returns>
    public async Task<(int Published, int Removed)> PublishAsync(IObjectStore store, string target, string webPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrEmpty(webPath) || !File.Exists(Path.Combine(webPath, EntryPage)))
        {
            _logger.LogInformation("No web client found at {WebPath}; the twin's web client is left as it is.", webPath);
            return (0, 0);
        }

        var state = LoadState(target);

        // The page that names every other file goes last, so it never points at a file that is not there yet.
        var files = Directory.EnumerateFiles(webPath, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: Path.GetRelativePath(webPath, path).Replace('\\', '/')))
            .OrderBy(file => string.Equals(file.Name, EntryPage, StringComparison.Ordinal))
            .ToList();

        var published = 0;
        var removed = 0;
        try
        {
            foreach (var (path, name) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stamp = Stamp(path);
                if (state.Files.TryGetValue(name, out var sent) && string.Equals(sent, stamp, StringComparison.Ordinal))
                {
                    continue;
                }

                var contentType = _contentTypes.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
                await store.PutFileAsync(Prefix + name, path, contentType, cancellationToken).ConfigureAwait(false);
                state.Files[name] = stamp;
                if (++published % SaveEvery == 0)
                {
                    SaveState(state);
                }
            }

            var current = files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in state.Files.Keys.Where(name => !current.Contains(name)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await store.DeleteAsync(Prefix + gone, cancellationToken).ConfigureAwait(false);
                state.Files.Remove(gone);
                removed++;
            }
        }
        finally
        {
            if (published > 0 || removed > 0)
            {
                SaveState(state);
            }
        }

        _logger.LogInformation("Cloud twin web client: {Published} files sent, {Removed} removed, {Total} in all.", published, removed, files.Count);
        return (published, removed);
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}");
    }

    private TwinWebClientState LoadState(string target)
    {
        try
        {
            if (File.Exists(_statePath)
                && JsonSerializer.Deserialize<TwinWebClientState>(File.ReadAllBytes(_statePath), JsonDefaults.Options) is { } state
                && string.Equals(state.Target, target, StringComparison.Ordinal))
            {
                return state;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The record of the published web client could not be read; it will be published again.");
        }

        return new TwinWebClientState { Target = target };
    }

    private void SaveState(TwinWebClientState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllBytes(_statePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options));
    }
}

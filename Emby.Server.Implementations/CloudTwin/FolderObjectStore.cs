using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Publishes to a folder on disk: for trying the cloud twin out, and for setups that sync
/// a folder to storage with a tool of their own.
/// </summary>
public class FolderObjectStore : IObjectStore
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderObjectStore"/> class.
    /// </summary>
    /// <param name="root">The folder to publish into.</param>
    public FolderObjectStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken)
    {
        var target = Resolve(key);
        var partial = target + ".partial";
        File.Copy(filePath, partial, overwrite: true);
        File.Move(partial, target, overwrite: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var target = Resolve(key);
        var partial = target + ".partial";
        await File.WriteAllBytesAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, target, overwrite: true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(Resolve(key));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<(byte[] Content, string Tag)?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return (content, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    /// <inheritdoc />
    public async Task<bool> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? tag, CancellationToken cancellationToken)
    {
        // Good enough for a local stand-in: the check and the write are not one step.
        var current = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current?.Tag, tag, StringComparison.Ordinal))
        {
            return false;
        }

        await PutBytesAsync(key, content, contentType, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The key leaves the store: " + key, nameof(key));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}

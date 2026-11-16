using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// A place the cloud twin's files are published to, addressed by key.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Stores a file under a key, replacing what was there.
    /// </summary>
    /// <param name="key">The key, with forward slashes between parts.</param>
    /// <param name="filePath">The file to store.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Stores bytes under a key, replacing what was there.
    /// </summary>
    /// <param name="key">The key, with forward slashes between parts.</param>
    /// <param name="content">The bytes to store.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Removes what is stored under a key. Removing a key that holds nothing is not an error.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a small object and the version it is at.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The content and its version tag, or <c>null</c> when there is no such object.</returns>
    Task<(byte[] Content, string Tag)?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an object only if it is still at the version given, so a change made elsewhere in between is not lost.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="content">The new content.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="tag">The version read earlier, or <c>null</c> when the object did not exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when the object changed in between and nothing was written.</returns>
    Task<bool> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? tag, CancellationToken cancellationToken);
}

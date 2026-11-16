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
}

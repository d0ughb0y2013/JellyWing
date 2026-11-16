using System;
using System.Net.Http;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Opens the store the settings name.
/// </summary>
public static class TwinStoreFactory
{
    /// <summary>
    /// Opens the store and gives it a name that changes when the settings point somewhere else.
    /// </summary>
    /// <param name="options">The cloud twin settings, which must be complete.</param>
    /// <param name="httpClient">The client to reach an S3-compatible store with.</param>
    /// <returns>The store and its name.</returns>
    public static (IObjectStore Store, string Target) Create(TwinOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrEmpty(options.Folder)
            ? (new S3ObjectStore(httpClient, options), $"s3:{options.S3Endpoint}/{options.S3Bucket}")
            : (new FolderObjectStore(options.Folder), "folder:" + options.Folder);
    }
}

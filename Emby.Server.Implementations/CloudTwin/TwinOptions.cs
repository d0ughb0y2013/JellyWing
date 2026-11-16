using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Where and what the cloud twin publishes, read from the server's startup settings.
/// </summary>
public class TwinOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TwinOptions"/> class.
    /// </summary>
    /// <param name="configuration">The startup configuration.</param>
    public TwinOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Folder = configuration["twin:folder"];
        S3Endpoint = configuration["twin:s3:endpoint"];
        S3Bucket = configuration["twin:s3:bucket"];
        S3Region = configuration["twin:s3:region"] ?? "auto";
        S3AccessKeyId = configuration["twin:s3:accessKeyId"];
        S3SecretAccessKey = configuration["twin:s3:secretAccessKey"];
        Libraries = (configuration["twin:libraries"] ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Gets a folder to publish into instead of a store.
    /// </summary>
    public string? Folder { get; }

    /// <summary>
    /// Gets the address of the S3-compatible store.
    /// </summary>
    public string? S3Endpoint { get; }

    /// <summary>
    /// Gets the bucket to publish into.
    /// </summary>
    public string? S3Bucket { get; }

    /// <summary>
    /// Gets the region; <c>auto</c> for stores without regions.
    /// </summary>
    public string S3Region { get; }

    /// <summary>
    /// Gets the access key id.
    /// </summary>
    public string? S3AccessKeyId { get; }

    /// <summary>
    /// Gets the secret access key.
    /// </summary>
    public string? S3SecretAccessKey { get; }

    /// <summary>
    /// Gets the names of the libraries to publish. Nothing is published unless named here.
    /// </summary>
    public IReadOnlyList<string> Libraries { get; }

    /// <summary>
    /// Gets a value indicating whether the S3 settings are complete.
    /// </summary>
    public bool HasS3 => !string.IsNullOrEmpty(S3Endpoint)
        && !string.IsNullOrEmpty(S3Bucket)
        && !string.IsNullOrEmpty(S3AccessKeyId)
        && !string.IsNullOrEmpty(S3SecretAccessKey);

    /// <summary>
    /// Gets a value indicating whether there is somewhere to publish to and something to publish.
    /// </summary>
    public bool IsConfigured => Libraries.Count > 0 && (!string.IsNullOrEmpty(Folder) || HasS3);
}

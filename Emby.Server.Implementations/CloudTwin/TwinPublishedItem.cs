namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// What was last published for one item, kept at home to tell what has changed since.
/// </summary>
public class TwinPublishedItem
{
    /// <summary>
    /// Gets or sets the key of the published playable file.
    /// </summary>
    public string? MediaKey { get; set; }

    /// <summary>
    /// Gets or sets the size and modified time of the file that was published.
    /// </summary>
    public string? MediaStamp { get; set; }

    /// <summary>
    /// Gets or sets the key of the published image.
    /// </summary>
    public string? ImageKey { get; set; }

    /// <summary>
    /// Gets or sets the size and modified time of the image that was published.
    /// </summary>
    public string? ImageStamp { get; set; }

    /// <summary>
    /// Gets or sets the hash of the published record.
    /// </summary>
    public string? RecordHash { get; set; }
}

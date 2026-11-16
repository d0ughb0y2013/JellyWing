using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// What the cloud twin knows about one item: enough to browse to it and play it.
/// </summary>
public class TwinRecord
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the kind of item: Movie, Series, Season or Episode.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the name of the library the item is in.
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the series an episode or season belongs to.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the id of the season an episode belongs to.
    /// </summary>
    public Guid? SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the episode or season number.
    /// </summary>
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the season number of an episode.
    /// </summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the year of release.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the date of first release.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the running time in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the key of the playable file, for a video.
    /// </summary>
    public string? MediaKey { get; set; }

    /// <summary>
    /// Gets or sets the size of the playable file in bytes.
    /// </summary>
    public long? MediaSize { get; set; }

    /// <summary>
    /// Gets or sets the key of the poster or thumbnail.
    /// </summary>
    public string? ImageKey { get; set; }
}

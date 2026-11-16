using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// A single-use invitation made on the twin.
/// </summary>
public class TwinInvitation
{
    /// <summary>
    /// Gets or sets when it was made, in milliseconds since 1970.
    /// </summary>
    public long Created { get; set; }

    /// <summary>
    /// Gets or sets when it stops working, in milliseconds since 1970.
    /// </summary>
    public long Expires { get; set; }

    /// <summary>
    /// Gets or sets the name of the administrator who made it.
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Gets or sets the name picked by whoever used it.
    /// </summary>
    public string? UsedBy { get; set; }

    /// <summary>
    /// Gets or sets when it was used, in milliseconds since 1970.
    /// </summary>
    public long? UsedAt { get; set; }

    /// <summary>
    /// Gets or sets the libraries whoever uses it may see. Nothing set means every published library.
    /// </summary>
    public IReadOnlyList<string>? Libraries { get; set; }
}

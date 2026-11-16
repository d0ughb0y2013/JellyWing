using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Someone who accepted an invitation on the twin.
/// </summary>
public class TwinInvitedUser
{
    /// <summary>
    /// Gets or sets the id the twin gave them when they accepted.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name they picked.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the password they picked, until the account exists at home.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets when they accepted, in milliseconds since 1970.
    /// </summary>
    public long Accepted { get; set; }

    /// <summary>
    /// Gets or sets the name of the administrator who invited them.
    /// </summary>
    public string? InvitedBy { get; set; }

    /// <summary>
    /// Gets or sets the id of their account at home, once it exists.
    /// </summary>
    public string? HomeId { get; set; }

    /// <summary>
    /// Gets or sets why the home server would not make the account, if it would not.
    /// </summary>
    public string? Rejected { get; set; }

    /// <summary>
    /// Gets or sets the libraries they were invited to. Nothing set means every published library.
    /// </summary>
    public IReadOnlyList<string>? Libraries { get; set; }
}

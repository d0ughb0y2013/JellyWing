using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// The content of <c>state/invitations.json</c> in the twin's store.
/// </summary>
public class TwinInvitations
{
    /// <summary>
    /// Gets or sets the invitations, by the hash of their code.
    /// </summary>
    public Dictionary<string, TwinInvitation> Invitations { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the people who accepted one.
    /// </summary>
    public List<TwinInvitedUser> Users { get; set; } = [];
}

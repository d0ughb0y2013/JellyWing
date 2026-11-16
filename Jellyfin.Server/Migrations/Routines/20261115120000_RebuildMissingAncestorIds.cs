using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Re-adds ancestor rows that an item's parent chain implies but the table no longer holds.
/// Played and total counts of a folder are read from these rows, so a show whose episodes lost
/// theirs counts no episodes and reports as watched.
/// </summary>
/// <remarks>
/// Insert-only, so it takes no backup: a row is added only where the parent row exists.
/// </remarks>
[JellyfinMigration("2026-11-15T12:00:00", nameof(RebuildMissingAncestorIds))]
public class RebuildMissingAncestorIds : IAsyncMigrationRoutine
{
    // Deeper than any real library; bounds the loop if ParentId ever forms a cycle.
    private const int MaxPasses = 64;

    // Mirrors BaseItem.SupportsAncestors.
    private const string TypeFilter = """c."Type" NOT IN ({0}, {1}, {2}, {3}, {4})""";

    private const string DirectParentSql = $"""
        INSERT INTO "AncestorIds" ("ItemId", "ParentItemId")
        SELECT c."Id", c."ParentId"
        FROM "BaseItems" c
        WHERE c."ParentId" IS NOT NULL
          AND c."ParentId" <> c."Id"
          AND {TypeFilter}
          AND EXISTS (SELECT 1 FROM "BaseItems" p WHERE p."Id" = c."ParentId")
          AND NOT EXISTS (
              SELECT 1 FROM "AncestorIds" x
              WHERE x."ItemId" = c."Id" AND x."ParentItemId" = c."ParentId")
        """;

    // An item has every ancestor its parent has.
    private const string InheritedSql = $"""
        INSERT INTO "AncestorIds" ("ItemId", "ParentItemId")
        SELECT c."Id", a."ParentItemId"
        FROM "BaseItems" c
        INNER JOIN "AncestorIds" a ON a."ItemId" = c."ParentId"
        WHERE a."ParentItemId" <> c."Id"
          AND {TypeFilter}
          AND NOT EXISTS (
              SELECT 1 FROM "AncestorIds" x
              WHERE x."ItemId" = c."Id" AND x."ParentItemId" = a."ParentItemId")
        """;

    private static readonly object[] _typesWithoutAncestors =
    [
        typeof(Person).FullName!,
        typeof(Genre).FullName!,
        typeof(MusicGenre).FullName!,
        typeof(Studio).FullName!,
        typeof(Year).FullName!
    ];

    private readonly IStartupLogger<RebuildMissingAncestorIds> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebuildMissingAncestorIds"/> class.
    /// </summary>
    /// <param name="logger">The startup logger.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    public RebuildMissingAncestorIds(
        IStartupLogger<RebuildMissingAncestorIds> logger,
        IDbContextFactory<JellyfinDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var added = await dbContext.Database
                .ExecuteSqlRawAsync(DirectParentSql, _typesWithoutAncestors, cancellationToken)
                .ConfigureAwait(false);

            // Each pass carries ancestors one level further down.
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var inherited = await dbContext.Database
                    .ExecuteSqlRawAsync(InheritedSql, _typesWithoutAncestors, cancellationToken)
                    .ConfigureAwait(false);
                if (inherited == 0)
                {
                    break;
                }

                added += inherited;
            }

            if (added == 0)
            {
                _logger.LogInformation("No ancestor rows were missing.");
            }
            else
            {
                _logger.LogInformation("Re-added {Count} missing ancestor rows.", added);
            }
        }
    }
}

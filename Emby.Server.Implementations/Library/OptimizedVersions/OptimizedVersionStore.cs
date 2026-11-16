using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Keeps optimized versions on disk, outside the media libraries, one file per item.
/// </summary>
public class OptimizedVersionStore
{
    private const string FolderName = "optimized-versions";
    private const string MediaExtension = ".mp4";
    private const string InfoExtension = ".json";

    // Copies share a disk with the server's database, which must never run out of room.
    private const long FreeSpaceToKeep = 2L * 1024 * 1024 * 1024;

    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    public OptimizedVersionStore(IApplicationPaths applicationPaths)
    {
        _root = Path.Combine(applicationPaths.DataPath, FolderName);
    }

    /// <summary>
    /// Gets where an item's optimized version is, or would be, stored.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The file path.</returns>
    public string GetMediaPath(Guid itemId)
    {
        var name = itemId.ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(_root, name[..2], name + MediaExtension);
    }

    /// <summary>
    /// Gets an item's optimized version if one exists and still matches its source file.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="sourcePath">The item's current file.</param>
    /// <returns>The recorded info, or <c>null</c>.</returns>
    public OptimizedVersionInfo? Find(Guid itemId, string sourcePath)
    {
        var mediaPath = GetMediaPath(itemId);
        var infoPath = Path.ChangeExtension(mediaPath, InfoExtension);
        if (!File.Exists(mediaPath) || !File.Exists(infoPath))
        {
            return null;
        }

        OptimizedVersionInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<OptimizedVersionInfo>(File.ReadAllBytes(infoPath), JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        var source = new FileInfo(sourcePath);
        if (info is null
            || !source.Exists
            || !string.Equals(info.SourcePath, sourcePath, StringComparison.Ordinal)
            || info.SourceSize != source.Length
            || info.SourceModifiedTicks != source.LastWriteTimeUtc.Ticks)
        {
            return null;
        }

        return info;
    }

    /// <summary>
    /// Takes a finished file into the store as an item's optimized version.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="finishedFile">The file to move into place.</param>
    /// <param name="info">What to record about it.</param>
    public void Save(Guid itemId, string finishedFile, OptimizedVersionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var mediaPath = GetMediaPath(itemId);
        var infoPath = Path.ChangeExtension(mediaPath, InfoExtension);

        // The info file is what makes a version usable, so it goes away first and comes back last.
        File.Delete(infoPath);
        File.Move(finishedFile, mediaPath, overwrite: true);
        File.WriteAllBytes(infoPath, JsonSerializer.SerializeToUtf8Bytes(info, JsonDefaults.Options));
    }

    /// <summary>
    /// Gets a path inside the store to write an item's version to before it is finished.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The working file path; its folder exists.</returns>
    public string GetWorkingPath(Guid itemId)
    {
        var mediaPath = GetMediaPath(itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        return mediaPath + ".partial";
    }

    /// <summary>
    /// Gets a value indicating whether the store's disk has room for a copy of a file.
    /// A copy is never larger than about the size of its source.
    /// </summary>
    /// <param name="sourceSize">The size of the source file.</param>
    /// <returns><c>true</c> if writing the copy would still leave free space to spare.</returns>
    public bool HasRoomFor(long sourceSize)
    {
        Directory.CreateDirectory(_root);
        var free = new DriveInfo(_root).AvailableFreeSpace;
        return free - sourceSize >= FreeSpaceToKeep;
    }

    /// <summary>
    /// Removes an item's optimized version, if it has one.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    public void Delete(Guid itemId)
    {
        var mediaPath = GetMediaPath(itemId);
        File.Delete(Path.ChangeExtension(mediaPath, InfoExtension));
        File.Delete(mediaPath);
        File.Delete(mediaPath + ".partial");
    }

    /// <summary>
    /// Counts the finished optimized versions in the store and adds up their sizes.
    /// </summary>
    /// <returns>The number of copies and their total size in bytes.</returns>
    public (int Count, long TotalSize) GetTotals()
    {
        if (!Directory.Exists(_root))
        {
            return (0, 0);
        }

        var count = 0;
        long size = 0;
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*" + MediaExtension, SearchOption.AllDirectories))
        {
            count++;
            size += file.Length;
        }

        return (count, size);
    }

    /// <summary>
    /// Lists the items that have files in the store.
    /// </summary>
    /// <returns>The item ids.</returns>
    public IReadOnlyList<Guid> GetStoredItemIds()
    {
        var ids = new HashSet<Guid>();
        if (!Directory.Exists(_root))
        {
            return [];
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && Guid.TryParseExact(name[..dot], "N", out var id))
            {
                ids.Add(id);
            }
        }

        return [.. ids];
    }
}

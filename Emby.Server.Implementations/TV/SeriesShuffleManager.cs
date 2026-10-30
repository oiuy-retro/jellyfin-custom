#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Emby.Server.Implementations.TV;

/// <summary>
/// Persists and manages per-user, per-series shuffle state and shuffled episode queues.
/// </summary>
public sealed class SeriesShuffleManager : ISeriesShuffleManager
{
    private const string StateFileName = "series-shuffle-state.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Lock _syncLock = new();
    private readonly string? _stateFilePath;
    private readonly ILogger<SeriesShuffleManager> _logger;
    private readonly Dictionary<(Guid UserId, Guid SeriesId), SeriesShuffleStateEntry> _entries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesShuffleManager"/> class.
    /// </summary>
    /// <param name="applicationPaths">Optional application paths for JSON persistence.</param>
    /// <param name="logger">Optional logger.</param>
    public SeriesShuffleManager(
        IApplicationPaths? applicationPaths = null,
        ILogger<SeriesShuffleManager>? logger = null)
    {
        _logger = logger ?? NullLogger<SeriesShuffleManager>.Instance;
        if (!string.IsNullOrWhiteSpace(applicationPaths?.DataPath))
        {
            _stateFilePath = Path.Combine(applicationPaths.DataPath, StateFileName);
            LoadStateFromDisk();
        }
    }

    /// <inheritdoc />
    public void RecordSeriesShuffle(
        Guid userId,
        Guid seriesId,
        string? seriesPresentationUniqueKey,
        Guid? seasonId,
        IReadOnlyList<Guid> shuffledEpisodeIds)
    {
        if (userId.IsEmpty() || seriesId.IsEmpty() || shuffledEpisodeIds is null)
        {
            return;
        }

        var distinctIds = new List<Guid>(shuffledEpisodeIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var id in shuffledEpisodeIds)
        {
            if (!id.IsEmpty() && seen.Add(id))
            {
                distinctIds.Add(id);
            }
        }

        if (distinctIds.Count == 0)
        {
            return;
        }

        lock (_syncLock)
        {
            _entries[(userId, seriesId)] = new SeriesShuffleStateEntry
            {
                UserId = userId,
                SeriesId = seriesId,
                SeriesPresentationUniqueKey = seriesPresentationUniqueKey,
                SeasonId = seasonId is { } sId && !sId.IsEmpty() ? sId : null,
                IsActive = true,
                EpisodeIds = distinctIds,
                LastEpisodeId = distinctIds[0],
                LastUpdatedUtc = DateTime.UtcNow
            };

            SaveStateToDiskLocked();
        }
    }

    /// <inheritdoc />
    public void ClearSeriesShuffle(Guid userId, Guid seriesId)
    {
        if (userId.IsEmpty() || seriesId.IsEmpty())
        {
            return;
        }

        lock (_syncLock)
        {
            if (_entries.Remove((userId, seriesId)))
            {
                SaveStateToDiskLocked();
            }
        }
    }

    /// <inheritdoc />
    public bool IsShuffleActive(Guid userId, Guid seriesId)
    {
        if (userId.IsEmpty() || seriesId.IsEmpty())
        {
            return false;
        }

        lock (_syncLock)
        {
            return _entries.TryGetValue((userId, seriesId), out var entry)
                && entry.IsActive
                && entry.EpisodeIds.Count > 0;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> OrderEpisodesForResume(
        Guid userId,
        Guid seriesId,
        IReadOnlyList<BaseItem> episodes,
        Guid startItemId)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        if (episodes.Count == 0 || startItemId.IsEmpty())
        {
            return episodes;
        }

        lock (_syncLock)
        {
            if (!_entries.TryGetValue((userId, seriesId), out var entry) || !entry.IsActive || entry.EpisodeIds.Count == 0)
            {
                return episodes
                    .SkipWhile(i => !startItemId.Equals(i.Id))
                    .ToList();
            }

            var candidateEpisodes = FilterCandidateEpisodesBySeasonLocked(entry, episodes, startItemId);
            var episodeById = BuildEpisodeLookup(candidateEpisodes);

            if (!episodeById.ContainsKey(startItemId))
            {
                episodeById = BuildEpisodeLookup(episodes);
                if (!episodeById.ContainsKey(startItemId))
                {
                    return [];
                }

                entry.SeasonId = null;
                candidateEpisodes = episodes;
            }

            var reconciledIds = ReconcileEpisodeIds(entry.EpisodeIds, candidateEpisodes, episodeById);
            var rotatedIds = RotateToStartItem(reconciledIds, startItemId);

            entry.EpisodeIds = rotatedIds;
            entry.LastEpisodeId = startItemId;
            entry.LastUpdatedUtc = DateTime.UtcNow;
            SaveStateToDiskLocked();

            var orderedResult = new List<BaseItem>(rotatedIds.Count);
            foreach (var id in rotatedIds)
            {
                if (episodeById.TryGetValue(id, out var item))
                {
                    orderedResult.Add(item);
                }
            }

            return orderedResult;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> OrderEpisodesForAdjacency(
        Guid userId,
        Guid seriesId,
        IReadOnlyList<BaseItem> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        if (episodes.Count == 0)
        {
            return episodes;
        }

        lock (_syncLock)
        {
            if (!_entries.TryGetValue((userId, seriesId), out var entry) || !entry.IsActive || entry.EpisodeIds.Count == 0)
            {
                return episodes;
            }

            var episodeById = BuildEpisodeLookup(episodes);
            var reconciledIds = ReconcileEpisodeIds(entry.EpisodeIds, episodes, episodeById);

            var orderedResult = new List<BaseItem>(reconciledIds.Count);
            foreach (var id in reconciledIds)
            {
                if (episodeById.TryGetValue(id, out var item))
                {
                    orderedResult.Add(item);
                }
            }

            return orderedResult;
        }
    }

    /// <inheritdoc />
    public void OnPlaybackReported(
        Guid userId,
        Guid seriesId,
        string? seriesPresentationUniqueKey,
        Guid episodeId,
        PlaybackOrder playbackOrder,
        bool toggledOffInActiveSession,
        IReadOnlyList<Guid>? queueItemIds,
        Func<IReadOnlyList<Guid>>? allSeriesEpisodeIdsProvider = null)
    {
        if (userId.IsEmpty() || seriesId.IsEmpty() || episodeId.IsEmpty())
        {
            return;
        }

        lock (_syncLock)
        {
            if (playbackOrder == PlaybackOrder.Shuffle)
            {
                if (_entries.TryGetValue((userId, seriesId), out var existing) && existing.IsActive && existing.EpisodeIds.Count > 0)
                {
                    if (!string.IsNullOrEmpty(seriesPresentationUniqueKey))
                    {
                        existing.SeriesPresentationUniqueKey = seriesPresentationUniqueKey;
                    }

                    if (!existing.EpisodeIds.Contains(episodeId))
                    {
                        existing.EpisodeIds.Insert(0, episodeId);
                    }
                    else
                    {
                        existing.EpisodeIds = RotateToStartItem(existing.EpisodeIds, episodeId);
                    }

                    existing.LastEpisodeId = episodeId;
                    existing.LastUpdatedUtc = DateTime.UtcNow;
                    SaveStateToDiskLocked();
                    return;
                }

                var initialOrder = new List<Guid> { episodeId };
                var seen = new HashSet<Guid> { episodeId };

                if (queueItemIds is not null)
                {
                    foreach (var qId in queueItemIds)
                    {
                        if (!qId.IsEmpty() && seen.Add(qId))
                        {
                            initialOrder.Add(qId);
                        }
                    }
                }

                if (allSeriesEpisodeIdsProvider is not null)
                {
                    var allIds = allSeriesEpisodeIdsProvider();
                    var remaining = new List<Guid>();
                    foreach (var sId in allIds)
                    {
                        if (!sId.IsEmpty() && seen.Add(sId))
                        {
                            remaining.Add(sId);
                        }
                    }

                    remaining.Shuffle();
                    initialOrder.AddRange(remaining);
                }

                _entries[(userId, seriesId)] = new SeriesShuffleStateEntry
                {
                    UserId = userId,
                    SeriesId = seriesId,
                    SeriesPresentationUniqueKey = seriesPresentationUniqueKey,
                    SeasonId = null,
                    IsActive = true,
                    EpisodeIds = initialOrder,
                    LastEpisodeId = episodeId,
                    LastUpdatedUtc = DateTime.UtcNow
                };

                SaveStateToDiskLocked();
                return;
            }

            if (toggledOffInActiveSession)
            {
                if (_entries.Remove((userId, seriesId)))
                {
                    SaveStateToDiskLocked();
                }

                return;
            }

            if (_entries.TryGetValue((userId, seriesId), out var activeEntry) && activeEntry.IsActive && activeEntry.EpisodeIds.Count > 0)
            {
                if (!string.IsNullOrEmpty(seriesPresentationUniqueKey))
                {
                    activeEntry.SeriesPresentationUniqueKey = seriesPresentationUniqueKey;
                }

                if (activeEntry.EpisodeIds.Contains(episodeId))
                {
                    activeEntry.EpisodeIds = RotateToStartItem(activeEntry.EpisodeIds, episodeId);
                }

                activeEntry.LastEpisodeId = episodeId;
                activeEntry.LastUpdatedUtc = DateTime.UtcNow;
                SaveStateToDiskLocked();
            }
        }
    }

    /// <inheritdoc />
    public void OnEpisodePlaybackStopped(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        bool playedToCompletion)
    {
        if (userId.IsEmpty() || seriesId.IsEmpty() || episodeId.IsEmpty())
        {
            return;
        }

        lock (_syncLock)
        {
            if (!_entries.TryGetValue((userId, seriesId), out var entry) || !entry.IsActive || entry.EpisodeIds.Count == 0)
            {
                return;
            }

            if (entry.EpisodeIds.Contains(episodeId))
            {
                entry.EpisodeIds = RotateToStartItem(entry.EpisodeIds, episodeId);
            }

            if (playedToCompletion && entry.EpisodeIds.Count > 1 && entry.EpisodeIds[0].Equals(episodeId))
            {
                var nextOrder = new List<Guid>(entry.EpisodeIds.Count);
                for (var i = 1; i < entry.EpisodeIds.Count; i++)
                {
                    nextOrder.Add(entry.EpisodeIds[i]);
                }

                nextOrder.Add(episodeId);
                entry.EpisodeIds = nextOrder;
            }

            entry.LastEpisodeId = entry.EpisodeIds[0];
            entry.LastUpdatedUtc = DateTime.UtcNow;
            SaveStateToDiskLocked();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetActiveSeriesPresentationKeys(Guid userId, DateTime dateCutoff)
    {
        if (userId.IsEmpty())
        {
            return [];
        }

        lock (_syncLock)
        {
            var keys = new List<string>();
            foreach (var entry in _entries.Values)
            {
                if (entry.UserId.Equals(userId)
                    && entry.IsActive
                    && entry.EpisodeIds.Count > 0
                    && entry.LastUpdatedUtc >= dateCutoff
                    && !string.IsNullOrEmpty(entry.SeriesPresentationUniqueKey))
                {
                    keys.Add(entry.SeriesPresentationUniqueKey);
                }
            }

            return keys;
        }
    }

    /// <inheritdoc />
    public bool TryGetNextUpEpisode(
        User user,
        string seriesPresentationUniqueKey,
        Guid? seriesId,
        IReadOnlyList<BaseItem>? parentFolders,
        bool enableResumable,
        bool skipPlayed,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        out Episode? nextEpisode,
        out DateTime lastWatchedDate)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);

        nextEpisode = null;
        lastWatchedDate = DateTime.MinValue;

        SeriesShuffleStateEntry? matchedEntry = null;
        List<Guid>? episodeIdsSnapshot = null;

        lock (_syncLock)
        {
            if (seriesId is { } sId && !sId.IsEmpty() && _entries.TryGetValue((user.Id, sId), out var byId) && byId.IsActive && byId.EpisodeIds.Count > 0)
            {
                matchedEntry = byId;
            }
            else if (!string.IsNullOrEmpty(seriesPresentationUniqueKey))
            {
                foreach (var entry in _entries.Values)
                {
                    if (entry.UserId.Equals(user.Id)
                        && entry.IsActive
                        && entry.EpisodeIds.Count > 0
                        && string.Equals(entry.SeriesPresentationUniqueKey, seriesPresentationUniqueKey, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedEntry = entry;
                        break;
                    }
                }
            }

            if (matchedEntry is null)
            {
                return false;
            }

            episodeIdsSnapshot = [.. matchedEntry.EpisodeIds];
            lastWatchedDate = matchedEntry.LastUpdatedUtc;
        }

        Episode? firstPlayedFallback = null;
        foreach (var candidateId in episodeIdsSnapshot)
        {
            if (libraryManager.GetItemById<BaseItem>(candidateId, user) is not Episode candidateEpisode || (!string.IsNullOrEmpty(candidateEpisode.Path) && candidateEpisode.IsMissingEpisode))
            {
                continue;
            }

            if (parentFolders is { Count: > 0 }
                && candidateEpisode.GetTopParent()?.Id is { } topParentId
                && !parentFolders.Any(p => p.Id.Equals(topParentId)))
            {
                return false;
            }

            var isResumable = false;
            foreach (var version in candidateEpisode.GetAllVersions())
            {
                if (userDataManager.GetUserData(user, version)?.PlaybackPositionTicks > 0)
                {
                    isResumable = true;
                    break;
                }
            }

            if (skipPlayed && !isResumable && userDataManager.GetUserData(user, candidateEpisode)?.Played == true)
            {
                // Played: keep looking, but remember the first one for the all-played fallback.
                firstPlayedFallback ??= candidateEpisode;
                continue;
            }

            if (isResumable && !enableResumable)
            {
                nextEpisode = null;
                return true;
            }

            nextEpisode = candidateEpisode;
            return true;
        }

        if (firstPlayedFallback is not null)
        {
            // Every remaining episode is played: fall back to the first one in the queue.
            nextEpisode = firstPlayedFallback;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<BaseItem> FilterCandidateEpisodesBySeasonLocked(
        SeriesShuffleStateEntry entry,
        IReadOnlyList<BaseItem> episodes,
        Guid startItemId)
    {
        if (entry.SeasonId is not { } seasonId || seasonId.IsEmpty())
        {
            return episodes;
        }

        var seasonEpisodes = episodes
            .Where(e => e.ParentId.Equals(seasonId) || (e is Episode ep && ep.SeasonId.Equals(seasonId)))
            .ToList();

        if (seasonEpisodes.Count > 0 && seasonEpisodes.Any(e => e.Id.Equals(startItemId)))
        {
            return seasonEpisodes;
        }

        entry.SeasonId = null;
        return episodes;
    }

    private static Dictionary<Guid, BaseItem> BuildEpisodeLookup(IReadOnlyList<BaseItem> episodes)
    {
        var map = new Dictionary<Guid, BaseItem>(episodes.Count);
        foreach (var episode in episodes)
        {
            map.TryAdd(episode.Id, episode);
        }

        return map;
    }

    private static List<Guid> ReconcileEpisodeIds(
        IReadOnlyList<Guid> savedIds,
        IReadOnlyList<BaseItem> candidateEpisodes,
        Dictionary<Guid, BaseItem> episodeById)
    {
        var result = new List<Guid>(candidateEpisodes.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in savedIds)
        {
            if (episodeById.ContainsKey(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        var missing = new List<Guid>();
        foreach (var ep in candidateEpisodes)
        {
            if (seen.Add(ep.Id))
            {
                missing.Add(ep.Id);
            }
        }

        if (missing.Count > 0)
        {
            missing.Shuffle();
            result.AddRange(missing);
        }

        return result;
    }

    private static List<Guid> RotateToStartItem(List<Guid> ids, Guid startItemId)
    {
        var idx = ids.IndexOf(startItemId);
        if (idx <= 0)
        {
            return ids;
        }

        var rotated = new List<Guid>(ids.Count);
        for (var i = idx; i < ids.Count; i++)
        {
            rotated.Add(ids[i]);
        }

        for (var i = 0; i < idx; i++)
        {
            rotated.Add(ids[i]);
        }

        return rotated;
    }

    private void LoadStateFromDisk()
    {
        if (string.IsNullOrEmpty(_stateFilePath) || !File.Exists(_stateFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_stateFilePath);
            var persisted = JsonSerializer.Deserialize<List<SeriesShuffleStateEntry>>(json, _jsonOptions);
            if (persisted is null)
            {
                return;
            }

            foreach (var entry in persisted)
            {
                if (!entry.UserId.IsEmpty() && !entry.SeriesId.IsEmpty() && entry.EpisodeIds is { Count: > 0 })
                {
                    _entries[(entry.UserId, entry.SeriesId)] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load series shuffle state from {Path}", _stateFilePath);
        }
    }

    private void SaveStateToDiskLocked()
    {
        if (string.IsNullOrEmpty(_stateFilePath))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _stateFilePath + ".tmp";
            var list = _entries.Values.ToList();
            var json = JsonSerializer.Serialize(list, _jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save series shuffle state to {Path}", _stateFilePath);
        }
    }

    internal sealed class SeriesShuffleStateEntry
    {
        public Guid UserId { get; set; }

        public Guid SeriesId { get; set; }

        public string? SeriesPresentationUniqueKey { get; set; }

        public Guid? SeasonId { get; set; }

        public bool IsActive { get; set; }

        public List<Guid> EpisodeIds { get; set; } = [];

        public Guid? LastEpisodeId { get; set; }

        public DateTime LastUpdatedUtc { get; set; }
    }
}

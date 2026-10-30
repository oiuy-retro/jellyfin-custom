using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Session;

namespace MediaBrowser.Controller.TV;

/// <summary>
/// Manages per-user, per-series shuffle state and shuffled episode queues across playback sessions.
/// </summary>
public interface ISeriesShuffleManager
{
    /// <summary>
    /// Records an active shuffle order for a user and series.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <param name="seriesPresentationUniqueKey">Optional series presentation unique key.</param>
    /// <param name="seasonId">Optional season identifier if shuffle was started for a single season.</param>
    /// <param name="shuffledEpisodeIds">The shuffled episode identifiers.</param>
    void RecordSeriesShuffle(
        Guid userId,
        Guid seriesId,
        string? seriesPresentationUniqueKey,
        Guid? seasonId,
        IReadOnlyList<Guid> shuffledEpisodeIds);

    /// <summary>
    /// Clears any active shuffle state for a user and series.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    void ClearSeriesShuffle(Guid userId, Guid seriesId);

    /// <summary>
    /// Determines whether shuffle mode is currently active for a user and series.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns><c>true</c> if shuffle is active; otherwise, <c>false</c>.</returns>
    bool IsShuffleActive(Guid userId, Guid seriesId);

    /// <summary>
    /// Orders the episode list for resuming playback from a specific episode while preserving the active shuffle queue.
    /// Places <paramref name="startItemId"/> at index 0 followed by the remaining episodes in shuffled order.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <param name="episodes">The full list of candidate episodes for the series or season.</param>
    /// <param name="startItemId">The primary episode identifier being resumed or started.</param>
    /// <returns>The ordered list of episodes starting with <paramref name="startItemId"/>.</returns>
    IReadOnlyList<BaseItem> OrderEpisodesForResume(
        Guid userId,
        Guid seriesId,
        IReadOnlyList<BaseItem> episodes,
        Guid startItemId);

    /// <summary>
    /// Orders the episode list according to the saved shuffle sequence without rotating, for adjacency queries.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <param name="episodes">The full list of candidate episodes for the series or season.</param>
    /// <returns>The episodes ordered by the active shuffle sequence.</returns>
    IReadOnlyList<BaseItem> OrderEpisodesForAdjacency(
        Guid userId,
        Guid seriesId,
        IReadOnlyList<BaseItem> episodes);

    /// <summary>
    /// Updates shuffle state from a session playback start or progress report.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <param name="seriesPresentationUniqueKey">Optional series presentation unique key.</param>
    /// <param name="episodeId">The primary episode identifier currently playing.</param>
    /// <param name="playbackOrder">The reported playback order.</param>
    /// <param name="toggledOffInActiveSession">Whether the client explicitly switched from Shuffle to Default on the same playing item.</param>
    /// <param name="queueItemIds">Optional queue item identifiers reported by the client.</param>
    /// <param name="allSeriesEpisodeIdsProvider">Optional callback to load all episode identifiers of the series when activating shuffle from in-player controls.</param>
    void OnPlaybackReported(
        Guid userId,
        Guid seriesId,
        string? seriesPresentationUniqueKey,
        Guid episodeId,
        PlaybackOrder playbackOrder,
        bool toggledOffInActiveSession,
        IReadOnlyList<Guid>? queueItemIds,
        Func<IReadOnlyList<Guid>>? allSeriesEpisodeIdsProvider = null);

    /// <summary>
    /// Updates the shuffle queue position when episode playback stops.
    /// When <paramref name="playedToCompletion"/> is <c>true</c>, advances the finished episode to the end of the shuffle queue so the next random episode is queued first.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <param name="episodeId">The primary episode identifier that stopped.</param>
    /// <param name="playedToCompletion">Whether the episode was played to completion.</param>
    void OnEpisodePlaybackStopped(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        bool playedToCompletion);

    /// <summary>
    /// Gets the presentation unique keys of series with active shuffle state for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="dateCutoff">The minimum last-updated date.</param>
    /// <returns>The list of series presentation unique keys.</returns>
    IReadOnlyList<string> GetActiveSeriesPresentationKeys(Guid userId, DateTime dateCutoff);

    /// <summary>
    /// Attempts to resolve the Next Up episode for a series that has an active shuffle state.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="seriesPresentationUniqueKey">The series presentation unique key.</param>
    /// <param name="seriesId">Optional series identifier.</param>
    /// <param name="parentFolders">Optional parent library folders filter.</param>
    /// <param name="enableResumable">Whether resumable (in-progress) episodes should be included in Next Up.</param>
    /// <param name="skipPlayed">Whether played episodes are skipped. If all are played, the first valid episode is returned.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="nextEpisode">When returning <c>true</c>, the resolved next shuffled episode, or <c>null</c> if suppressed because it is resumable and <paramref name="enableResumable"/> is <c>false</c>.</param>
    /// <param name="lastWatchedDate">When returning <c>true</c>, the timestamp to sort the Next Up entry by.</param>
    /// <returns><c>true</c> if the series has an active shuffle state handled by this manager; otherwise, <c>false</c>.</returns>
    bool TryGetNextUpEpisode(
        User user,
        string seriesPresentationUniqueKey,
        Guid? seriesId,
        IReadOnlyList<BaseItem>? parentFolders,
        bool enableResumable,
        bool skipPlayed,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        out Episode? nextEpisode,
        out DateTime lastWatchedDate);
}

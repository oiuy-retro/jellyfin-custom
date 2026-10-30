using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace MediaBrowser.Controller.TV
{
    /// <summary>
    /// Helpers that keep already played items out of shuffled play queues.
    /// Music, music videos, audiobooks and books are never filtered.
    /// </summary>
    public static class ShuffleSkipPlayed
    {
        /// <summary>
        /// Determines whether a collection type belongs to content that is never filtered.
        /// </summary>
        /// <param name="collectionType">The collection type.</param>
        /// <returns><c>true</c> for music, music video and book libraries.</returns>
        public static bool IsExcludedCollectionType(CollectionType? collectionType)
            => collectionType is CollectionType.music or CollectionType.musicvideos or CollectionType.books;

        /// <summary>
        /// Determines whether an item kind belongs to content that is never filtered.
        /// </summary>
        /// <param name="kind">The item kind.</param>
        /// <returns><c>true</c> for music, music video and book kinds.</returns>
        public static bool IsExcludedKind(BaseItemKind kind)
            => kind is BaseItemKind.Audio or BaseItemKind.AudioBook or BaseItemKind.MusicAlbum
                or BaseItemKind.MusicArtist or BaseItemKind.MusicGenre or BaseItemKind.MusicVideo or BaseItemKind.Book;

        /// <summary>
        /// Determines whether an item is music-like content that is never filtered.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns><c>true</c> if the item is excluded from skip-played filtering.</returns>
        public static bool IsExcludedItem(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return item is Audio or MusicAlbum or MusicArtist or MusicGenre or MusicVideo or Book
                || (item is Playlist playlist && playlist.MediaType == MediaType.Audio)
                || item.MediaType == MediaType.Audio;
        }

        /// <summary>
        /// Determines whether an item should stay in a shuffled queue given the user's data.
        /// Unplayed and in-progress items stay.
        /// </summary>
        /// <param name="data">The user data, if any.</param>
        /// <returns><c>true</c> if the item should stay.</returns>
        public static bool ShouldKeep(UserItemData? data)
            => data is null || !data.Played || data.PlaybackPositionTicks > 0;

        /// <summary>
        /// Removes played items from a shuffled list. Excluded (music-like) items and the item
        /// with <paramref name="keepId"/> always stay. If nothing other than <paramref name="keepId"/>
        /// would remain, the original list is returned unchanged.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">The shuffled items.</param>
        /// <param name="user">The user.</param>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="keepId">An item id that always stays, or <see cref="Guid.Empty"/>.</param>
        /// <returns>The filtered list.</returns>
        public static IReadOnlyList<T> FilterPlayed<T>(IReadOnlyList<T> items, User user, IUserDataManager userDataManager, Guid keepId)
            where T : BaseItem
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(userDataManager);

            var filtered = items
                .Where(i => (!keepId.IsEmpty() && i.Id.Equals(keepId))
                    || IsExcludedItem(i)
                    || ShouldKeep(userDataManager.GetUserData(user, i)))
                .ToList();

            var hasOthers = filtered.Any(i => keepId.IsEmpty() || !i.Id.Equals(keepId));
            return hasOthers ? filtered : items;
        }
    }
}

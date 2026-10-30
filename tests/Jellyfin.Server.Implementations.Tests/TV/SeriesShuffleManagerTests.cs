using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Emby.Server.Implementations.TV;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

[Collection("LibraryManagerTests")]
public sealed class SeriesShuffleManagerTests : IDisposable
{
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;
    private readonly IFileSystem? _previousFileSystem;
    private readonly string _tempDir;

    public SeriesShuffleManagerTests()
    {
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousConfigurationManager = BaseItem.ConfigurationManager;
        _previousFileSystem = BaseItem.FileSystem;
        var fsMock = new Mock<IFileSystem>();
        fsMock.Setup(f => f.IsPathFile(It.IsAny<string>())).Returns(true);
        BaseItem.FileSystem = fsMock.Object;
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>();
        var configMock = new Mock<IServerConfigurationManager>();
        configMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        BaseItem.ConfigurationManager = configMock.Object;
        _tempDir = Path.Combine(Path.GetTempPath(), "jellyfin-shuffle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        BaseItem.FileSystem = _previousFileSystem!;
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void OrderEpisodesForResume_PreservesShuffleOrderWithResumedEpisodeFirst()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = CreateEpisodes(seriesId, seasonId, 6);
        var shuffledOrder = new List<Guid>
        {
            episodes[4].Id,
            episodes[1].Id,
            episodes[5].Id,
            episodes[0].Id,
            episodes[3].Id,
            episodes[2].Id
        };

        var manager = new SeriesShuffleManager();
        manager.RecordSeriesShuffle(userId, seriesId, "series-key-1", null, shuffledOrder);

        // User stops episodes[5] partway through and resumes it from Continue Watching.
        var resumedResult = manager.OrderEpisodesForResume(userId, seriesId, episodes, episodes[5].Id);

        var expectedOrder = new List<Guid>
        {
            episodes[5].Id,
            episodes[0].Id,
            episodes[3].Id,
            episodes[2].Id,
            episodes[4].Id,
            episodes[1].Id
        };

        Assert.Equal(expectedOrder, resumedResult.Select(e => e.Id).ToList());
    }

    [Fact]
    public void Persistence_SurvivesManagerReload()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.DataPath).Returns(_tempDir);

        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = CreateEpisodes(seriesId, seasonId, 4);
        var shuffledOrder = new List<Guid>
        {
            episodes[2].Id,
            episodes[0].Id,
            episodes[3].Id,
            episodes[1].Id
        };

        var manager1 = new SeriesShuffleManager(appPaths.Object);
        manager1.RecordSeriesShuffle(userId, seriesId, "series-key-persist", null, shuffledOrder);

        var manager2 = new SeriesShuffleManager(appPaths.Object);
        Assert.True(manager2.IsShuffleActive(userId, seriesId));

        var resumed = manager2.OrderEpisodesForResume(userId, seriesId, episodes, episodes[0].Id);
        Assert.Equal(
            new List<Guid> { episodes[0].Id, episodes[3].Id, episodes[1].Id, episodes[2].Id },
            resumed.Select(e => e.Id).ToList());
    }

    [Fact]
    public void OnEpisodePlaybackStopped_AdvancesQueueWhenPlayedToCompletion()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = CreateEpisodes(seriesId, seasonId, 4);
        var shuffledOrder = new List<Guid>
        {
            episodes[3].Id,
            episodes[1].Id,
            episodes[0].Id,
            episodes[2].Id
        };

        var manager = new SeriesShuffleManager();
        manager.RecordSeriesShuffle(userId, seriesId, "series-key-stop", null, shuffledOrder);

        // Stopping partway through keeps episodes[3] at the front of the queue.
        manager.OnEpisodePlaybackStopped(userId, seriesId, episodes[3].Id, playedToCompletion: false);

        var user = new User("test", "default", "default") { Id = userId };
        var libraryManager = new Mock<ILibraryManager>();
        foreach (var ep in episodes)
        {
            libraryManager.Setup(m => m.GetItemById<BaseItem>(ep.Id, user)).Returns(ep);
        }

        var userDataManager = new Mock<IUserDataManager>();
        userDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "ep", PlaybackPositionTicks = 0 });

        Assert.True(manager.TryGetNextUpEpisode(
            user,
            "series-key-stop",
            seriesId,
            null,
            enableResumable: true,
            skipPlayed: false,
            libraryManager.Object,
            userDataManager.Object,
            out var nextMidway,
            out _));
        Assert.NotNull(nextMidway);
        Assert.Equal(episodes[3].Id, nextMidway!.Id);

        // Finishing episodes[3] advances the queue so episodes[1] becomes Next Up.
        manager.OnEpisodePlaybackStopped(userId, seriesId, episodes[3].Id, playedToCompletion: true);

        Assert.True(manager.TryGetNextUpEpisode(
            user,
            "series-key-stop",
            seriesId,
            null,
            enableResumable: true,
            skipPlayed: false,
            libraryManager.Object,
            userDataManager.Object,
            out var nextAfterCompletion,
            out _));
        Assert.NotNull(nextAfterCompletion);
        Assert.Equal(episodes[1].Id, nextAfterCompletion!.Id);
    }

    [Fact]
    public void OnPlaybackReported_ActivatesOnShuffleAndClearsOnlyWhenToggledOffInSameSession()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = CreateEpisodes(seriesId, seasonId, 4);
        var allIds = episodes.Select(e => e.Id).ToList();

        var manager = new SeriesShuffleManager();
        Assert.False(manager.IsShuffleActive(userId, seriesId));

        // User enables shuffle from inside the player controls.
        manager.OnPlaybackReported(
            userId,
            seriesId,
            "series-key-player",
            episodes[2].Id,
            PlaybackOrder.Shuffle,
            toggledOffInActiveSession: false,
            new List<Guid> { episodes[2].Id, episodes[0].Id },
            () => allIds);

        Assert.True(manager.IsShuffleActive(userId, seriesId));

        // Resuming from the main menu reports PlaybackOrder.Default on a fresh session (toggledOffInActiveSession: false).
        // Shuffle must remain active.
        manager.OnPlaybackReported(
            userId,
            seriesId,
            "series-key-player",
            episodes[2].Id,
            PlaybackOrder.Default,
            toggledOffInActiveSession: false,
            null);

        Assert.True(manager.IsShuffleActive(userId, seriesId));

        // Explicitly toggling shuffle off during an active playback session clears shuffle state.
        manager.OnPlaybackReported(
            userId,
            seriesId,
            "series-key-player",
            episodes[2].Id,
            PlaybackOrder.Default,
            toggledOffInActiveSession: true,
            null);

        Assert.False(manager.IsShuffleActive(userId, seriesId));
    }

    [Fact]
    public void TvShowsController_GetEpisodes_ResumesInShuffleOrderAndClearsOnSequentialPlay()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var user = new User("test", "default", "default") { Id = userId };
        var episodes = CreateEpisodes(seriesId, seasonId, 5);

        var series = new Series
        {
            Id = seriesId,
            Name = "Test Series",
            Path = "/media/TestSeries",
            PresentationUniqueKey = "test-series-key"
        };
        var season = new Season
        {
            Id = seasonId,
            SeriesId = seriesId,
            ParentId = seriesId,
            IndexNumber = 1,
            Name = "Season 1",
            Path = "/media/TestSeries/Season 1",
            PresentationUniqueKey = "test-season-key"
        };

        var allItems = new List<BaseItem> { season };
        allItems.AddRange(episodes);

        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUserById(userId)).Returns(user);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetItemById(seriesId)).Returns(series);
        libraryManager.Setup(m => m.GetItemById<BaseItem>(seriesId, user)).Returns(series);
        libraryManager.Setup(m => m.GetItemById<Series>(seriesId, user)).Returns(series);
        libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(allItems);
        libraryManager
            .Setup(m => m.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User?>(), It.IsAny<IEnumerable<(ItemSortBy, SortOrder)>>()))
            .Returns((IEnumerable<BaseItem> items, User? _, IEnumerable<(ItemSortBy, SortOrder)> _) => items);
        libraryManager
            .Setup(m => m.Sort(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User?>(), It.IsAny<IEnumerable<ItemSortBy>>(), It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User? _, IEnumerable<ItemSortBy> _, SortOrder _) => items);
        foreach (var ep in episodes)
        {
            libraryManager.Setup(m => m.GetItemById<Video>(ep.Id)).Returns((Episode)ep);
        }

        BaseItem.LibraryManager = libraryManager.Object;

        var dtoService = new Mock<IDtoService>();
        dtoService
            .Setup(d => d.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), user, It.IsAny<BaseItem?>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<BaseItem> items, DtoOptions _, User _, BaseItem? _, bool _) =>
                items.Select(i => new BaseItemDto { Id = i.Id, Name = i.Name }).ToList());

        var shuffleManager = new SeriesShuffleManager();
        var controller = new TvShowsController(
            userManager.Object,
            libraryManager.Object,
            dtoService.Object,
            Mock.Of<ITVSeriesManager>(),
            shuffleManager);

        var claims = new[]
        {
            new Claim(InternalClaimTypes.UserId, userId.ToString("N")),
            new Claim(InternalClaimTypes.IsApiKey, "false")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        // 1. Start shuffle playback on the series.
        var shuffleResponse = controller.GetEpisodes(
            seriesId,
            userId,
            [ItemFields.MediaSources],
            season: null,
            seasonId: null,
            isMissing: null,
            adjacentTo: null,
            startItemId: null,
            startIndex: null,
            limit: 100,
            enableImages: null,
            imageTypeLimit: null,
            enableImageTypes: [],
            enableUserData: true,
            sortBy: ItemSortBy.Random);

        var shuffledItems = shuffleResponse.Value!.Items;
        Assert.Equal(5, shuffledItems.Count);
        Assert.True(shuffleManager.IsShuffleActive(userId, seriesId));

        // 2. Browsing the series without MediaSources (e.g. season/episode details UI) does not clear shuffle.
        _ = controller.GetEpisodes(
            seriesId,
            userId,
            [ItemFields.Overview],
            season: null,
            seasonId: null,
            isMissing: null,
            adjacentTo: null,
            startItemId: null,
            startIndex: null,
            limit: null,
            enableImages: null,
            imageTypeLimit: null,
            enableImageTypes: [],
            enableUserData: true,
            sortBy: null);

        Assert.True(shuffleManager.IsShuffleActive(userId, seriesId));

        // 3. Resuming from Continue Watching / Next Up with startItemId and sortBy=null returns the resumed
        //    episode at index 0 followed by the remaining episodes in the saved shuffled order.
        var targetEpisodeId = shuffledItems[2].Id;
        var resumeResponse = controller.GetEpisodes(
            seriesId,
            userId,
            [ItemFields.MediaSources],
            season: null,
            seasonId: null,
            isMissing: null,
            adjacentTo: null,
            startItemId: targetEpisodeId,
            startIndex: null,
            limit: 100,
            enableImages: null,
            imageTypeLimit: null,
            enableImageTypes: [],
            enableUserData: true,
            sortBy: null);

        var expectedResumeIds = new List<Guid>
        {
            shuffledItems[2].Id,
            shuffledItems[3].Id,
            shuffledItems[4].Id,
            shuffledItems[0].Id,
            shuffledItems[1].Id
        };
        Assert.Equal(expectedResumeIds, resumeResponse.Value!.Items.Select(i => i.Id).ToList());

        // 4. Starting explicit sequential playback (sortBy=SortName) clears shuffle state.
        _ = controller.GetEpisodes(
            seriesId,
            userId,
            [ItemFields.MediaSources],
            season: null,
            seasonId: null,
            isMissing: null,
            adjacentTo: null,
            startItemId: null,
            startIndex: null,
            limit: 100,
            enableImages: null,
            imageTypeLimit: null,
            enableImageTypes: [],
            enableUserData: true,
            sortBy: ItemSortBy.SortName);

        Assert.False(shuffleManager.IsShuffleActive(userId, seriesId));
    }

    [Fact]
    public void FilterPlayed_RemovesPlayedKeepsInProgressStartItemAndMusic()
    {
        var user = new User("test", "default", "default") { Id = Guid.NewGuid() };
        var episodes = CreateEpisodes(Guid.NewGuid(), Guid.NewGuid(), 5);
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Name = "Song" };
        var items = new List<BaseItem>(episodes) { song };

        var data = new Dictionary<Guid, UserItemData>
        {
            [episodes[0].Id] = new UserItemData { Key = "0", Played = true },
            [episodes[1].Id] = new UserItemData { Key = "1", Played = true, PlaybackPositionTicks = 100 },
            [episodes[2].Id] = new UserItemData { Key = "2", Played = false, PlaybackPositionTicks = 100 },
            [episodes[3].Id] = new UserItemData { Key = "3", Played = true },
            [episodes[4].Id] = new UserItemData { Key = "4", Played = false },
            [song.Id] = new UserItemData { Key = "s", Played = true },
        };
        var userDataManager = new Mock<IUserDataManager>();
        userDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem i) => data[i.Id]);

        var result = ShuffleSkipPlayed.FilterPlayed(items, user, userDataManager.Object, episodes[3].Id);

        Assert.Equal(
            new List<Guid> { episodes[1].Id, episodes[2].Id, episodes[3].Id, episodes[4].Id, song.Id },
            result.Select(i => i.Id).ToList());
    }

    [Fact]
    public void FilterPlayed_ReturnsAllItemsWhenEverythingIsPlayed()
    {
        var user = new User("test", "default", "default") { Id = Guid.NewGuid() };
        var episodes = CreateEpisodes(Guid.NewGuid(), Guid.NewGuid(), 3);
        var userDataManager = new Mock<IUserDataManager>();
        userDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "x", Played = true });

        var result = ShuffleSkipPlayed.FilterPlayed(episodes, user, userDataManager.Object, episodes[1].Id);

        Assert.Equal(episodes.Select(i => i.Id).ToList(), result.Select(i => i.Id).ToList());
    }

    [Fact]
    public void TryGetNextUpEpisode_SkipsPlayedAndFallsBackWhenAllPlayed()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodes = CreateEpisodes(seriesId, Guid.NewGuid(), 4);
        var manager = new SeriesShuffleManager();
        manager.RecordSeriesShuffle(
            userId,
            seriesId,
            "series-key-skip",
            null,
            new List<Guid> { episodes[3].Id, episodes[1].Id, episodes[0].Id, episodes[2].Id });

        var user = new User("test", "default", "default") { Id = userId };
        var libraryManager = new Mock<ILibraryManager>();
        foreach (var ep in episodes)
        {
            libraryManager.Setup(m => m.GetItemById<BaseItem>(ep.Id, user)).Returns(ep);
        }

        var played = new HashSet<Guid> { episodes[3].Id, episodes[1].Id };
        var userDataManager = new Mock<IUserDataManager>();
        userDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem i) => new UserItemData { Key = "k", Played = played.Contains(i.Id) });

        Assert.True(manager.TryGetNextUpEpisode(user, "series-key-skip", seriesId, null, true, true, libraryManager.Object, userDataManager.Object, out var next, out _));
        Assert.Equal(episodes[0].Id, next!.Id);

        played.UnionWith(episodes.Select(e => e.Id));
        Assert.True(manager.TryGetNextUpEpisode(user, "series-key-skip", seriesId, null, true, true, libraryManager.Object, userDataManager.Object, out var fallback, out _));
        Assert.Equal(episodes[3].Id, fallback!.Id);
    }

    private static List<BaseItem> CreateEpisodes(Guid seriesId, Guid seasonId, int count)
    {
        var list = new List<BaseItem>(count);
        for (var i = 1; i <= count; i++)
        {
            list.Add(new Episode
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                SeasonId = seasonId,
                ParentId = seasonId,
                ParentIndexNumber = 1,
                IndexNumber = i,
                Name = $"Episode {i}",
                Path = $"/media/TestSeries/Season 1/S01E{i:00}.mkv"
            });
        }

        return list;
    }
}

using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Tests for <see cref="SessionHistoryStore"/>: persistence, trimming, filtering,
/// aggregated stats, atomic write, and corruption fallback.
/// </summary>
public class SessionHistoryStoreTests
{
    [Fact]
    public void Add_PersistsToFile()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("history.json");
        var store = new SessionHistoryStore(path);

        var session = new GameSession
        {
            GameName = "Test",
            GameId = "test",
            StartTime = DateTime.UtcNow.AddMinutes(-10),
            EndTime = DateTime.UtcNow,
            Duration = TimeSpan.FromMinutes(10)
        };
        store.Add(session);

        var store2 = new SessionHistoryStore(path);
        store2.Load();
        var all = store2.GetAll();
        Assert.Single(all);
        Assert.Equal("Test", all[0].GameName);
    }

    [Fact]
    public void Add_EnforcesMaxLimit_TrimsOldest()
    {
        using var temp = new TempPath();
        var store = new SessionHistoryStore(temp.GetFile("history.json"));

        // Add 110 sessions (limit is 100)
        for (int i = 0; i < 110; i++)
        {
            store.Add(new GameSession
            {
                GameName = $"Game{i}",
                GameId = $"g{i}",
                StartTime = DateTime.UtcNow.AddMinutes(-i * 10),
                EndTime = DateTime.UtcNow.AddMinutes(-i * 10 + 5),
                Duration = TimeSpan.FromMinutes(5)
            });
        }

        Assert.Equal(100, store.GetAll().Count);
    }

    [Fact]
    public void Save_IsAtomic_NoTmpLeftover()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("history.json");
        var store = new SessionHistoryStore(path);

        store.Add(new GameSession
        {
            GameName = "T",
            GameId = "t",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow,
            Duration = TimeSpan.Zero
        });

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetForGame_FiltersByGameId()
    {
        using var temp = new TempPath();
        var store = new SessionHistoryStore(temp.GetFile("history.json"));

        store.Add(new GameSession { GameId = "g1", GameName = "Game1", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow });
        store.Add(new GameSession { GameId = "g2", GameName = "Game2", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow });
        store.Add(new GameSession { GameId = "g1", GameName = "Game1", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow });

        Assert.Equal(2, store.GetForGame("g1").Count);
        Assert.Single(store.GetForGame("g2"));
    }

    [Fact]
    public void GetStatsForGame_ReturnsNullWhenNoSessions()
    {
        using var temp = new TempPath();
        var store = new SessionHistoryStore(temp.GetFile("history.json"));

        Assert.Null(store.GetStatsForGame("nonexistent"));
    }

    [Fact]
    public void GetStatsForGame_ComputesCorrectAggregates()
    {
        using var temp = new TempPath();
        var store = new SessionHistoryStore(temp.GetFile("history.json"));

        var start = DateTime.UtcNow.AddHours(-2);
        store.Add(new GameSession
        {
            GameId = "g",
            GameName = "G",
            StartTime = start,
            EndTime = start.AddMinutes(30),
            Duration = TimeSpan.FromMinutes(30),
            AvgDpcDuring = 1000,
            OptimizationsApplied = 10
        });
        store.Add(new GameSession
        {
            GameId = "g",
            GameName = "G",
            StartTime = start.AddMinutes(45),
            EndTime = start.AddMinutes(60),
            Duration = TimeSpan.FromMinutes(15),
            AvgDpcDuring = 2000,
            OptimizationsApplied = 12
        });

        var stats = store.GetStatsForGame("g");
        Assert.NotNull(stats);
        Assert.Equal(2, stats!.SessionCount);
        Assert.Equal(TimeSpan.FromMinutes(45), stats.TotalPlayTime);
        Assert.Equal(1500, stats.AvgDpcLatency);  // (1000+2000)/2
        Assert.Equal(1000, stats.BestDpcLatency);
        Assert.Equal(11, stats.AvgOptimizationsApplied);
    }

    [Fact]
    public void Load_CorruptJson_FallsBackToEmpty()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("history.json");
        File.WriteAllText(path, "{ not valid json");

        var store = new SessionHistoryStore(path);
        store.Load();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void GetRecent_ReturnsOrderedByStartTimeDescending()
    {
        using var temp = new TempPath();
        var store = new SessionHistoryStore(temp.GetFile("history.json"));

        var baseTime = DateTime.UtcNow;
        store.Add(new GameSession { GameId = "g1", GameName = "G1", StartTime = baseTime.AddHours(-3), EndTime = baseTime });
        store.Add(new GameSession { GameId = "g2", GameName = "G2", StartTime = baseTime.AddHours(-1), EndTime = baseTime });
        store.Add(new GameSession { GameId = "g3", GameName = "G3", StartTime = baseTime.AddHours(-2), EndTime = baseTime });

        var recent = store.GetRecent(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("G2", recent[0].GameName);  // most recent
        Assert.Equal("G3", recent[1].GameName);
    }
}

using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

public class TimeManagementTests
{
    [Fact]
    public void OptimumNeverExceedsMaximum()
    {
        var tm = new TimeManagement();
        tm.Init(myTime: 300_000, myInc: 0, movesToGo: 0, ply: 10);

        Assert.True(tm.OptimumTime > 0);
        Assert.True(tm.OptimumTime <= tm.MaximumTime);
    }

    [Fact]
    public void MoreTimeLeftGivesMoreOptimumTime()
    {
        // timeman.cpp:46-142 — a parità di ply/incremento, più tempo disponibile deve tradursi in
        // un budget maggiore per la mossa (non necessariamente proporzionale).
        var tmShort = new TimeManagement();
        tmShort.Init(myTime: 10_000, myInc: 0, movesToGo: 0, ply: 10);

        var tmLong = new TimeManagement();
        tmLong.Init(myTime: 300_000, myInc: 0, movesToGo: 0, ply: 10);

        Assert.True(tmLong.OptimumTime > tmShort.OptimumTime);
    }

    [Fact]
    public void NoTimeGivesUnboundedBudget()
    {
        // "go depth N"/"go infinite" — myTime=0 (nessun orologio) non deve limitare artificialmente
        // il tempo (timeman.cpp:61-65).
        var tm = new TimeManagement();
        tm.Init(myTime: 0, myInc: 0, movesToGo: 0, ply: 0);

        Assert.True(tm.OptimumTime > 1_000_000);
        Assert.True(tm.MaximumTime > 1_000_000);
    }

    [Fact]
    public void MovesToGoModeStaysWithinTimeLeft()
    {
        // "x mosse in y secondi" — con un orizzonte esplicito, il budget non deve mai superare il
        // tempo effettivamente rimasto.
        var tm = new TimeManagement();
        tm.Init(myTime: 60_000, myInc: 0, movesToGo: 20, ply: 5);

        Assert.True(tm.OptimumTime > 0);
        Assert.True(tm.OptimumTime < 60_000);
    }
}

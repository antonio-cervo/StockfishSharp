using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

public class TimeManagementTests
{
    /// <summary>Regressione sul TEMPO REALE consumato da una ricerca vera (non solo sulla formula
    /// di TimeManagement): con un orologio dato, la ricerca non deve mai divorarne una frazione
    /// pericolosa. Copre insieme i tre meccanismi che ci hanno fatto perdere partite a tempo
    /// scaduto e che sono stati corretti il 2026-09-07:
    ///  - il budget adattivo che si gonfiava (ora limitato a <c>MaxBudgetOverOptimum</c>),
    ///  - una singola iterazione che sforava il budget perche' il controllo avveniva solo FRA le
    ///    iterazioni (ora c'e' la scadenza morbida che interrompe anche a meta'),
    ///  - il tetto assoluto della fonte, che concede fino a 0,81 volte l'orologio residuo.
    /// La soglia e' volutamente generosa (60%): serve a intercettare una regressione catastrofica
    /// (una mossa che consuma piu' dell'orologio, come accadeva), non a tarare i margini. Misurato
    /// per confronto: su queste stesse condizioni l'oracolo Stockfish 19 arriva all'81%.</summary>
    [Theory]
    [InlineData("r1bq1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP1B1PPP/R2QKB1R w KQ - 0 8")]
    [InlineData("4rr2/pp1q1ppk/2np3p/b1pn3b/2P1PP2/1P1P4/PBN2QBP/R4R1K w - - 0 19")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11")]
    public void RealSearchNeverEatsADangerousShareOfTheClock(string fen)
    {
        const long WTime = 3_000;   // orologio corto: il caso in cui si perde per tempo
        const long WInc = 1_000;

        Attacks.EnsureInitialized();
        Position.Init();

        var pos = new Position();
        pos.Set(fen, false);

        var tm = new TimeManagement();
        tm.Init(myTime: WTime, myInc: WInc, movesToGo: 0, ply: pos.GamePly);

        var pool = new SearchThreadPool();
        pool.SetThreadCount(1);
        pool.Resize(16);
        pool.NewGame();

        // Un giro a vuoto: il primo "go" di un processo .NET paga la compilazione JIT dei metodi
        // caldi, che non fa parte del tempo di ricerca vero (vedi il riscaldamento in Program.cs).
        pool.Search_(pos, 6, TimeSpan.FromSeconds(5), CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        pool.Search_(pos, 30, TimeSpan.FromMilliseconds(tm.MaximumTime), CancellationToken.None,
            optimumMs: tm.OptimumTime);
        sw.Stop();

        double share = sw.Elapsed.TotalMilliseconds / WTime;
        Assert.True(share < 0.60,
            $"La ricerca ha consumato {sw.Elapsed.TotalMilliseconds:F0}ms su un orologio di {WTime}ms "
            + $"({share:P0}): oltre la soglia di sicurezza del 60%.");
    }

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

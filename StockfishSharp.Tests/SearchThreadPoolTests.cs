using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica SearchThreadPool (C1, Lazy SMP): con 1 thread deve comportarsi esattamente
/// come Search diretta (nessuna regressione per il percorso a thread singolo, ancora il default);
/// con più thread non deve mai produrre una mossa illegale né un conteggio nodi minore di un
/// singolo thread nello stesso tempo — il multi-threading è intrinsecamente non deterministico
/// (timing reale), quindi non è verificabile bit-esatto come il resto del motore: qui si verificano
/// proprietà strutturali (legalità, monotonicità dei nodi) invece del valore esatto.</summary>
public class SearchThreadPoolTests
{
    public SearchThreadPoolTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Position MakePosition(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    [Fact]
    public void SingleThreadPoolMatchesDirectSearch()
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

        var direct = new Search();
        direct.Resize(16);
        direct.NewGame();
        var directResult = direct.Search_(MakePosition(fen), 6, TimeSpan.FromSeconds(30));

        var pool = new SearchThreadPool();
        pool.SetThreadCount(1);
        pool.Resize(16);
        pool.NewGame();
        var poolResult = pool.Search_(MakePosition(fen), 6, TimeSpan.FromSeconds(30));

        Assert.Equal(directResult.BestMove, poolResult.BestMove);
        Assert.Equal(directResult.ScoreCp, poolResult.ScoreCp);
        Assert.Equal(directResult.Nodes, poolResult.Nodes);
        Assert.Equal(directResult.Depth, poolResult.Depth);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void MultiThreadPoolProducesLegalMoveAndMoreNodes(int threadCount)
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        var budget = TimeSpan.FromSeconds(2);

        var single = new SearchThreadPool();
        single.SetThreadCount(1);
        single.Resize(16);
        single.NewGame();
        var singleResult = single.Search_(MakePosition(fen), 30, budget);

        var multi = new SearchThreadPool();
        multi.SetThreadCount(threadCount);
        multi.Resize(16);
        multi.NewGame();
        var multiResult = multi.Search_(MakePosition(fen), 30, budget);

        Assert.NotNull(multiResult.BestMove);

        var pos = MakePosition(fen);
        var legal = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, legal);
        Assert.Contains(multiResult.BestMove!.Value, legal);

        // I nodi totali (somma di tutti i thread) devono essere maggiori di un singolo thread
        // nello stesso budget di tempo — il beneficio atteso di Lazy SMP.
        Assert.True(multiResult.Nodes > singleResult.Nodes,
            $"nodi multi-thread ({multiResult.Nodes}) non maggiori di quelli single-thread ({singleResult.Nodes})");
    }

    [Fact]
    public void NewGameWithoutExplicitThreadCountDefaultsToOne()
    {
        var pool = new SearchThreadPool();
        pool.Resize(16);
        pool.NewGame(); // non deve lanciare, deve creare implicitamente 1 thread
        Assert.Equal(1, pool.ThreadCount);
    }
}

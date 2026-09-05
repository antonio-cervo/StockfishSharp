using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

public class NnueFeatureTests
{
    public NnueFeatureTests()
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

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    public void HalfKaIndicesAreInRange(string fen)
    {
        var pos = MakePosition(fen);
        foreach (var perspective in new[] { Color.White, Color.Black })
        {
            var active = new List<int>();
            HalfKAv2Hm.AppendActiveIndices(perspective, pos, active);
            Assert.NotEmpty(active);
            foreach (int idx in active)
            {
                Assert.InRange(idx, 0, HalfKAv2Hm.Dimensions - 1);
            }
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void ThreatIndicesAreInRange(string fen)
    {
        var pos = MakePosition(fen);
        foreach (var perspective in new[] { Color.White, Color.Black })
        {
            var active = new List<int>();
            FullThreats.AppendActiveIndices(perspective, pos, active);
            foreach (int idx in active)
            {
                Assert.InRange(idx, 0, FullThreats.Dimensions - 1);
            }
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void PawnPairIndicesAreInRange(string fen)
    {
        var pos = MakePosition(fen);
        foreach (var perspective in new[] { Color.White, Color.Black })
        {
            var active = new List<int>();
            Pp3Wide.AppendActiveIndices(perspective, pos, active);
            foreach (int idx in active)
            {
                Assert.InRange(idx, Pp3Wide.IndexBase, Pp3Wide.IndexBase + Pp3Wide.Dimensions - 1);
            }
        }
    }

    [Fact]
    public void StartingPositionHasExpectedPawnPairCount()
    {
        // PawnPairBB(s) copre l'INTERA banda di 3 colonne (file-1, file, file+1) su TUTTE le
        // traverse 2-7 (bitboard.h:130-144), non solo le case fisicamente vicine a "s" — quindi
        // in posizione iniziale un pedone in 2a traversa forma comunque una "coppia" con un
        // pedone in 7a traversa che sta nella stessa banda di colonne, anche se lontanissimi sulla
        // scacchiera. Conteggio a mano: coppie bianco-bianco = coppie di colonne adiacenti fra le
        // 8 colonne = 7; nero-nero = 7 allo stesso modo; bianco-nero = per ogni colonna bianca,
        // le colonne nere nella banda (2 se colonna di bordo, 3 altrimenti) = 2*2 + 6*3 = 22.
        // Totale 7+7+22 = 36.
        var pos = MakePosition("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var active = new List<int>();
        Pp3Wide.AppendActiveIndices(Color.White, pos, active);
        Assert.Equal(36, active.Count);
    }
}

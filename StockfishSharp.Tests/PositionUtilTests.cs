using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica Position.Flip/PosIsOk/MaterialKeyIsOk (A5, position.cpp:1573-1668) — non
/// consumate dalla logica di ricerca/perft, solo utilità di debug, ma verificate con lo stesso
/// standard del resto del progetto: proprietà indipendenti dall'implementazione stessa.</summary>
public class PositionUtilTests
{
    public PositionUtilTests()
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
    [InlineData("2K5/p7/7P/5pR1/8/5k2/r7/8 w - - 0 1")] // en passant non presente ma finale asimmetrico
    public void PosIsOkOnStandardPositions(string fen)
    {
        var pos = MakePosition(fen);
        Assert.True(pos.PosIsOk());
        Assert.True(pos.MaterialKeyIsOk());
    }

    /// <summary>Flip è involutivo: specchiare due volte deve riportare esattamente alla FEN
    /// originale (a parte l'eventuale differenza fra "-" e assenza di campi, non applicabile qui).</summary>
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    public void FlipTwiceReturnsOriginalPosition(string fen)
    {
        var pos = MakePosition(fen);
        string original = pos.Fen();

        pos.Flip();
        Assert.True(pos.PosIsOk());
        pos.Flip();

        Assert.Equal(original, pos.Fen());
    }

    /// <summary>Una posizione specchiata (colori scambiati, board ruotata) ha esattamente lo
    /// stesso numero di mosse legali della originale — proprietà scacchistica indipendente
    /// dall'implementazione di Flip, verificata con lo stesso motore di perft già validato contro
    /// i valori pubblicati.</summary>
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)]
    public void FlippedPositionHasSamePerft(string fen, int depth)
    {
        var pos = MakePosition(fen);
        long before = Perft.Run(pos, depth);

        pos.Flip();
        long after = Perft.Run(pos, depth);

        Assert.Equal(before, after);
    }
}

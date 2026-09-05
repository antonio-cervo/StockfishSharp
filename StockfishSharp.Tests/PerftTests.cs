using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica Position+MoveGen contro i valori di perft pubblicati e universalmente noti
/// nella comunità chess-programming (non specifici di Stockfish — sono lo standard oggettivo di
/// correttezza per QUALUNQUE move generator, usati per verificare motori scritti da zero ben
/// prima che Stockfish esistesse). Le profondità testate sono scelte per restare entro qualche
/// secondo con l'implementazione attuale (List&lt;Move&gt;, non ancora ottimizzata) — profondità
/// più alte sulle stesse posizioni restano un buon obiettivo per quando la generazione mosse sarà
/// più veloce.</summary>
public class PerftTests
{
    public PerftTests()
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
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    [InlineData(4, 197281)]
    public void StartingPosition(int depth, long expected)
    {
        var pos = MakePosition("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, 2039)]
    [InlineData(3, 97862)]
    public void Kiwipete(int depth, long expected)
    {
        var pos = MakePosition("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 191)]
    [InlineData(3, 2812)]
    [InlineData(4, 43238)]
    public void Position3EndgameRookAndKingsPawns(int depth, long expected)
    {
        var pos = MakePosition("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 264)]
    [InlineData(3, 9467)]
    public void Position4Chess960LikeCastlingAndPromotions(int depth, long expected)
    {
        var pos = MakePosition("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Theory]
    [InlineData(1, 44)]
    [InlineData(2, 1486)]
    [InlineData(3, 62379)]
    public void Position5(int depth, long expected)
    {
        var pos = MakePosition("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Theory]
    [InlineData(1, 46)]
    [InlineData(2, 2079)]
    [InlineData(3, 89890)]
    public void Position6(int depth, long expected)
    {
        var pos = MakePosition("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10");
        Assert.Equal(expected, Perft.Run(pos, depth));
    }

    [Fact]
    public void FenRoundTripsThroughSet()
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        var pos = MakePosition(fen);
        Assert.Equal(fen, pos.Fen());
    }

    [Fact]
    public void DoMoveUndoMoveRestoresFen()
    {
        const string fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        var pos = MakePosition(fen);
        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        foreach (var m in moves)
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            pos.UndoMove(m);
            Assert.Equal(fen, pos.Fen());
        }
    }
}

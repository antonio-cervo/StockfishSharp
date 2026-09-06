using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica indipendente di DirtyPiece/DirtyPawnPairs (types.h:296-306,347-350): per ogni
/// mossa legale, applicare i campi di DirtyPiece a una copia della board "prima" deve produrre
/// esattamente la board "dopo" osservata direttamente — senza replicare la logica di DoMove,
/// solo il significato dichiarato dei campi (rimuovi RemovePc da RemoveSq, sposta Pc da From a To,
/// aggiungi AddPc su AddSq). DirtyPawnPairs è confrontato contro le bitboard dei pedoni lette
/// direttamente prima/dopo.</summary>
public class DirtyPieceTests
{
    public DirtyPieceTests()
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

    private static Piece[] SnapshotBoard(Position pos)
    {
        var board = new Piece[64];
        for (Square s = Square.A1; s <= Square.H8; s++)
            board[(byte)s] = pos.PieceOn(s);
        return board;
    }

    /// <summary>Significato dichiarato dei campi di DirtyPiece — non la logica di DoMove.</summary>
    private static void Apply(Piece[] board, DirtyPiece dp)
    {
        if (dp.RemoveSq != Square.None) board[(byte)dp.RemoveSq] = Piece.None;
        board[(byte)dp.From] = Piece.None;
        if (dp.To != Square.None) board[(byte)dp.To] = dp.Pc;
        if (dp.AddSq != Square.None) board[(byte)dp.AddSq] = dp.AddPc;
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")] // mosse/spinte doppie normali
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")] // catture, arrocco
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")] // promozioni, en passant, arrocco
    [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8")] // promozione con cattura
    public void DirtyPieceMatchesResultingBoard(string fen)
    {
        var pos = MakePosition(fen);
        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);
        Assert.NotEmpty(moves);

        foreach (var m in moves)
        {
            var before = SnapshotBoard(pos);
            ulong pawnsBeforeWhite = pos.Pieces(Color.White, PieceType.Pawn);
            ulong pawnsBeforeBlack = pos.Pieces(Color.Black, PieceType.Pawn);

            var dp = new DirtyPiece();
            var dpps = new DirtyPawnPairs();
            var st = new StateInfo();
            pos.DoMove(m, st, pos.GivesCheck(m), dirtyPiece: dp, dirtyPawnPairs: dpps);

            var predicted = (Piece[])before.Clone();
            Apply(predicted, dp);
            var actual = SnapshotBoard(pos);

            Assert.True(predicted.AsSpan().SequenceEqual(actual),
                $"FEN={fen}, mossa={m.FromSq}{m.ToSq}: la board prevista da DirtyPiece non combacia con quella reale.");

            Assert.Equal(pawnsBeforeWhite, dpps.Before[(byte)Color.White]);
            Assert.Equal(pawnsBeforeBlack, dpps.Before[(byte)Color.Black]);
            Assert.Equal(pos.Pieces(Color.White, PieceType.Pawn), dpps.After[(byte)Color.White]);
            Assert.Equal(pos.Pieces(Color.Black, PieceType.Pawn), dpps.After[(byte)Color.Black]);

            pos.UndoMove(m);
        }
    }
}

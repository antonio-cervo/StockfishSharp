using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica indipendente di AccumulatorStack (N9, aggiornamento incrementale
/// dell'accumulatore NNUE): per ogni mossa legale raggiunta durante una piccola perft, l'
/// accumulatore mantenuto incrementalmente tramite Push/Evaluate deve essere bit-esatto (stessi
/// interi, non solo la stessa valutazione finale) contro NnueAccumulator.ComputeFromScratch
/// calcolato indipendentemente sulla stessa posizione — stesso principio già usato per
/// l'accumulatore "da zero" negli oracoli N1-N8.</summary>
public class NnueIncrementalTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public NnueIncrementalTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static void AssertAccumulatorMatches(NnueAccumulator incremental, NnueAccumulator scratch, string context)
    {
        foreach (var c in new[] { Color.White, Color.Black })
        {
            int p = (byte)c;
            Assert.True(incremental.Accumulation[p].AsSpan().SequenceEqual(scratch.Accumulation[p]),
                $"{context}: accumulation[{c}] non combacia con il ricalcolo da zero.");
            Assert.True(incremental.PsqtAccumulation[p].AsSpan().SequenceEqual(scratch.PsqtAccumulation[p]),
                $"{context}: psqtAccumulation[{c}] non combacia con il ricalcolo da zero.");
        }
    }

    private static void Verify(Position pos, NnueNetwork net, AccumulatorStack stack, int depth, string fen)
    {
        if (depth == 0) return;

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        foreach (var m in moves)
        {
            var frame = stack.Push();
            var st = new StateInfo();
            bool givesCheck = pos.GivesCheck(m);
            pos.DoMove(m, st, givesCheck, frame.DirtyThreats, frame.DirtyPiece, frame.DirtyPawnPairs);

            stack.Evaluate(pos, net);
            var scratch = NnueAccumulator.ComputeFromScratch(net, pos);
            AssertAccumulatorMatches(stack.Latest, scratch, $"FEN={fen}, mossa={m.FromSq}{m.ToSq}");

            Verify(pos, net, stack, depth - 1, fen);

            pos.UndoMove(m);
            stack.Pop();
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 2)]
    public void IncrementalAccumulatorMatchesFromScratch(string fen, int depth)
    {
        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set(fen, isChess960: false);

        var stack = new AccumulatorStack();
        stack.Reset();

        // Il frame radice (ply 0) non è mai "pushato" — va calcolato una volta prima di iniziare,
        // esattamente come farebbe la prima chiamata a Evaluate() nella ricerca reale.
        stack.Evaluate(pos, net);
        var rootScratch = NnueAccumulator.ComputeFromScratch(net, pos);
        AssertAccumulatorMatches(stack.Latest, rootScratch, $"FEN={fen}, radice");

        Verify(pos, net, stack, depth, fen);
    }

    /// <summary>Il caso che esercita davvero "requires_refresh": una mossa di re (qui un arrocco,
    /// che sposta il re) deve forzare un refresh completo per quella prospettiva, non un
    /// aggiornamento incrementale — verificato indirettamente (se il refresh non scattasse, gli
    /// indici HalfKA/FullThreats userebbero ancora la vecchia casa del re e l'accumulatore
    /// risultante NON combacerebbe col ricalcolo da zero, facendo fallire lo stesso confronto di
    /// sopra sulla posizione "Kiwipete", che include l'arrocco fra le mosse legali).</summary>
    [Fact]
    public void KingMoveTriggersRefreshNotIncrementalCorruption()
    {
        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", isChess960: false);

        var stack = new AccumulatorStack();
        stack.Reset();
        stack.Evaluate(pos, net);

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);
        Move castling = moves.First(m => m.TypeOf == MoveType.Castling);

        var frame = stack.Push();
        var st = new StateInfo();
        pos.DoMove(castling, st, pos.GivesCheck(castling), frame.DirtyThreats, frame.DirtyPiece, frame.DirtyPawnPairs);
        stack.Evaluate(pos, net);

        var scratch = NnueAccumulator.ComputeFromScratch(net, pos);
        AssertAccumulatorMatches(stack.Latest, scratch, "dopo arrocco");
    }
}

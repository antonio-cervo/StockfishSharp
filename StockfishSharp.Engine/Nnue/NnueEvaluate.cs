// Corrisponde a Eval::evaluate, src/evaluate.cpp:42-69. Vedi ../Types.cs per la nota generale sul
// porting e docs/nnue-porting-plan.md (N6).

namespace StockfishSharp.Engine.Nnue;

public static class NnueEvaluate
{
    private const int PawnValue = 208;
    private const int KnightValue = 781;
    private const int BishopValue = 825;
    private const int RookValue = 1276;
    private const int QueenValue = 2538;

    private const int MaxPly = 246;
    private const int ValueMate = 32000;
    private const int ValueMateInMaxPly = ValueMate - MaxPly;
    private const int ValueTb = ValueMateInMaxPly - 1;
    private const int ValueTbWinInMaxPly = ValueTb - MaxPly;
    private const int ValueTbLossInMaxPly = -ValueTbWinInMaxPly;

    /// <summary><c>Position::non_pawn_material()</c>, position.h:334-337 — qui ricalcolato al
    /// volo dai conteggi pezzi invece che da un campo incrementale (mai aggiunto a Position.cs,
    /// non serve a perft/do_move/undo_move di base — vedi la nota in cima a Position.cs).</summary>
    private static int NonPawnMaterial(Position pos) =>
        (KnightValue * pos.Count(PieceType.Knight)) + (BishopValue * pos.Count(PieceType.Bishop))
        + (RookValue * pos.Count(PieceType.Rook)) + (QueenValue * pos.Count(PieceType.Queen));

    /// <summary><c>Eval::evaluate</c>, evaluate.cpp:42-69 — assume <c>!pos.checkers()</c> (NNUE
    /// non va chiamata sotto scacco, come nella fonte: il chiamante deve gestire quel caso, non
    /// ancora integrato — N7). Valore dal punto di vista del lato al tratto. <paramref
    /// name="accStack"/> (facoltativo, N9): se fornito, l'accumulatore viene aggiornato in modo
    /// incrementale invece di essere ricalcolato da zero — vedi AccumulatorStack.cs.</summary>
    public static int Evaluate(NnueNetwork net, Position pos, int optimism = 0, AccumulatorStack? accStack = null)
    {
        int numPieces = Bitboards.PopCount(pos.Pieces());
        int bucket = (numPieces - 1) / 4;

        NnueAccumulator acc;
        if (accStack != null)
        {
            accStack.Evaluate(pos, net);
            acc = accStack.Latest;
        }
        else
        {
            acc = NnueAccumulator.ComputeFromScratch(net, pos);
        }

        byte[] transformed = NnueLayers.TransformBothPerspectives(acc, pos.SideToMove);

        int psqt = acc.MaterialPsqt(pos.SideToMove, bucket);
        int positional = NnueLayers.Propagate(net.LayerStacks[bucket], transformed) / NnueCommon.OutputScale;

        int nnue = psqt + positional;

        int nnueComplexity = Math.Abs(psqt - positional);
        optimism += (int)((long)optimism * nnueComplexity / 476);
        nnue -= (int)((long)nnue * nnueComplexity / 18236);

        int material = (534 * pos.Count(PieceType.Pawn)) + NonPawnMaterial(pos);
        int v = nnue + (int)(((long)nnue * material + ((long)optimism * 7675)) / 91000);

        v -= v * pos.Rule50Count / 199;

        return Math.Clamp(v, ValueTbLossInMaxPly + 1, ValueTbWinInMaxPly - 1);
    }
}

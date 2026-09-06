// Il placeholder originale (materiale + tabelle posizione-per-pezzo, sotto) resta come fallback
// quando nessuna rete NNUE è caricata (es. i test esistenti di Search, che non hanno bisogno del
// file da ~100MB e traggono vantaggio dalla velocità del placeholder). Quando NnueNetwork è
// impostato (fatto da StockfishSharp.Uci all'avvio, N7 del piano NNUE — vedi
// docs/nnue-porting-plan.md), StaticEval delega a Nnue.NnueEvaluate, il vero porting di
// evaluate.cpp. Il chiamante deve garantire "non sotto scacco" (assert(!pos.checkers()) nella
// fonte) — già vero per entrambi i punti di chiamata in Search.cs, che gestiscono lo scacco a
// parte prima di arrivare qui.

namespace StockfishSharp.Engine;

public static class Evaluate
{
    public static Nnue.NnueNetwork? NnueNetwork { get; set; }


    // Valori standard di manuale (centipedoni), non dalla fonte Stockfish (che con NNUE non ha
    // più bisogno di valori di materiale statici per la valutazione stessa — li usa solo altrove,
    // es. per calibrare margini di ricerca).
    private static readonly int[] PieceValue =
    [
        0, 100, 320, 330, 500, 900, 0, 0,
        0, 100, 320, 330, 500, 900, 0, 0,
    ];

    // Tabelle posizione-per-pezzo (dal punto di vista del Bianco, rank1 in fondo all'array come
    // in una FEN letta dall'alto) — valori standard "PeSTO"-like, dominio pubblico nella comunità
    // chess-programming, usati qui solo come placeholder ragionevole.
    private static readonly int[] PawnTable =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
         50,  50,  50,  50,  50,  50,  50,  50,
         10,  10,  20,  30,  30,  20,  10,  10,
          5,   5,  10,  25,  25,  10,   5,   5,
          0,   0,   0,  20,  20,   0,   0,   0,
          5,  -5, -10,   0,   0, -10,  -5,   5,
          5,  10,  10, -20, -20,  10,  10,   5,
          0,   0,   0,   0,   0,   0,   0,   0,
    ];

    private static readonly int[] KnightTable =
    [
        -50, -40, -30, -30, -30, -30, -40, -50,
        -40, -20,   0,   0,   0,   0, -20, -40,
        -30,   0,  10,  15,  15,  10,   0, -30,
        -30,   5,  15,  20,  20,  15,   5, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30,   5,  10,  15,  15,  10,   5, -30,
        -40, -20,   0,   5,   5,   0, -20, -40,
        -50, -40, -30, -30, -30, -30, -40, -50,
    ];

    private static readonly int[] BishopTable =
    [
        -20, -10, -10, -10, -10, -10, -10, -20,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -10,   0,   5,  10,  10,   5,   0, -10,
        -10,   5,   5,  10,  10,   5,   5, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,  10,  10,  10,  10,  10,  10, -10,
        -10,   5,   0,   0,   0,   0,   5, -10,
        -20, -10, -10, -10, -10, -10, -10, -20,
    ];

    private static readonly int[] RookTable =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
          5,  10,  10,  10,  10,  10,  10,   5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
          0,   0,   0,   5,   5,   0,   0,   0,
    ];

    private static readonly int[] QueenTable =
    [
        -20, -10, -10,  -5,  -5, -10, -10, -20,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -10,   0,   5,   5,   5,   5,   0, -10,
         -5,   0,   5,   5,   5,   5,   0,  -5,
          0,   0,   5,   5,   5,   5,   0,  -5,
        -10,   5,   5,   5,   5,   5,   0, -10,
        -10,   0,   5,   0,   0,   0,   0, -10,
        -20, -10, -10,  -5,  -5, -10, -10, -20,
    ];

    private static readonly int[] KingMiddleGameTable =
    [
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -20, -30, -30, -40, -40, -30, -30, -20,
        -10, -20, -20, -20, -20, -20, -20, -10,
         20,  20,   0,   0,   0,   0,  20,  20,
         20,  30,  10,   0,   0,  10,  30,  20,
    ];

    /// <summary>Valutazione statica dal punto di vista del lato al tratto — segno coerente col
    /// negamax (positivo = meglio per chi deve muovere). <paramref name="accStack"/> (facoltativo,
    /// N9): se fornito e una rete NNUE è caricata, l'accumulatore viene aggiornato in modo
    /// incrementale invece di essere ricalcolato da zero a ogni chiamata — passato solo dalla
    /// ricerca vera (Search.cs), che tiene lo stack sincronizzato con DoMove/UndoMove.
    /// <paramref name="optimism"/> — <c>Search::Worker::evaluate</c>, search.cpp:1901-1904:
    /// <c>optimism[pos.side_to_move()]</c>, derivato dal punteggio medio della mossa radice
    /// corrente (vedi Search.cs, campo _rootOptimism) — zero di default per i chiamanti che non
    /// fanno parte della ricerca vera (es. "eval" da linea di comando, test).</summary>
    public static int StaticEval(Position pos, Nnue.AccumulatorStack? accStack = null, int optimism = 0)
    {
        if (NnueNetwork != null)
            return Nnue.NnueEvaluate.Evaluate(NnueNetwork, pos, optimism, accStack);

        int score = 0;
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            Piece pc = pos.PieceOn(s);
            if (pc == Piece.None) continue;

            var pt = Types.TypeOf(pc);
            var color = Types.ColorOf(pc);
            int sign = color == Color.White ? 1 : -1;

            // Le tabelle sono scritte dal punto di vista del Bianco con rank8 in cima: per il Nero
            // si specchia la casa verticalmente (flip_rank).
            Square tableSquare = color == Color.White ? Types.FlipRank(s) : s;
            int idx = (byte)tableSquare;

            int value = PieceValue[(byte)pc] + pt switch
            {
                PieceType.Pawn => PawnTable[idx],
                PieceType.Knight => KnightTable[idx],
                PieceType.Bishop => BishopTable[idx],
                PieceType.Rook => RookTable[idx],
                PieceType.Queen => QueenTable[idx],
                PieceType.King => KingMiddleGameTable[idx],
                _ => 0,
            };

            score += sign * value;
        }

        return pos.SideToMove == Color.White ? score : -score;
    }
}

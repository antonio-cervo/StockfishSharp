// NON un porting: la fonte reale (evaluate.cpp, 105 righe) è ormai solo un sottile involucro
// attorno a NNUE (rete neurale) — non esiste più una valutazione "classica" da tradurre. Questo
// file è codice originale, scritto apposta per avere una valutazione statica funzionante PRIMA
// che arrivi la Fase NNUE (che sostituirà interamente questo file): materiale + tabelle
// posizione-per-pezzo standard, sufficiente per rendere testabile la ricerca (Fase 2) senza
// aspettare il porting di NNUE (~3.400 righe, fase a sé nel piano).

namespace StockfishSharp.Engine;

public static class Evaluate
{
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
    /// negamax (positivo = meglio per chi deve muovere).</summary>
    public static int StaticEval(Position pos)
    {
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

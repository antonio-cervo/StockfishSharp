// NON un porting diretto di src/movepick.cpp (383 righe: generazione a stadi con history
// completa, countermove, continuation history, capture history) — quello resta un obiettivo per
// un affinamento futuro (vedi docs/porting-plan.md). Qui un ordinamento più semplice ma dello
// stesso spirito, sufficiente a rendere efficace la potatura alfa-beta in Search: mossa dalla TT
// per prima, poi catture ordinate per SEE, poi killer moves, poi mosse quiete per history
// heuristic. Codice originale (non tradotto da C++), pensato per essere sostituito pezzo per
// pezzo quando arriverà il porting fedele di movepick.cpp.

namespace StockfishSharp.Engine;

public sealed class MovePick
{
    private const int MaxPly = Ply.MaxPly;

    // Killer moves: 2 per ply, indicizzate per ply come in Stockfish (non per profondità residua).
    private readonly Move[,] _killers = new Move[MaxPly, 2];

    // History heuristic per mosse quiete, indicizzata [colore][from][to] — stesso schema classico
    // (Butterfly Boards) usato da praticamente ogni motore con questa euristica.
    private readonly int[,,] _history = new int[Colors.Nb, Squares.Nb, Squares.Nb];

    public void Clear()
    {
        Array.Clear(_killers);
        Array.Clear(_history);
    }

    public void RecordCutoff(Position pos, Move m, int ply, int depth)
    {
        if (pos.Capture(m)) return;

        if (_killers[ply, 0] != m)
        {
            _killers[ply, 1] = _killers[ply, 0];
            _killers[ply, 0] = m;
        }

        Color us = pos.SideToMove;
        _history[(byte)us, (byte)m.FromSq, (byte)m.ToSq] += depth * depth;
    }

    /// <summary>Ordina le mosse in place: mossa TT (se presente) per prima, poi catture per SEE
    /// decrescente, poi le due killer di questo ply, poi le rimanenti mosse quiete per history
    /// decrescente.</summary>
    public void OrderMoves(Position pos, List<Move> moves, int ply, Move ttMove)
    {
        Color us = pos.SideToMove;
        Move killer0 = _killers[ply, 0];
        Move killer1 = _killers[ply, 1];

        int Score(Move m)
        {
            if (m == ttMove) return int.MaxValue;

            if (pos.Capture(m))
            {
                // Guadagno SEE come punteggio diretto: catture nettamente vincenti prima di quelle
                // in pareggio/perdenti, ma sempre prima delle mosse quiete (offset fisso).
                int gain = Values.PieceValue[(byte)pos.PieceOn(m.ToSq)] - Values.PieceValue[(byte)pos.PieceOn(m.FromSq)] / 100;
                return 1_000_000 + gain;
            }

            if (m == killer0) return 900_000;
            if (m == killer1) return 899_999;

            return _history[(byte)us, (byte)m.FromSq, (byte)m.ToSq];
        }

        moves.Sort((a, b) => Score(b).CompareTo(Score(a)));
    }
}

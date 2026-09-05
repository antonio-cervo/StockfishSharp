// Ancora NON un porting diretto di src/movepick.cpp (383 righe: generazione a stadi, countermove,
// continuation history, capture history) né di src/history.h per intero — vedi
// docs/porting-plan.md/docs/porting-master-plan.md (Flow A2). Nota di fedeltà: la fonte reale
// (questa versione, letta in ../stockfish-upstream-reference) NON usa più le killer move
// classiche — le ha eliminate a favore della sola history a più livelli (main+continuation+
// capture). Le killer restano qui come euristica aggiuntiva NOSTRA (non della fonte), da
// rivalutare/rimuovere quando arriverà la continuation history vera.
//
// Portato con fedeltà in questo Step: ButterflyHistory (history.h:70-78,128 — aggiornamento "a
// gravità" StatsEntry::operator<<, D=7183) e la formula di bonus/malus di update_all_stats
// (search.cpp:1957-1998, solo il ramo delle mosse quiete: bonus alla bestMove, malus decrescente
// alle altre mosse quiete provate). NON portato: CapturePieceToHistory, ContinuationHistory
// (richiede tracciare currentMove per ply nello Stack, non ancora fatto), PawnHistory,
// LowPlyHistory, TTMoveHistory, CorrectionHistory (vedi Search.cs).

namespace StockfishSharp.Engine;

public sealed class MovePick
{
    private const int MaxPly = Ply.MaxPly;
    private const int MainHistoryLimit = 7183; // ButterflyHistory D, history.h:128

    // Killer moves: 2 per ply, indicizzate per ply come in Stockfish (non per profondità residua)
    // — vedi nota in testa al file: euristica nostra, non della fonte reale.
    private readonly Move[,] _killers = new Move[MaxPly, 2];

    // ButterflyHistory, history.h:128 — Stats<i16,7183,COLOR_NB,UINT_16_HISTORY_SIZE>, indicizzata
    // [colore][move.raw()] esattamente come la fonte.
    private readonly short[,] _mainHistory = new short[Colors.Nb, 65536];

    public void Clear()
    {
        Array.Clear(_killers);
        for (int c = 0; c < Colors.Nb; c++)
            for (int m = 0; m < 65536; m++)
                _mainHistory[c, m] = -5; // Worker::clear(), search.cpp:691 — mainHistory.fill(-5)
    }

    /// <summary><c>StatsEntry::operator&lt;&lt;</c>, history.h:70-77: il bonus spinge il valore
    /// verso ±limite, ma l'incremento effettivo si riduce quanto più il valore è già vicino al
    /// limite (decadimento proporzionale) — garantisce che il valore resti sempre in [-limite,
    /// +limite] senza bisogno di un clamp esplicito dopo l'aggiornamento.</summary>
    private static void UpdateHistory(ref short entry, int bonus, int limit)
    {
        int clampedBonus = Math.Clamp(bonus, -limit, limit);
        int val = entry;
        entry = (short)(val + clampedBonus - (val * Math.Abs(clampedBonus) / limit));
    }

    /// <summary>Aggiorna solo le killer (euristica nostra, vedi nota in testa al file) — chiamata
    /// al taglio beta, come <c>RecordCutoff</c> faceva prima di questo Step.</summary>
    public void RecordKiller(Position pos, Move m, int ply)
    {
        if (pos.Capture(m)) return;

        if (_killers[ply, 0] != m)
        {
            _killers[ply, 1] = _killers[ply, 0];
            _killers[ply, 0] = m;
        }
    }

    /// <summary><c>update_all_stats</c>, search.cpp:1957-1998 — solo il ramo delle mosse quiete
    /// (bonus alla mossa migliore, malus via via più piccolo alle altre mosse quiete provate prima
    /// di trovarla). Chiamata una volta a fine ciclo mosse quando esiste una bestMove, non più ad
    /// ogni taglio beta come <c>RecordCutoff</c> — la fonte aggiorna le statistiche anche quando la
    /// bestMove non causa un taglio (nodo PV pienamente esplorato).</summary>
    public void UpdateStats(Position pos, Move bestMove, List<Move> quietsSearched, List<Move> capturesSearched,
        int depth, Move ttMove, bool isPvNode)
    {
        int bonus = Math.Min((133 * depth) - 81, 1487) + (364 * (bestMove == ttMove ? 1 : 0));
        int malus = Math.Min((968 * depth) - 235, 2244);

        if (!isPvNode)
            bonus += (int)((long)bonus * (quietsSearched.Count + capturesSearched.Count) / 256);

        if (!pos.CaptureStage(bestMove))
        {
            Color us = pos.SideToMove;
            UpdateHistory(ref _mainHistory[(byte)us, bestMove.Raw], bonus * 899 / 1024, MainHistoryLimit);

            int actualMalus = malus * 1159 / 1024;
            foreach (var m in quietsSearched)
            {
                actualMalus = actualMalus * 921 / 1024;
                UpdateHistory(ref _mainHistory[(byte)us, m.Raw], -actualMalus, MainHistoryLimit);
            }
        }
        // NON portato: bonus/malus di CapturePieceToHistory quando bestMove è una cattura, e malus
        // per le catture scartate in capturesSearched (history.h/search.cpp:1993-2011) — le
        // catture restano ordinate solo per SEE (vedi OrderMoves).
    }

    /// <summary>Ordina le mosse in place: mossa TT (se presente) per prima, poi catture per SEE
    /// decrescente, poi le due killer di questo ply (euristica nostra), poi le rimanenti mosse
    /// quiete per <see cref="_mainHistory"/> decrescente.</summary>
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

            return _mainHistory[(byte)us, m.Raw];
        }

        moves.Sort((a, b) => Score(b).CompareTo(Score(a)));
    }
}

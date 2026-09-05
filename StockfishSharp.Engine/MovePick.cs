// Ancora NON un porting diretto di src/movepick.cpp (383 righe: generazione a stadi, countermove,
// continuation history, capture history) né di src/history.h per intero — vedi
// docs/porting-plan.md/docs/porting-master-plan.md (Flow A2). Nota di fedeltà: la fonte reale
// (questa versione, letta in ../stockfish-upstream-reference) NON usa più le killer move
// classiche — le ha eliminate a favore della sola history a più livelli (main+continuation+
// capture). Le killer restano qui come euristica aggiuntiva NOSTRA (non della fonte), da
// rivalutare/rimuovere quando arriverà la continuation history vera.
//
// Portato con fedeltà: ButterflyHistory (history.h:70-78,128 — aggiornamento "a gravità"
// StatsEntry::operator<<, D=7183) e la formula di bonus/malus di update_all_stats
// (search.cpp:1957-1998, solo il ramo delle mosse quiete: bonus alla bestMove, malus decrescente
// alle altre mosse quiete provate).
//
// ContinuationHistory PARZIALE: la fonte guarda fino a 6 ply indietro (conthist_bonuses,
// search.cpp:2018-2019, pesi {520,390,145,251,66,209} per ss-1..ss-6) con una selezione
// [inCheck][captureStage] a parte per ogni combinazione di ply corrente/precedente. Qui SOLO
// ss-1 (il peso maggiore, 520), UNA sola tabella (niente selezione inCheck/captureStage), niente
// "positiveCount"/moltiplicatore variabile (con un solo termine è sempre il primo,
// CMHCMultipliers[0]=94) — vedi UpdateQuietHistory. Richiede Search.cs a tracciare
// Stack::currentMove/moved_piece per ply (fatto in questo Step).
//
// NON portato: CapturePieceToHistory, ContinuationHistory per ss-2..ss-6, PawnHistory,
// LowPlyHistory, TTMoveHistory, CorrectionHistory (vedi Search.cs).

namespace StockfishSharp.Engine;

public sealed class MovePick
{
    private const int MaxPly = Ply.MaxPly;
    private const int MainHistoryLimit = 7183; // ButterflyHistory D, history.h:128
    private const int PieceToHistoryLimit = 30000; // PieceToHistory D, history.h:138

    // Killer moves: 2 per ply, indicizzate per ply come in Stockfish (non per profondità residua)
    // — vedi nota in testa al file: euristica nostra, non della fonte reale.
    private readonly Move[,] _killers = new Move[MaxPly, 2];

    // ButterflyHistory, history.h:128 — Stats<i16,7183,COLOR_NB,UINT_16_HISTORY_SIZE>, indicizzata
    // [colore][move.raw()] esattamente come la fonte.
    private readonly short[,] _mainHistory = new short[Colors.Nb, 65536];

    // ContinuationHistory a un solo livello di lookback (ss-1) — vedi nota in testa al file.
    // Indicizzata [pezzo mosso al ply precedente][sua casa di arrivo][pezzo di questa mossa][sua
    // casa di arrivo], PieceToHistory della fonte (history.h:137-138).
    private readonly short[,,,] _continuationHistory1 = new short[PieceSlots.Nb, Squares.Nb, PieceSlots.Nb, Squares.Nb];

    public void Clear()
    {
        Array.Clear(_killers);
        for (int c = 0; c < Colors.Nb; c++)
            for (int m = 0; m < 65536; m++)
                _mainHistory[c, m] = -5; // Worker::clear(), search.cpp:691 — mainHistory.fill(-5)

        // search.cpp:702-704 — continuationHistory[...].fill(-586); qui una sola tabella (vedi
        // nota in testa al file) invece delle 4 [inCheck][captureStage] della fonte.
        for (int p1 = 0; p1 < PieceSlots.Nb; p1++)
            for (int s1 = 0; s1 < Squares.Nb; s1++)
                for (int p2 = 0; p2 < PieceSlots.Nb; p2++)
                    for (int s2 = 0; s2 < Squares.Nb; s2++)
                        _continuationHistory1[p1, s1, p2, s2] = -586;
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
    /// bestMove non causa un taglio (nodo PV pienamente esplorato). <paramref name="prevPiece"/>/
    /// <paramref name="prevTo"/> sono <c>(ss-1)-&gt;currentMove</c> (pezzo/casa d'arrivo), per la
    /// continuation history — <see cref="Piece.None"/> se questo nodo non ha un ply precedente
    /// (radice) o non l'abbiamo tracciato.</summary>
    public void UpdateStats(Position pos, Move bestMove, List<Move> quietsSearched, List<Move> capturesSearched,
        int depth, Move ttMove, bool isPvNode, Piece prevPiece, Square prevTo)
    {
        int bonus = Math.Min((133 * depth) - 81, 1487) + (364 * (bestMove == ttMove ? 1 : 0));
        int malus = Math.Min((968 * depth) - 235, 2244);

        if (!isPvNode)
            bonus += (int)((long)bonus * (quietsSearched.Count + capturesSearched.Count) / 256);

        if (!pos.CaptureStage(bestMove))
        {
            UpdateQuietHistory(pos, bestMove, bonus * 899 / 1024, prevPiece, prevTo);

            int actualMalus = malus * 1159 / 1024;
            foreach (var m in quietsSearched)
            {
                actualMalus = actualMalus * 921 / 1024;
                UpdateQuietHistory(pos, m, -actualMalus, prevPiece, prevTo);
            }
        }
        // NON portato: bonus/malus di CapturePieceToHistory quando bestMove è una cattura, e malus
        // per le catture scartate in capturesSearched (history.h/search.cpp:1993-2011) — le
        // catture restano ordinate solo per SEE (vedi OrderMoves).
    }

    /// <summary><c>update_quiet_histories</c>, search.cpp:2045-2056 — qui solo main history +
    /// continuation history a un livello (vedi nota in testa al file per low-ply/pawn history non
    /// portate).</summary>
    private void UpdateQuietHistory(Position pos, Move move, int bonus, Piece prevPiece, Square prevTo)
    {
        Color us = pos.SideToMove;
        UpdateHistory(ref _mainHistory[(byte)us, move.Raw], bonus, MainHistoryLimit);

        if (prevPiece == Piece.None) return; // (ss-1)->currentMove non è ok (radice, o non tracciata)

        // update_continuation_histories, search.cpp:2017-2041: qui solo il termine ss-1 (peso 520,
        // il maggiore dei 6) — con un solo termine "positiveCount" resta sempre 0, quindi il
        // moltiplicatore è sempre CMHCMultipliers[0]=94. Il "+73*(i<2)" della fonte si applica
        // (ss-1 ha i=1<2) indipendentemente dal segno del bonus.
        Piece pc = pos.MovedPiece(move);
        int conthistBonus = (bonus * 750 / 1024 * 520 * 94 / 65536) + 73;
        UpdateHistory(ref _continuationHistory1[(byte)prevPiece, (byte)prevTo, (byte)pc, (byte)move.ToSq], conthistBonus, PieceToHistoryLimit);
    }

    /// <summary>Ordina le mosse in place: mossa TT (se presente) per prima, poi catture per SEE
    /// decrescente, poi le due killer di questo ply (euristica nostra), poi le rimanenti mosse
    /// quiete per main history + continuation history a un livello (vedi nota in testa al file)
    /// decrescenti. <paramref name="prevPiece"/>/<paramref name="prevTo"/> come in
    /// <see cref="UpdateStats"/>.</summary>
    public void OrderMoves(Position pos, List<Move> moves, int ply, Move ttMove, Piece prevPiece, Square prevTo)
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

            int score = _mainHistory[(byte)us, m.Raw];
            if (prevPiece != Piece.None)
                score += _continuationHistory1[(byte)prevPiece, (byte)prevTo, (byte)pos.MovedPiece(m), (byte)m.ToSq];
            return score;
        }

        moves.Sort((a, b) => Score(b).CompareTo(Score(a)));
    }
}

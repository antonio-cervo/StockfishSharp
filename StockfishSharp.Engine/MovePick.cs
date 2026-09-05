// Ancora NON un porting diretto di src/movepick.cpp (383 righe: generazione a stadi, countermove)
// — vedi docs/porting-plan.md/docs/porting-master-plan.md (Flow A2). Da src/history.h portate con
// fedeltà main/capture/continuation history (sotto); mancano ancora pawn/low-ply/TT-move history.
// Nota di fedeltà: la fonte reale (questa versione, letta in ../stockfish-upstream-reference) NON
// usa più le killer move classiche — le ha eliminate a favore della sola history a più livelli.
// Le killer restano qui come euristica aggiuntiva NOSTRA (non della fonte), da rivalutare/
// rimuovere quando arriverà la generazione a stadi vera (countermove incluso).
//
// Portato con fedeltà: ButterflyHistory (history.h:70-78,128 — aggiornamento "a gravità"
// StatsEntry::operator<<, D=7183) e la formula di bonus/malus di update_all_stats
// (search.cpp:1957-1998, solo il ramo delle mosse quiete: bonus alla bestMove, malus decrescente
// alle altre mosse quiete provate).
//
// ContinuationHistory FEDELE per i 6 livelli di lookback (ss-1..ss-6, conthist_bonuses
// search.cpp:2018-2022, pesi {520,390,145,251,66,209} e CMHCMultipliers
// {94,103,110,106,119,126,121}), inclusa la selezione [inCheck][captureStage] della fonte
// (do_move, search.cpp:663-671: la tabella dipende dallo scacco del NODO GENITORE e da se la
// mossa lì giocata era una cattura) e il "solo i primi 2 se il nodo corrente è sotto scacco"
// (search.cpp:2028-2029). Richiede Search.cs a tracciare currentMove/moved_piece/inCheck/
// captureStage per ply (ContinuationRef, costruita da Search.cs e passata qui).
//
// CapturePieceToHistory portata con fedeltà (history.h:135, D=10692) — bonus/malus da
// update_all_stats (search.cpp:1993-2011), usata anche in OrderMoves come termine aggiuntivo
// (la fonte la userebbe dentro il vero MovePicker a stadi, non ancora portato).
//
// NON portato: PawnHistory, LowPlyHistory, TTMoveHistory, CorrectionHistory (vedi Search.cs); in
// OrderMoves la continuation history usa solo ss-1 (non tutti e 6 i livelli) come termine
// d'ordinamento — la fonte la userebbe tutta dentro il vero MovePicker a stadi/reduction(), non
// ancora portati.

namespace StockfishSharp.Engine;

/// <summary>Informazioni su <c>(ss-i)-&gt;currentMove</c> necessarie alla continuation history —
/// costruita da <see cref="Search"/> per i=1..6, <see cref="IsOk"/> falso se quel ply non esiste
/// (vicino alla radice) o la mossa lì non è stata tracciata (es. in quiescenza).</summary>
public readonly struct ContinuationRef(bool isOk, bool inCheck, bool captureStage, Piece piece, Square to)
{
    public readonly bool IsOk = isOk;
    public readonly bool InCheck = inCheck;
    public readonly bool CaptureStage = captureStage;
    public readonly Piece Piece = piece;
    public readonly Square To = to;
}

public sealed class MovePick
{
    private const int MaxPly = Ply.MaxPly;
    private const int MainHistoryLimit = 7183; // ButterflyHistory D, history.h:128
    private const int PieceToHistoryLimit = 30000; // PieceToHistory D, history.h:138
    private const int CaptureHistoryLimit = 10692; // CapturePieceToHistory D, history.h:135

    // Killer moves: 2 per ply, indicizzate per ply come in Stockfish (non per profondità residua)
    // — vedi nota in testa al file: euristica nostra, non della fonte reale.
    private readonly Move[,] _killers = new Move[MaxPly, 2];

    // ButterflyHistory, history.h:128 — Stats<i16,7183,COLOR_NB,UINT_16_HISTORY_SIZE>, indicizzata
    // [colore][move.raw()] esattamente come la fonte.
    private readonly short[,] _mainHistory = new short[Colors.Nb, 65536];

    // ContinuationHistory, history.h:137-143 — ContinuationHistoryBlock::table[2][2] della fonte:
    // indicizzata [scacco del nodo genitore][la sua mossa era una cattura][pezzo mosso lì][sua
    // casa di arrivo][pezzo di questa mossa][sua casa di arrivo].
    private readonly short[,,,,,] _continuationHistory = new short[2, 2, PieceSlots.Nb, Squares.Nb, PieceSlots.Nb, Squares.Nb];

    private static readonly (int Lookback, int Weight)[] ConthistBonuses =
        [(1, 520), (2, 390), (3, 145), (4, 251), (5, 66), (6, 209)]; // search.cpp:2018-2019
    private static readonly int[] CmhcMultipliers = [94, 103, 110, 106, 119, 126, 121]; // search.cpp:2022

    // CapturePieceToHistory, history.h:135 — Stats<i16,10692,PIECE_NB,SQUARE_NB,PIECE_TYPE_NB>,
    // indicizzata [pezzo che cattura][casa di arrivo][tipo del pezzo catturato].
    private readonly short[,,] _captureHistory = new short[PieceSlots.Nb, Squares.Nb, PieceTypes.Nb];

    public void Clear()
    {
        Array.Clear(_killers);
        for (int c = 0; c < Colors.Nb; c++)
            for (int m = 0; m < 65536; m++)
                _mainHistory[c, m] = -5; // Worker::clear(), search.cpp:691 — mainHistory.fill(-5)

        // search.cpp:699-704 — continuationHistory[inCheck][capture][...].fill(-586) per le 4
        // combinazioni.
        for (int ic = 0; ic < 2; ic++)
            for (int cs = 0; cs < 2; cs++)
                for (int p1 = 0; p1 < PieceSlots.Nb; p1++)
                    for (int s1 = 0; s1 < Squares.Nb; s1++)
                        for (int p2 = 0; p2 < PieceSlots.Nb; p2++)
                            for (int s2 = 0; s2 < Squares.Nb; s2++)
                                _continuationHistory[ic, cs, p1, s1, p2, s2] = -586;

        for (int p = 0; p < PieceSlots.Nb; p++)
            for (int s = 0; s < Squares.Nb; s++)
                for (int t = 0; t < PieceTypes.Nb; t++)
                    _captureHistory[p, s, t] = -742; // Worker::clear(), search.cpp:692
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
    /// bestMove non causa un taglio (nodo PV pienamente esplorato). <paramref name="contRefs"/> è
    /// <c>(ss-1)..(ss-6)-&gt;currentMove</c> per la continuation history, <paramref
    /// name="currentInCheck"/> lo scacco di QUESTO nodo (non del genitore).</summary>
    public void UpdateStats(Position pos, Move bestMove, List<Move> quietsSearched, List<Move> capturesSearched,
        int depth, Move ttMove, bool isPvNode, ContinuationRef[] contRefs, bool currentInCheck)
    {
        int bonus = Math.Min((133 * depth) - 81, 1487) + (364 * (bestMove == ttMove ? 1 : 0));
        int malus = Math.Min((968 * depth) - 235, 2244);

        if (!isPvNode)
            bonus += (int)((long)bonus * (quietsSearched.Count + capturesSearched.Count) / 256);

        if (!pos.CaptureStage(bestMove))
        {
            UpdateQuietHistory(pos, bestMove, bonus * 899 / 1024, contRefs, currentInCheck);

            int actualMalus = malus * 1159 / 1024;
            foreach (var m in quietsSearched)
            {
                actualMalus = actualMalus * 921 / 1024;
                UpdateQuietHistory(pos, m, -actualMalus, contRefs, currentInCheck);
            }
        }
        else
        {
            // search.cpp:1995-1998 — bonus alla cattura migliore.
            Piece movedPiece = pos.MovedPiece(bestMove);
            PieceType capturedPiece = Types.TypeOf(pos.PieceOn(bestMove.ToSq));
            UpdateHistory(ref _captureHistory[(byte)movedPiece, (byte)bestMove.ToSq, (byte)capturedPiece], bonus * 1427 / 1024, CaptureHistoryLimit);
        }

        // search.cpp:2005-2011 — malus per tutte le catture provate ma scartate (indipendente da
        // se bestMove sia stata una cattura o una mossa quieta).
        foreach (var m in capturesSearched)
        {
            Piece movedPiece = pos.MovedPiece(m);
            PieceType capturedPiece = Types.TypeOf(pos.PieceOn(m.ToSq));
            UpdateHistory(ref _captureHistory[(byte)movedPiece, (byte)m.ToSq, (byte)capturedPiece], -malus * 1489 / 1024, CaptureHistoryLimit);
        }
    }

    /// <summary><c>update_quiet_histories</c>, search.cpp:2045-2056 — qui solo main history +
    /// continuation history (vedi nota in testa al file per low-ply/pawn history non portate).</summary>
    private void UpdateQuietHistory(Position pos, Move move, int bonus, ContinuationRef[] contRefs, bool currentInCheck)
    {
        Color us = pos.SideToMove;
        UpdateHistory(ref _mainHistory[(byte)us, move.Raw], bonus, MainHistoryLimit);

        Piece pc = pos.MovedPiece(move);
        UpdateContinuationHistories(contRefs, currentInCheck, pc, move.ToSq, bonus * 750 / 1024);
    }

    /// <summary><c>update_continuation_histories</c>, search.cpp:2017-2041 — fedele ai 6 livelli
    /// di lookback, "positiveCount"/moltiplicatore variabile incluso. <paramref
    /// name="currentInCheck"/> ferma il ciclo dopo i primi 2 livelli, come nella fonte.</summary>
    private void UpdateContinuationHistories(ContinuationRef[] contRefs, bool currentInCheck, Piece pc, Square to, int bonus)
    {
        int positiveCount = 0;

        foreach (var (i, weight) in ConthistBonuses)
        {
            if (currentInCheck && i > 2) break;

            var r = contRefs[i - 1];
            if (!r.IsOk) continue;

            ref short entry = ref _continuationHistory[r.InCheck ? 1 : 0, r.CaptureStage ? 1 : 0, (byte)r.Piece, (byte)r.To, (byte)pc, (byte)to];
            if (entry > 0) positiveCount++;

            int multiplier = CmhcMultipliers[positiveCount];
            UpdateHistory(ref entry, (bonus * weight * multiplier / 65536) + (73 * (i < 2 ? 1 : 0)), PieceToHistoryLimit);
        }
    }

    /// <summary>Ordina le mosse in place: mossa TT (se presente) per prima, poi catture per SEE
    /// decrescente (con CapturePieceToHistory come spareggio), poi le due killer di questo ply
    /// (euristica nostra), poi le rimanenti mosse quiete per main history + continuation history
    /// (solo ss-1, <c>contRefs[0]</c> — la fonte la userebbe tutta dentro il vero MovePicker a
    /// stadi/reduction(), non ancora portati) decrescenti.</summary>
    public void OrderMoves(Position pos, List<Move> moves, int ply, Move ttMove, ContinuationRef[] contRefs)
    {
        Color us = pos.SideToMove;
        Move killer0 = _killers[ply, 0];
        Move killer1 = _killers[ply, 1];
        var ss1 = contRefs[0];

        int Score(Move m)
        {
            if (m == ttMove) return int.MaxValue;

            if (pos.Capture(m))
            {
                // Guadagno SEE come termine dominante (catture nettamente vincenti prima di quelle
                // in pareggio/perdenti, sempre prima delle mosse quiete grazie all'offset fisso),
                // più CapturePieceToHistory come spareggio fra catture di guadagno simile — la
                // fonte li combinerebbe dentro il vero MovePicker a stadi, non ancora portato.
                int gain = Values.PieceValue[(byte)pos.PieceOn(m.ToSq)] - Values.PieceValue[(byte)pos.PieceOn(m.FromSq)] / 100;
                int captureHistoryScore = _captureHistory[(byte)pos.MovedPiece(m), (byte)m.ToSq, (byte)Types.TypeOf(pos.PieceOn(m.ToSq))];
                return 1_000_000 + gain + (captureHistoryScore / 64);
            }

            if (m == killer0) return 900_000;
            if (m == killer1) return 899_999;

            int score = _mainHistory[(byte)us, m.Raw];
            if (ss1.IsOk)
                score += _continuationHistory[ss1.InCheck ? 1 : 0, ss1.CaptureStage ? 1 : 0, (byte)ss1.Piece, (byte)ss1.To, (byte)pos.MovedPiece(m), (byte)m.ToSq];
            return score;
        }

        moves.Sort((a, b) => Score(b).CompareTo(Score(a)));
    }
}

// Contenitore delle tabelle di history — corrisponde a ciò che in Stockfish vive dentro Worker
// (mainHistory, lowPlyHistory, captureHistory, continuationHistory, ttMoveHistory, pawnHistory),
// NON alla classe MovePicker della fonte: la generazione "a stadi" vera (src/movepick.cpp, Flow A2)
// è in MovePicker.cs, che legge queste tabelle tramite i getter pubblici sotto invece di
// possederle. Le vecchie killer move (euristica nostra, non della fonte — Stockfish le ha
// eliminate a favore della sola history a più livelli) e il vecchio OrderMoves eager sono stati
// rimossi quando MovePicker.cs ha sostituito quel percorso.
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
// update_all_stats (search.cpp:1993-2011) e usata da MovePicker.ScoreCaptures come termine di
// ordinamento delle catture (movepick.cpp:224-226).
//
// LowPlyHistory portata con fedeltà (history.h:130-132, D=7183 come main history, LOW_PLY_
// HISTORY_SIZE=5): a differenza delle altre si azzera (fill 102) a OGNI ricerca
// (iterative_deepening, search.cpp:326), non a ogni nuova partita — vedi ResetForSearch, chiamata
// da Search.Search_. Usata da MovePicker.ScoreQuiets (termine aggiuntivo per ply<5).
//
// TTMoveHistory portata con fedeltà (history.h:196, D=8192): un solo contatore globale, bonus
// quando la mossa migliore combacia con quella di TT (search.cpp:1574-1575). Usata dalle Singular
// Extensions in Search.cs (search.cpp:1260-1261/1279); non ancora nel margine di futility Step 9
// né nella riduzione LMR (entrambi non ancora a questo livello di dettaglio nella fonte qui usata).
//
// PawnHistory portata PARZIALMENTE (history.h:146, D=8192, chiave = zobrist dei pedoni & 8191):
// il punto di aggiornamento in update_quiet_histories (search.cpp:2056-2057) e la lettura da
// MovePicker.ScoreQuiets (movepick.cpp:232, tramite sharedHistory->pawn_entry nella fonte). Il
// bonus al "countermove" quieto su fail-low puro (search.cpp:1578-1601) è portato
// (ApplyCountermoveQuietBonus); il bonus di ordinamento da differenza di valutazione statica
// (search.cpp:978-986) no, perché la tecnica a cui appartiene non lo è.
//
// NON portato: CorrectionHistory è in Search.cs (fatta).

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

    // LowPlyHistory, history.h:130-132 — Stats<i16,7183,LOW_PLY_HISTORY_SIZE,UINT_16_HISTORY_SIZE>,
    // indicizzata [ply][move.raw()]; azzerata per ogni ricerca, non per ogni partita (vedi nota in
    // testa al file).
    private const int LowPlyHistorySize = 5;
    private readonly short[,] _lowPlyHistory = new short[LowPlyHistorySize, 65536];

    // TTMoveHistory, history.h:196 — StatsEntry<i16,8192> singolo, non indicizzato.
    private const int TtMoveHistoryLimit = 8192;
    private short _ttMoveHistory;

    /// <summary>Lettura pubblica di TTMoveHistory — usata dalle Singular Extensions in Search.cs
    /// (search.cpp:1260-1261).</summary>
    public int TtMoveHistory => _ttMoveHistory;

    /// <summary>Aggiornamento diretto di TTMoveHistory fuori da <see cref="UpdateStats"/> — usato
    /// dal multi-cut delle Singular Extensions (search.cpp:1279).</summary>
    public void UpdateTtMoveHistory(int bonus) => UpdateHistory(ref _ttMoveHistory, bonus, TtMoveHistoryLimit);

    // PawnHistory, history.h:146, 38 — DynStats<AtomicStats<i16,8192,PIECE_NB,SQUARE_NB>,
    // PAWN_HISTORY_BASE_SIZE(8192)>, indicizzata [zobrist dei pedoni & 8191][pezzo][casa]. Qui
    // solo il punto di aggiornamento in update_quiet_histories (search.cpp:2056-2057) — gli altri
    // due usi della fonte (bonus di ordinamento da differenza di valutazione statica, bonus al
    // "countermove" quieto su fail-low puro) non sono ancora portati (le tecniche a cui
    // appartengono non lo sono).
    private const int PawnHistorySize = 8192;
    private const int PawnHistoryLimit = 8192; // AtomicStats<i16,8192,...> D, history.h:146
    private readonly short[,,] _pawnHistory = new short[PawnHistorySize, PieceSlots.Nb, Squares.Nb];

    public void Clear()
    {
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

        _ttMoveHistory = 0; // Worker::clear(), search.cpp:706

        for (int k = 0; k < PawnHistorySize; k++)
            for (int p = 0; p < PieceSlots.Nb; p++)
                for (int s = 0; s < Squares.Nb; s++)
                    _pawnHistory[k, p, s] = -1338; // clear_range(-1338, ...), search.cpp:697
    }

    /// <summary><c>lowPlyHistory.fill(102)</c>, iterative_deepening, search.cpp:326 — a differenza
    /// delle altre history questa si azzera a OGNI ricerca, non a ogni nuova partita.</summary>
    public void ResetForSearch()
    {
        for (int p = 0; p < LowPlyHistorySize; p++)
            for (int m = 0; m < 65536; m++)
                _lowPlyHistory[p, m] = 102;
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

    /// <summary><c>update_all_stats</c>, search.cpp:1957-1998 — solo il ramo delle mosse quiete
    /// (bonus alla mossa migliore, malus via via più piccolo alle altre mosse quiete provate prima
    /// di trovarla). Chiamata una volta a fine ciclo mosse quando esiste una bestMove, non più ad
    /// ogni taglio beta come <c>RecordCutoff</c> — la fonte aggiorna le statistiche anche quando la
    /// bestMove non causa un taglio (nodo PV pienamente esplorato). <paramref name="contRefs"/> è
    /// <c>(ss-1)..(ss-6)-&gt;currentMove</c> per la continuation history, <paramref
    /// name="currentInCheck"/> lo scacco di QUESTO nodo (non del genitore).</summary>
    public void UpdateStats(Position pos, int ply, Move bestMove, List<Move> quietsSearched, List<Move> capturesSearched,
        int depth, Move ttMove, bool isPvNode, ContinuationRef[] contRefs, bool currentInCheck)
    {
        int bonus = Math.Min((133 * depth) - 81, 1487) + (364 * (bestMove == ttMove ? 1 : 0));
        int malus = Math.Min((968 * depth) - 235, 2244);

        if (!isPvNode)
            bonus += (int)((long)bonus * (quietsSearched.Count + capturesSearched.Count) / 256);

        if (!pos.CaptureStage(bestMove))
        {
            UpdateQuietHistory(pos, ply, bestMove, bonus * 899 / 1024, contRefs, currentInCheck);

            int actualMalus = malus * 1159 / 1024;
            foreach (var m in quietsSearched)
            {
                actualMalus = actualMalus * 921 / 1024;
                UpdateQuietHistory(pos, ply, m, -actualMalus, contRefs, currentInCheck);
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

        // search.cpp:1574-1575 — bonus/malus a TTMoveHistory quando la bestMove combacia o no con
        // la mossa di TT (solo nei nodi non-PV).
        if (!isPvNode)
            UpdateHistory(ref _ttMoveHistory, bestMove == ttMove ? 918 : -747, TtMoveHistoryLimit);
    }

    /// <summary>"Post LMR continuation history updates" (Step 18, search.cpp:1390) — bonus fisso
    /// alla mossa la cui ricerca ridotta ha superato alpha, applicato sulle continuation history
    /// del nodo CORRENTE (ss, non ss-1).</summary>
    public void ApplyPostLmrBonus(ContinuationRef[] contRefs, bool currentInCheck, Piece pc, Square to) =>
        UpdateContinuationHistories(contRefs, currentInCheck, pc, to, 1334);

    /// <summary>Aggiornamenti di ordinamento su TAGLIO DA TRANSPOSITION TABLE (Step 6,
    /// search.cpp:879-883) — la mossa di TT quieta che fa fallire alto viene premiata anche se non
    /// e' mai stata realmente cercata: senza questo, ogni taglio da TT sarebbe informazione persa
    /// per le history. Stesso <c>update_quiet_histories</c> usato dallo Step 23.</summary>
    public void ApplyTtCutoffQuietBonus(Position pos, int ply, Move ttMove, int bonus, ContinuationRef[] contRefs, bool currentInCheck) =>
        UpdateQuietHistory(pos, ply, ttMove, bonus, contRefs, currentInCheck);

    /// <summary>"Extra penalty for early quiet moves of the previous ply" (Step 6,
    /// search.cpp:886-887): se il taglio arriva presto nel ciclo mosse del genitore, la mossa del
    /// genitore era probabilmente cattiva.</summary>
    public void ApplyTtCutoffPrevPenalty(ContinuationRef[] prevContRefs, bool prevInCheck, Piece prevPiece, Square prevSq, int bonus) =>
        UpdateContinuationHistories(prevContRefs, prevInCheck, prevPiece, prevSq, bonus);

    /// <summary>Ramo "bonus per il countermove quieto che ha causato il fail-low puro",
    /// search.cpp:1594-1601 — chiamato da Search.cs quando nessuna mossa del nodo corrente supera
    /// alpha. <paramref name="parentContRefs"/>/<paramref name="parentInCheck"/> sono
    /// (ss-1)-&gt;(ss-2..ss-7), non quelli del nodo corrente.</summary>
    public void ApplyCountermoveQuietBonus(Position pos, Piece prevPiece, Square prevSq, Move parentMove,
        ContinuationRef[] parentContRefs, bool parentInCheck, int scaledBonus, Color opponent)
    {
        UpdateContinuationHistories(parentContRefs, parentInCheck, prevPiece, prevSq, scaledBonus * 263 / 16384);
        UpdateHistory(ref _mainHistory[(byte)opponent, parentMove.Raw], scaledBonus * 215 / 32768, MainHistoryLimit);

        if (Types.TypeOf(prevPiece) != PieceType.Pawn && parentMove.TypeOf != MoveType.Promotion)
            UpdateHistory(ref _pawnHistory[pos.PawnKey & (PawnHistorySize - 1), (byte)prevPiece, (byte)prevSq], scaledBonus * 324 / 8192, PawnHistoryLimit);
    }

    /// <summary>Ramo "bonus per il countermove di cattura che ha causato il fail-low puro",
    /// search.cpp:1603-1609.</summary>
    public void ApplyCountermoveCaptureBonus(Piece prevPiece, Square prevSq, PieceType capturedType) =>
        UpdateHistory(ref _captureHistory[(byte)prevPiece, (byte)prevSq, (byte)capturedType], 892, CaptureHistoryLimit);

    /// <summary>"Use static evaluation difference to improve quiet move ordering",
    /// search.cpp:978-986 — a differenza di <see cref="UpdateStats"/> non dipende dall'esito della
    /// ricerca di questo nodo: premia/punisce la mossa del GENITORE in base a quanto la
    /// valutazione statica è cambiata da lì a qui (una mossa che ha portato a una posizione più
    /// brutta/migliore del previsto). Chiamata da Search.cs per ogni nodo non sotto scacco la cui
    /// mossa del genitore non era né sotto scacco né una cattura.</summary>
    public void ApplyEvalDiffMainBonus(Color opponent, Move parentMove, int bonus) =>
        UpdateHistory(ref _mainHistory[(byte)opponent, parentMove.Raw], bonus, MainHistoryLimit);

    /// <summary>Metà "pawn history" dello stesso ramo, search.cpp:983-985 — condizionata a parte
    /// perché nella fonte ha guardie aggiuntive (nessun hit di TT, pezzo del genitore non un
    /// pedone, mossa del genitore non una promozione).</summary>
    public void ApplyEvalDiffPawnBonus(Position pos, Piece prevPiece, Square prevSq, int bonus) =>
        UpdateHistory(ref _pawnHistory[pos.PawnKey & (PawnHistorySize - 1), (byte)prevPiece, (byte)prevSq], bonus, PawnHistoryLimit);

    /// <summary><c>ss-&gt;statScore</c>, search.cpp:1342-1349 — usato da Reduction() in Search.cs
    /// per rifinire la riduzione LMR in base a quanto la history "approva" la mossa. Per le
    /// catture usa CapturePieceToHistory; per le mosse quiete combina main history + le prime due
    /// continuation history (ss-1, ss-2 — <paramref name="contRefs"/>[0]/[1]).</summary>
    public int ComputeStatScore(Position pos, Move m, bool captureStage, ContinuationRef[] contRefs)
    {
        if (captureStage)
        {
            Piece movedPiece = pos.MovedPiece(m);
            PieceType capturedPiece = Types.TypeOf(pos.PieceOn(m.ToSq));
            return (873 * Values.PieceValue[(byte)pos.PieceOn(m.ToSq)] / 128)
                + _captureHistory[(byte)movedPiece, (byte)m.ToSq, (byte)capturedPiece];
        }

        Color us = pos.SideToMove;
        Piece pc = pos.MovedPiece(m);
        int mainScore = _mainHistory[(byte)us, m.Raw];
        int cont0 = ContinuationScore(contRefs[0], pc, m.ToSq);
        int cont1 = ContinuationScore(contRefs[1], pc, m.ToSq);

        return ((2252 * mainScore) + (1126 * cont0) + (1093 * cont1)) / 1024;
    }

    /// <summary>Somma di contHist[0]+contHist[1]+pawn_entry per una mossa quieta — usata dallo
    /// Step 15 (potatura a profondità bassa, search.cpp:1200-1202) in Search.cs.</summary>
    public int ComputeQuietPruningHistory(Position pos, Move m, ContinuationRef[] contRefs)
    {
        Piece pc = pos.MovedPiece(m);
        Square to = m.ToSq;
        int cont0 = ContinuationScore(contRefs[0], pc, to);
        int cont1 = ContinuationScore(contRefs[1], pc, to);
        int pawnScore = _pawnHistory[pos.PawnKey & (PawnHistorySize - 1), (byte)pc, (byte)to];
        return cont0 + cont1 + pawnScore;
    }

    public int GetMainHistoryRaw(Color us, Move m) => _mainHistory[(byte)us, m.Raw];

    public int GetCaptureHistory(Piece movedPiece, Square to, PieceType captured) =>
        _captureHistory[(byte)movedPiece, (byte)to, (byte)captured];

    public int GetLowPlyHistoryValue(int ply, Move m) => _lowPlyHistory[ply, m.Raw];

    public int GetPawnHistoryValue(Position pos, Piece pc, Square to) =>
        _pawnHistory[pos.PawnKey & (PawnHistorySize - 1), (byte)pc, (byte)to];

    /// <summary>Lettura pubblica di un livello di continuation history (0=ss-1..5=ss-6) — usata
    /// da <see cref="MovePicker"/> per lo score delle mosse quiete (movepick.cpp:233-237).</summary>
    public int GetContinuationHistory(ContinuationRef r, Piece pc, Square to) => ContinuationScore(r, pc, to);

    private int ContinuationScore(ContinuationRef r, Piece pc, Square to) =>
        r.IsOk ? _continuationHistory[r.InCheck ? 1 : 0, r.CaptureStage ? 1 : 0, (byte)r.Piece, (byte)r.To, (byte)pc, (byte)to] : 0;

    /// <summary><c>update_quiet_histories</c>, search.cpp:2045-2056 — main history, low-ply
    /// history (solo ply&lt;5) e continuation history (vedi nota in testa al file per pawn
    /// history non portata).</summary>
    private void UpdateQuietHistory(Position pos, int ply, Move move, int bonus, ContinuationRef[] contRefs, bool currentInCheck)
    {
        Color us = pos.SideToMove;
        UpdateHistory(ref _mainHistory[(byte)us, move.Raw], bonus, MainHistoryLimit);

        if (ply < LowPlyHistorySize)
            UpdateHistory(ref _lowPlyHistory[ply, move.Raw], bonus * 712 / 1024, MainHistoryLimit);

        Piece pc = pos.MovedPiece(move);
        UpdateContinuationHistories(contRefs, currentInCheck, pc, move.ToSq, bonus * 750 / 1024);

        // search.cpp:2056-2057 — scala diversamente un bonus (raro, "bonus > -4") da un malus.
        UpdateHistory(ref _pawnHistory[pos.PawnKey & (PawnHistorySize - 1), (byte)pc, (byte)move.ToSq],
            bonus * (bonus > -4 ? 1104 : 459) / 1024, PawnHistoryLimit);
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

}

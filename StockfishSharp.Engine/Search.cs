// Ispirato a src/search.cpp della fonte upstream (2369 righe) ma NON un porting completo — vedi
// docs/porting-plan.md e docs/porting-master-plan.md (Flow A1). Portati con fedeltà (stesse
// costanti, stesse condizioni) i concetti fondamentali: negamax con PVS, quiescenza, transposition
// table (ora con value_to_tt/value_from_tt per i punteggi di matto — Step "value_to_tt"/
// "value_from_tt", search.cpp:1911-1953), mate distance pruning, null-move pruning, reverse
// futility pruning, LMR, Razoring (Step 8, search.cpp:989-992), Futility pruning per mossa figlia
// (Step 9, search.cpp:994-1008), aspiration windows (iterative_deepening, search.cpp:375-441).
//
// Semplificazioni deliberate in questi Step, documentate dove non ovvie:
// - CorrectionHistory ora portata con fedeltà (correction_value/to_corrected_static_eval/
//   update_correction_history, search.cpp:85-131): Step 5 (valutazione statica corretta), Step 9
//   (margine di futility) e l'aggiornamento a fine nodo (Step 23) usano tutti il vero
//   correctionValue, non più il segnaposto a 0.
// - "seekMate"/l'estimate di punteggio radice usata da Step 9 usa il punteggio dell'ITERAZIONE
//   PRECEDENTE completata (non quello del root move in corso di aggiornamento nell'iterazione
//   corrente come fa rootMoves[pvIdx].score nella fonte — richiederebbe una struttura RootMove
//   dedicata che qui non esiste, non facendo MultiPV/multi-thread).
// - Le aspiration windows usano averageScore/meanSquaredScore SENZA la media mobile pesata per
//   "effort" della fonte (search.cpp:1441-1468, richiede bookkeeping per-root-move dei nodi
//   cercati): qui averageScore = valore dell'iterazione precedente (equivalente a peso=1 sempre,
//   che è esattamente cosa fa la fonte alla PRIMA volta che una root move viene vista — qui è
//   così ad ogni iterazione). Stessa formula di ampiezza/allargamento finestra della fonte.
// cutNode è ora tracciato attraverso la ricorsione (Step 11 Internal Iterative Reduction,
// search.cpp:1048-1052, ne dipende) con la stessa convenzione di chiamata della fonte — vedi i
// commenti sui singoli punti di ricorsione. "followPV" (segue la riga principale dell'iterazione
// precedente) non è portato: la condizione di IIR qui è quindi leggermente più ampia di quella
// esatta della fonte.
// ProbCut (Step 12, la verifica vera con quiescenza + ricerca ridotta sulle catture con SEE sopra
// soglia; Step 13, la "piccola idea" solo da TT, attiva anche sotto scacco) è ora portato.
// Flow A2 (docs/porting-master-plan.md) iniziato in MovePick.cs: ButterflyHistory (main history)
// con la formula "a gravità" fedele e i bonus/malus di update_all_stats (solo il ramo mosse
// quiete). Da qui, corretta anche una semantica pre-esistente di bestMove: si aggiorna SOLO
// quando una mossa supera davvero alpha (search.cpp:1518-1520), non ogni volta che migliora il
// punteggio grezzo — un nodo "fail-low puro" (nessuna mossa batte alpha) ora lascia bestMove a
// null come nella fonte, invece di premiare arbitrariamente la prima mossa provata.
// NON ancora portati (candidati per i prossimi Step): Singular Extensions, la vera formula di
// riduzione LMR (reduction(), che dipende da una tabella reductions[]/rootDelta/statScore non
// ancora portati — qui LMR resta una riduzione fissa di 1), countermove, PawnHistory,
// LowPlyHistory, TTMoveHistory (vedi MovePick.cs), Lazy SMP (multi-thread), hindsight depth
// adjustment da priorReduction.

namespace StockfishSharp.Engine;

public sealed class SearchResult
{
    public Move? BestMove;
    public int ScoreCp;
    public long Nodes;
    public int Depth;
}

public sealed class Search
{
    private readonly TranspositionTable _tt = new();
    private readonly MovePick _movePick = new();
    private CancellationToken _ct;
    private long _nodes;

    private const int Infinity = 32001; // VALUE_INFINITE della fonte, types.h:155
    private const int MateScore = 32000; // VALUE_MATE, types.h:157

    private const int NullMoveMinDepth = 3;
    private const int NullMoveReduction = 3;
    private const int ReverseFutilityMaxDepth = 6;
    private const int ReverseFutilityMarginPerDepth = 90;
    private const int LmrMinDepth = 3;
    private const int LmrMinMoveIndex = 3;

    // Stack della fonte (search.cpp:289-298: stack[MAX_PLY+10], ss=stack+7) ridotto ai soli campi
    // che servono qui: valutazione statica (improving/opponentWorsening), mossa/pezzo mosso/scacco
    // del nodo/mossa-era-cattura per ply, per la continuation history (fino a ss-6). StackOffset=7
    // come la fonte, per poter leggere (ss-6) anche dal ply 0. Sentinella "(ss-i) non esiste o non
    // tracciata": Move.None/Piece.None (default di Array.Clear).
    private const int StackOffset = 7;
    private readonly int[] _staticEvalHistory = new int[Ply.MaxPly + StackOffset + 1];
    private readonly Move[] _currentMoveHistory = new Move[Ply.MaxPly + StackOffset + 1];
    private readonly Piece[] _movedPieceHistory = new Piece[Ply.MaxPly + StackOffset + 1];
    private readonly bool[] _inCheckHistory = new bool[Ply.MaxPly + StackOffset + 1];
    private readonly bool[] _captureStageHistory = new bool[Ply.MaxPly + StackOffset + 1];

    // Usati da Step 9 (futility pruning) per "seekMate" — vedi nota di semplificazione in testa
    // al file: qui è il punteggio/la profondità dell'ULTIMA iterazione completata, non del root
    // move in corso nell'iterazione corrente come nella fonte.
    private int _rootDepth;
    private int _lastCompletedScore = -Infinity;

    // CorrectionHistory, history.h:148-257 — differenze fra valutazione statica e punteggio di
    // ricerca, per correggere la valutazione statica in Step 5. Chiave: hash di pedoni/pezzi
    // minori/non-pedoni per colore (history.h:227-248, sizeMinus1 = CORRHIST_BASE_SIZE-1 =
    // UINT_16_HISTORY_SIZE-1) più una continuation history a parte (ss-2/ss-4).
    private const int CorrectionHistoryLimit = 1024; // CORRECTION_HISTORY_LIMIT, history.h:41
    private const int CorrHistSize = 65536; // CORRHIST_BASE_SIZE, history.h:39-40
    private readonly short[,] _pawnCorrHistory = new short[CorrHistSize, Colors.Nb];
    private readonly short[,] _minorCorrHistory = new short[CorrHistSize, Colors.Nb];
    private readonly short[,] _nonPawnWhiteCorrHistory = new short[CorrHistSize, Colors.Nb];
    private readonly short[,] _nonPawnBlackCorrHistory = new short[CorrHistSize, Colors.Nb];
    // CorrectionHistory<Continuation>, history.h:183-185 — [pezzo/casa all'ancestro ss-2 o ss-4]
    // [pezzo/casa della mossa ss-1, valutata al nodo corrente]. Le celle mai scritte condividono
    // il sentinella [Piece.None,A1,...] esattamente come la fonte condivide un'unica entry
    // &continuationCorrectionHistory[NO_PIECE][0] per tutti i ply -7..-1 prima della radice.
    private readonly short[,,,] _continuationCorrHistory = new short[PieceSlots.Nb, Squares.Nb, PieceSlots.Nb, Squares.Nb];

    public void Resize(int hashMb) => _tt.Resize(hashMb);

    public void NewGame()
    {
        _tt.Clear();
        _movePick.Clear();

        // Worker::clear(), search.cpp:696-697,708-710 — pawn/minor/nonpawn a -5, continuation a
        // +5 (segno diverso, fedele alla fonte).
        for (int k = 0; k < CorrHistSize; k++)
            for (int c = 0; c < Colors.Nb; c++)
            {
                _pawnCorrHistory[k, c] = -5;
                _minorCorrHistory[k, c] = -5;
                _nonPawnWhiteCorrHistory[k, c] = -5;
                _nonPawnBlackCorrHistory[k, c] = -5;
            }
        for (int p1 = 0; p1 < PieceSlots.Nb; p1++)
            for (int s1 = 0; s1 < Squares.Nb; s1++)
                for (int p2 = 0; p2 < PieceSlots.Nb; p2++)
                    for (int s2 = 0; s2 < Squares.Nb; s2++)
                        _continuationCorrHistory[p1, s1, p2, s2] = 5;
    }

    /// <summary><c>correction_value</c>, search.cpp:85-101 — combina le correction history
    /// hash-indicizzate (pedoni/pezzi minori/non-pedoni bianco/nero) con la continuation
    /// correction history a due livelli (ss-2, ss-4), pesate come nella fonte.</summary>
    private int CorrectionValue(Position pos, int ply)
    {
        Color us = pos.SideToMove;
        Move m = _currentMoveHistory[ply + StackOffset - 1]; // (ss-1)->currentMove

        int pcv = _pawnCorrHistory[pos.PawnKey & (CorrHistSize - 1), (byte)us];
        int micv = _minorCorrHistory[pos.MinorPieceKey & (CorrHistSize - 1), (byte)us];
        int wnpcv = _nonPawnWhiteCorrHistory[pos.NonPawnKey(Color.White) & (CorrHistSize - 1), (byte)us];
        int bnpcv = _nonPawnBlackCorrHistory[pos.NonPawnKey(Color.Black) & (CorrHistSize - 1), (byte)us];

        int cntcv;
        if (m.IsOk)
        {
            Square to = m.ToSq;
            Piece pcAtTo = pos.PieceOn(to);
            int idx2 = ply + StackOffset - 2;
            int idx4 = ply + StackOffset - 4;
            int e2 = _continuationCorrHistory[(byte)_movedPieceHistory[idx2], (byte)_currentMoveHistory[idx2].ToSq, (byte)pcAtTo, (byte)to];
            int e4 = _continuationCorrHistory[(byte)_movedPieceHistory[idx4], (byte)_currentMoveHistory[idx4].ToSq, (byte)pcAtTo, (byte)to];
            cntcv = 8761 * (e2 + e4);
        }
        else
        {
            cntcv = 64049;
        }

        return (15341 * pcv) + (10569 * micv) + (12906 * (wnpcv + bnpcv)) + cntcv;
    }

    /// <summary><c>to_corrected_static_eval</c>, search.cpp:105-107.</summary>
    private static int ToCorrectedStaticEval(int v, int cv) =>
        Math.Clamp(v + (cv / 131072), Values.TbLossInMaxPly + 1, Values.TbWinInMaxPly - 1);

    /// <summary><c>update_correction_history</c>, search.cpp:109-131 — chiamata a fine nodo
    /// (Step 23) quando la mossa migliore non è una cattura e la direzione dell'errore combacia
    /// con sopra/sotto i bound.</summary>
    private void UpdateCorrectionHistory(Position pos, int ply, int bonus)
    {
        Color us = pos.SideToMove;
        Move m = _currentMoveHistory[ply + StackOffset - 1];

        const int nonPawnWeight = 186;
        UpdateCorrHistoryEntry(ref _pawnCorrHistory[pos.PawnKey & (CorrHistSize - 1), (byte)us], bonus);
        UpdateCorrHistoryEntry(ref _minorCorrHistory[pos.MinorPieceKey & (CorrHistSize - 1), (byte)us], bonus * 150 / 128);
        UpdateCorrHistoryEntry(ref _nonPawnWhiteCorrHistory[pos.NonPawnKey(Color.White) & (CorrHistSize - 1), (byte)us], bonus * nonPawnWeight / 128);
        UpdateCorrHistoryEntry(ref _nonPawnBlackCorrHistory[pos.NonPawnKey(Color.Black) & (CorrHistSize - 1), (byte)us], bonus * nonPawnWeight / 128);

        if (m.IsOk)
        {
            Square to = m.ToSq;
            Piece pc = pos.PieceOn(to);
            int idx2 = ply + StackOffset - 2;
            int idx4 = ply + StackOffset - 4;
            UpdateCorrHistoryEntry(ref _continuationCorrHistory[(byte)_movedPieceHistory[idx2], (byte)_currentMoveHistory[idx2].ToSq, (byte)pc, (byte)to], bonus * 130 / 128);
            UpdateCorrHistoryEntry(ref _continuationCorrHistory[(byte)_movedPieceHistory[idx4], (byte)_currentMoveHistory[idx4].ToSq, (byte)pc, (byte)to], bonus * 70 / 128);
        }
    }

    /// <summary>Stessa formula "a gravità" di <c>StatsEntry::operator&lt;&lt;</c> usata in
    /// MovePick.cs — qui con D=<see cref="CorrectionHistoryLimit"/>.</summary>
    private static void UpdateCorrHistoryEntry(ref short entry, int bonus)
    {
        int clampedBonus = Math.Clamp(bonus, -CorrectionHistoryLimit, CorrectionHistoryLimit);
        int val = entry;
        entry = (short)(val + clampedBonus - (val * Math.Abs(clampedBonus) / CorrectionHistoryLimit));
    }

    /// <summary><c>value_to_tt</c>, search.cpp:1911: converte un punteggio di matto/tablebase da
    /// "distanza dal nodo corrente" a "distanza dalla radice" prima di salvarlo in TT — altrimenti
    /// una entry scritta a un ply diverso da dove viene poi letta darebbe una distanza di matto
    /// sbagliata.</summary>
    private static int ValueToTt(int v, int ply) =>
        Values.IsWin(v) ? v + ply : Values.IsLoss(v) ? v - ply : v;

    /// <summary><c>value_from_tt</c>, search.cpp:1919-1953: l'inverso, con la salvaguardia contro
    /// falsi punteggi di matto/tablebase quando la regola delle 50 mosse rende quel punteggio
    /// inaffidabile (declassato al miglior punteggio non-tablebase).</summary>
    private static int ValueFromTt(int v, int ply, int rule50Count)
    {
        if (!Values.IsValid(v)) return Values.None;

        if (Values.IsWin(v))
        {
            if (Values.IsMate(v) && MateScore - v > 100 - rule50Count) return Values.TbWinInMaxPly - 1;
            if (Values.Tb - v > 100 - rule50Count) return Values.TbWinInMaxPly - 1;
            return v - ply;
        }

        if (Values.IsLoss(v))
        {
            if (Values.IsMated(v) && MateScore + v > 100 - rule50Count) return Values.TbLossInMaxPly + 1;
            if (Values.Tb + v > 100 - rule50Count) return Values.TbLossInMaxPly + 1;
            return v + ply;
        }

        return v;
    }

    /// <summary>Iterative deepening con aspiration windows — <c>iterative_deepening</c>,
    /// search.cpp:375-441 (vedi la nota di semplificazione in testa al file per
    /// averageScore/meanSquaredScore).</summary>
    public SearchResult Search_(Position pos, int maxDepth, TimeSpan timeLimit, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeLimit);
        _ct = cts.Token;
        _nodes = 0;
        _tt.NewSearch();

        Array.Clear(_staticEvalHistory);
        for (int i = 0; i < StackOffset; i++) _staticEvalHistory[i] = Values.None; // (ss-7)..(ss-1)
        Array.Clear(_currentMoveHistory);
        Array.Clear(_movedPieceHistory);
        Array.Clear(_inCheckHistory);
        Array.Clear(_captureStageHistory);
        _lastCompletedScore = -Infinity;

        var result = new SearchResult();

        int avgScore = -Infinity;
        long meanSquaredScore = -(long)Infinity * Infinity;

        try
        {
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                _rootDepth = depth;

                int delta = 5 + (int)(Math.Abs(meanSquaredScore) / 10193);
                int alpha = Math.Max(avgScore - delta, -Infinity);
                int beta = Math.Min(avgScore + delta, Infinity);

                int bestValue;
                while (true)
                {
                    bestValue = Negamax(pos, depth, 0, alpha, beta, cutNode: false);

                    if (bestValue <= alpha)
                    {
                        beta = alpha;
                        alpha = Math.Max(bestValue - delta, -Infinity);
                    }
                    else if (bestValue >= beta)
                    {
                        alpha = Math.Max(beta - delta, alpha);
                        beta = Math.Min(bestValue + delta, Infinity);
                    }
                    else break;

                    delta += 47 * delta / 128;
                }

                avgScore = bestValue;
                meanSquaredScore = (long)bestValue * Math.Abs(bestValue);
                _lastCompletedScore = bestValue;

                var probe = _tt.Probe(pos.Key);
                result.BestMove = probe.Found ? probe.Data.Move : result.BestMove;
                result.ScoreCp = bestValue;
                result.Depth = depth;
            }
        }
        catch (OperationCanceledException)
        {
            // Iterazione in corso interrotta a metà: si tiene il risultato dell'ultima completata.
        }

        result.Nodes = _nodes;
        return result;
    }

    private int Negamax(Position pos, int depth, int ply, int alpha, int beta, bool cutNode)
    {
        _nodes++;
        if ((_nodes & 2047) == 0) _ct.ThrowIfCancellationRequested();

        bool isPvNode = beta - alpha > 1;

        // Mate distance pruning — esatta, non euristica: da questo ply il miglior esito possibile
        // è dare matto alla prossima mossa, il peggiore essere già sotto matto.
        int matingValue = MateScore - ply;
        if (matingValue < beta)
        {
            beta = matingValue;
            if (alpha >= matingValue) return matingValue;
        }
        int matedValue = -MateScore + ply;
        if (matedValue > alpha)
        {
            alpha = matedValue;
            if (beta <= matedValue) return matedValue;
        }

        if (depth <= 0) return Quiesce(pos, alpha, beta, ply);

        bool inCheck = pos.Checkers() != 0;

        var probe = _tt.Probe(pos.Key);
        int ttScore = probe.Found ? ValueFromTt(probe.Data.Value, ply, pos.Rule50Count) : Values.None;
        bool ttPv = isPvNode || (probe.Found && probe.Data.IsPv);
        bool ttCapture = probe.Found && probe.Data.Move != Move.None && pos.Capture(probe.Data.Move);

        int correctionValue = CorrectionValue(pos, ply);

        if (!isPvNode && probe.Found && probe.Data.Depth >= depth && Values.IsValid(ttScore))
        {
            if (probe.Data.Bound == Bound.Exact) return ttScore;
            if (probe.Data.Bound == Bound.Lower && ttScore >= beta) return ttScore;
            if (probe.Data.Bound == Bound.Upper && ttScore <= alpha) return ttScore;
        }

        // Step 5. Valutazione statica — to_corrected_static_eval applica ora la vera correction
        // history (CorrectionValue sopra), non più un segnaposto a 0.
        int staticEval;
        int eval;
        int unadjustedStaticEval = Values.None;

        if (inCheck)
        {
            staticEval = eval = _staticEvalHistory[ply + StackOffset - 2]; // (ss-2)->staticEval
        }
        else
        {
            unadjustedStaticEval = probe.Found && Values.IsValid(probe.Data.Eval) ? probe.Data.Eval : Evaluate.StaticEval(pos);
            staticEval = eval = ToCorrectedStaticEval(unadjustedStaticEval, correctionValue);

            if (Values.IsValid(ttScore)
                && (probe.Data.Bound == (ttScore > eval ? Bound.Lower : Bound.Upper)))
                eval = ttScore;

            if (!probe.Found)
                _tt.Save(probe.WriteIndex, pos.Key, Values.None, ttPv, Bound.None, Ply.DepthNone, Move.None, unadjustedStaticEval);
        }

        _staticEvalHistory[ply + StackOffset] = staticEval;

        bool improving = staticEval > _staticEvalHistory[ply + StackOffset - 2]; // (ss-2)->staticEval
        bool opponentWorsening = staticEval > -_staticEvalHistory[ply + StackOffset - 1]; // -(ss-1)->staticEval

        if (!inCheck)
        {
            // Step 8. Razoring — search.cpp:989-992: se la valutazione statica è già molto sotto
            // alpha (margine che cresce col quadrato della profondità), la posizione non si
            // riprenderà: si passa direttamente alla quiescenza.
            if (!isPvNode && eval < alpha - 482 * depth * depth)
                return Quiesce(pos, alpha, beta, ply);

            // Step 9. Futility pruning: nodo figlio — search.cpp:994-1008. La condizione sulla
            // profondità (6 se si "cerca il matto", 19 altrimenti) non va tarata: serve a non
            // troncare la ricerca quando un punteggio già alto suggerisce un matto vicino.
            bool seekMate = _rootDepth >= 16 && Math.Abs(_lastCompletedScore) >= 2000;
            if (!ttPv && depth < (seekMate ? 6 : 19) && eval >= beta
                && (!probe.Found || probe.Data.Move == Move.None || ttCapture)
                && !Values.IsLoss(beta) && !Values.IsWin(eval))
            {
                int futilityMult = Math.Min(45 + (depth * 4), 85);
                futilityMult -= 20 * (probe.Found ? 0 : 1);

                int futilityMargin = (futilityMult * depth)
                    - (((2789 * (improving ? 1 : 0)) + (335 * (opponentWorsening ? 1 : 0))) * futilityMult / 1024)
                    + (Math.Abs(correctionValue) / 198435);

                if (eval - futilityMargin >= beta)
                    return ((661 * beta) + (363 * eval)) / 1024;
            }

            // Null-move pruning: mai sotto scacco né in nodi PV, mai con solo re+pedoni (zugzwang).
            if (!isPvNode && depth >= NullMoveMinDepth && HasNonPawnMaterial(pos, pos.SideToMove))
            {
                var nullSt = new StateInfo();
                pos.DoNullMove(nullSt);
                int nullScore = -Negamax(pos, depth - 1 - NullMoveReduction, ply + 1, -beta, -beta + 1, cutNode: false);
                pos.UndoNullMove();

                if (nullScore >= beta && Math.Abs(nullScore) < MateScore - Ply.MaxPly) return beta;
            }

            // Step 11. Internal iterative reduction — search.cpp:1048-1052: a profondità
            // sufficiente, riduce la profondità nei nodi PV/Cut senza una mossa in TT (una TT
            // vuota qui è un segnale che questo nodo non è mai stato esplorato a sufficienza).
            // "allNode" nella fonte è !(PvNode||cutNode); "followPV" (segue la riga principale
            // dell'iterazione precedente) non è portato — condizione qui leggermente più ampia.
            bool allNode = !isPvNode && !cutNode;
            if (!allNode && depth >= 6 && (!probe.Found || probe.Data.Move == Move.None))
                depth--;

            // Step 12. ProbCut — search.cpp:1054-1096: se una cattura (o promozione) "abbastanza
            // buona" (SEE sopra la soglia probCutBeta-staticEval) regge una verifica di
            // quiescenza e, se serve, una ricerca ridotta, il nodo genitore può essere potato: la
            // mossa appena giocata sarebbe comunque troppo costosa per l'avversario.
            int probCutBeta = beta + 241 - (64 * (improving ? 1 : 0));
            if (depth >= 3 && !Values.IsDecisive(beta) && !(Values.IsValid(ttScore) && ttScore < probCutBeta))
            {
                int probCutDepth = depth - (improving ? 5 : 3);
                var probCutCandidates = new List<Move>();
                MoveGen.Generate(GenType.Captures, pos, probCutCandidates);

                foreach (var m in probCutCandidates)
                {
                    if (!pos.Legal(m)) continue;
                    if (!pos.SeeGe(m, probCutBeta - staticEval)) continue;

                    var pcSt = new StateInfo();
                    pos.DoMove(m, pcSt);

                    int pcValue = -Quiesce(pos, -probCutBeta, -probCutBeta + 1, ply + 1);

                    if (pcValue >= probCutBeta && probCutDepth > 0)
                        pcValue = -Negamax(pos, probCutDepth, ply + 1, -probCutBeta, -probCutBeta + 1, cutNode: !cutNode);

                    pos.UndoMove(m);

                    if (pcValue >= probCutBeta)
                    {
                        _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(pcValue, ply), ttPv, Bound.Lower, probCutDepth + 1, m, unadjustedStaticEval);
                        if (!Values.IsDecisive(pcValue)) return pcValue - (probCutBeta - beta);
                    }
                }
            }
        }

        // Step 13. Una piccola idea di ProbCut — search.cpp:1100-1104: se la TT garantisce già un
        // punteggio molto sopra un beta ancora più alto ("probCutBeta"), non serve nemmeno
        // generare le mosse. A differenza dello Step 12 questo vale ANCHE sotto scacco.
        int probCutBeta13 = beta + 428;
        if ((probe.Data.Bound & Bound.Lower) != Bound.None && probe.Data.Depth >= depth - 4
            && Values.IsValid(ttScore) && ttScore >= probCutBeta13
            && !Values.IsDecisive(beta) && !Values.IsDecisive(ttScore))
            return probCutBeta13;

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        if (moves.Count == 0)
            return inCheck ? -(MateScore - ply) : 0;

        var contRefs = BuildContinuationRefs(ply);
        _movePick.OrderMoves(pos, moves, ply, probe.Data.Move, contRefs);

        int origAlpha = alpha;
        int value = -Infinity;
        Move? bestMove = null;

        // search.cpp:761-762/1543-1551: mosse quiete/catture provate ma non risultate la
        // migliore, per aggiornare le loro statistiche di ordinamento a fine ciclo (Step 23).
        var quietsSearched = new List<Move>();
        var capturesSearched = new List<Move>();
        const int SearchedListCapacity = 32; // SEARCHEDLIST_CAPACITY, search.cpp:73

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            bool tactical = pos.Capture(m) || m.TypeOf == MoveType.Promotion;
            bool captureStage = pos.CaptureStage(m);
            bool givesCheck = pos.GivesCheck(m);

            // Stack::currentMove per la continuation history del ply successivo (MovePick, i suoi
            // (ss-1)..(ss-6)) — va registrata PRIMA di fare la mossa, "moved_piece" guarda la casa
            // di partenza sulla posizione attuale; inCheck/captureStage selezionano la tabella
            // come in do_move, search.cpp:663-671.
            _movedPieceHistory[ply + StackOffset] = pos.MovedPiece(m);
            _currentMoveHistory[ply + StackOffset] = m;
            _inCheckHistory[ply + StackOffset] = inCheck;
            _captureStageHistory[ply + StackOffset] = captureStage;

            var st = new StateInfo();
            pos.DoMove(m, st, givesCheck);

            // cutNode del figlio — search.cpp:1372/1387/1403/1422: la ricerca a finestra piena di
            // "Step 20" (solo nei nodi PV, sulla prima mossa o dopo un fallimento alto) passa
            // sempre cutNode=false; la ricerca a finestra nulla (Step 18/19) passa true quando è
            // ridotta da LMR, altrimenti !cutNode del genitore.
            int score;
            if (i == 0)
            {
                score = -Negamax(pos, depth - 1, ply + 1, -beta, -alpha, cutNode: isPvNode ? false : !cutNode);
            }
            else
            {
                bool lmrEligible = depth >= LmrMinDepth && i >= LmrMinMoveIndex && !inCheck && !tactical && !givesCheck;
                int reduction = lmrEligible ? 1 : 0;
                int probeDepth = Math.Max(0, depth - 1 - reduction);

                score = -Negamax(pos, probeDepth, ply + 1, -alpha - 1, -alpha, cutNode: lmrEligible ? true : !cutNode);
                if (score > alpha)
                    score = -Negamax(pos, depth - 1, ply + 1, -beta, -alpha, cutNode: false);
            }

            pos.UndoMove(m);

            // Step 22, search.cpp:1514-1541: bestMove si aggiorna SOLO quando la mossa supera
            // davvero alpha — un fail-low puro (nessuna mossa batte alpha) lascia bestMove a null,
            // esattamente come nella fonte (lì "inc", un fattore di parità che promuove mosse a
            // pari punteggio, non è portato: raffinamento minore, non struttura).
            if (score > value)
            {
                value = score;
                if (score > alpha)
                {
                    bestMove = m;
                    alpha = score;
                    if (alpha >= beta)
                    {
                        _movePick.RecordKiller(pos, m, ply);
                        break;
                    }
                }
            }

            // search.cpp:1543-1551: "se la mossa è peggiore di una già provata, ricordarla per
            // aggiornarne le statistiche dopo" — qui bestMove riflette già l'eventuale
            // aggiornamento appena fatto sopra, quindi la mossa che ha appena causato il taglio
            // beta (bestMove) non entra mai in queste liste (il break sopra la esclude comunque).
            if (i + 1 <= SearchedListCapacity && m != bestMove)
            {
                if (captureStage) capturesSearched.Add(m); else quietsSearched.Add(m);
            }
        }

        // search.cpp:1558-1560: smussa bestValue verso beta quando fallisce alto per punteggi non
        // decisivi — evita che un singolo taglio beta "profondo poco" venga preso alla lettera.
        if (value >= beta && !Values.IsDecisive(value) && !Values.IsDecisive(alpha))
            value = ((value * depth) + beta) / (depth + 1);

        if (bestMove != null)
            _movePick.UpdateStats(pos, bestMove.Value, quietsSearched, capturesSearched, depth, probe.Data.Move, isPvNode, contRefs, inCheck);

        // search.cpp:1629-1638: aggiorna la correction history solo se la mossa migliore non è una
        // cattura e la direzione dell'errore (bestValue sopra/sotto la valutazione statica)
        // combacia con l'esito (una bestMove trovata o un fail-low puro).
        if (!inCheck && !(bestMove != null && pos.Capture(bestMove.Value))
            && (value > staticEval) == (bestMove != null))
        {
            int chBonus = Math.Clamp((value - staticEval) * depth * (bestMove != null ? 12 : 18) / 128,
                -CorrectionHistoryLimit / 4, CorrectionHistoryLimit / 4);
            UpdateCorrectionHistory(pos, ply, 1061 * chBonus / 1024);
        }

        var flag = value <= origAlpha ? Bound.Upper : value >= beta ? Bound.Lower : Bound.Exact;
        _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(value, ply), ttPv, flag, depth, bestMove ?? Move.None, unadjustedStaticEval);

        return value;
    }

    /// <summary>Ricerca di quiescenza: estende a catture (SEE non negativa) finché la posizione
    /// non è "tranquilla" — evita di valutare a metà di uno scambio.</summary>
    private int Quiesce(Position pos, int alpha, int beta, int ply)
    {
        _nodes++;

        bool inCheck = pos.Checkers() != 0;
        int standPat = inCheck ? -MateScore + ply : Evaluate.StaticEval(pos);

        if (!inCheck)
        {
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        // Sempre mosse LEGALI (non solo pseudo-legali): una cattura pseudo-legale può comunque
        // scoprire scacco al proprio re (pezzo inchiodato) — generare direttamente le legali
        // costa un po' di più ma evita quel caso limite senza un controllo a parte.
        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        var candidates = new List<Move>();
        foreach (var m in moves)
        {
            if (inCheck) { candidates.Add(m); continue; }
            if (!pos.Capture(m)) continue;
            if (!pos.SeeGe(m)) continue;
            candidates.Add(m);
        }

        if (inCheck && candidates.Count == 0)
            return -(MateScore - ply);

        // Continuation history non tracciata in quiescenza (Quiesce non scrive
        // _currentMoveHistory/_movedPieceHistory) — array vuoto disattiva il termine, resta solo
        // main history + killer/SEE come prima di questo Step.
        _movePick.OrderMoves(pos, candidates, ply, Move.None, EmptyContinuationRefs);

        foreach (var m in candidates)
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            int score = -Quiesce(pos, -beta, -alpha, ply + 1);
            pos.UndoMove(m);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    private static readonly ContinuationRef[] EmptyContinuationRefs = new ContinuationRef[6];

    /// <summary>Costruisce <c>(ss-1)..(ss-6)-&gt;currentMove</c> per la continuation history —
    /// vedi MovePick.cs. Con StackOffset=7 l'indice minimo (ply=0, i=6) è 1, sempre non negativo.</summary>
    private ContinuationRef[] BuildContinuationRefs(int ply)
    {
        var refs = new ContinuationRef[6];
        for (int i = 1; i <= 6; i++)
        {
            int idx = ply + StackOffset - i;
            if (_currentMoveHistory[idx].IsOk)
                refs[i - 1] = new ContinuationRef(true, _inCheckHistory[idx], _captureStageHistory[idx], _movedPieceHistory[idx], _currentMoveHistory[idx].ToSq);
        }
        return refs;
    }

    private static bool HasNonPawnMaterial(Position pos, Color c)
    {
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            Piece pc = pos.PieceOn(s);
            if (pc == Piece.None) continue;
            if (Types.ColorOf(pc) != c) continue;
            var pt = Types.TypeOf(pc);
            if (pt != PieceType.Pawn && pt != PieceType.King) return true;
        }

        return false;
    }
}

// Ispirato a src/search.cpp della fonte upstream (2369 righe) ma NON un porting completo — vedi
// docs/porting-plan.md e docs/porting-master-plan.md (Flow A1). Portati con fedeltà (stesse
// costanti, stesse condizioni) i concetti fondamentali: negamax con PVS, quiescenza, transposition
// table (ora con value_to_tt/value_from_tt per i punteggi di matto — Step "value_to_tt"/
// "value_from_tt", search.cpp:1911-1953), mate distance pruning, null-move pruning, reverse
// futility pruning, LMR, Razoring (Step 8, search.cpp:989-992), Futility pruning per mossa figlia
// (Step 9, search.cpp:994-1008), aspiration windows (iterative_deepening, search.cpp:375-441).
//
// Semplificazioni deliberate in questi Step, documentate dove non ovvie:
// - correctionValue è sempre 0 (correction history non portata — Flow A5/A1, history.h): la
//   formula del margine di futility resta quella della fonte con questo input a zero.
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
// NON ancora portati (candidati per i prossimi Step): ProbCut (entrambi i rami), Singular
// Extensions, correction history, la vera formula di riduzione LMR (reduction(), che dipende da
// una tabella reductions[]/rootDelta/statScore non ancora portati — qui LMR resta una riduzione
// fissa di 1), continuation history/countermove in movepick (vedi MovePick.cs), Lazy SMP
// (multi-thread), hindsight depth adjustment da priorReduction.

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

    // Cronologia della valutazione statica per ply, per calcolare improving/opponentWorsening —
    // Stack::staticEval della fonte, ma qui solo l'array di interi che serve (niente
    // continuationHistory/pv/ecc., non ancora portati). Indice = ply + 2, per poter leggere
    // (ss-2) anche dal ply 0 — search.cpp:289-298 alloca stack[MAX_PLY+10] con lo stesso scopo.
    private readonly int[] _staticEvalHistory = new int[Ply.MaxPly + 3];

    // Usati da Step 9 (futility pruning) per "seekMate" — vedi nota di semplificazione in testa
    // al file: qui è il punteggio/la profondità dell'ULTIMA iterazione completata, non del root
    // move in corso nell'iterazione corrente come nella fonte.
    private int _rootDepth;
    private int _lastCompletedScore = -Infinity;

    public void Resize(int hashMb) => _tt.Resize(hashMb);

    public void NewGame()
    {
        _tt.Clear();
        _movePick.Clear();
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
        _staticEvalHistory[0] = Values.None;
        _staticEvalHistory[1] = Values.None;
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

        if (!isPvNode && probe.Found && probe.Data.Depth >= depth && Values.IsValid(ttScore))
        {
            if (probe.Data.Bound == Bound.Exact) return ttScore;
            if (probe.Data.Bound == Bound.Lower && ttScore >= beta) return ttScore;
            if (probe.Data.Bound == Bound.Upper && ttScore <= alpha) return ttScore;
        }

        // Step 5. Valutazione statica — position.cpp non tiene una correction history (non
        // portata), quindi "to_corrected_static_eval" qui è solo il clamp fuori dal range
        // tablebase, con correctionValue fisso a 0 (vedi nota in testa al file).
        int staticEval;
        int eval;
        int unadjustedStaticEval = Values.None;

        if (inCheck)
        {
            staticEval = eval = _staticEvalHistory[ply]; // (ss-2)->staticEval
        }
        else
        {
            unadjustedStaticEval = probe.Found && Values.IsValid(probe.Data.Eval) ? probe.Data.Eval : Evaluate.StaticEval(pos);
            staticEval = eval = Math.Clamp(unadjustedStaticEval, Values.TbLossInMaxPly + 1, Values.TbWinInMaxPly - 1);

            if (Values.IsValid(ttScore)
                && (probe.Data.Bound == (ttScore > eval ? Bound.Lower : Bound.Upper)))
                eval = ttScore;

            if (!probe.Found)
                _tt.Save(probe.WriteIndex, pos.Key, Values.None, ttPv, Bound.None, Ply.DepthNone, Move.None, unadjustedStaticEval);
        }

        _staticEvalHistory[ply + 2] = staticEval;

        bool improving = staticEval > _staticEvalHistory[ply]; // (ss-2)->staticEval
        bool opponentWorsening = staticEval > -_staticEvalHistory[ply + 1]; // -(ss-1)->staticEval

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
                    - (((2789 * (improving ? 1 : 0)) + (335 * (opponentWorsening ? 1 : 0))) * futilityMult / 1024);

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
        }

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        if (moves.Count == 0)
            return inCheck ? -(MateScore - ply) : 0;

        _movePick.OrderMoves(pos, moves, ply, probe.Data.Move);

        int origAlpha = alpha;
        int value = -Infinity;
        Move? bestMove = null;

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            bool tactical = pos.Capture(m) || m.TypeOf == MoveType.Promotion;
            bool givesCheck = pos.GivesCheck(m);

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

            if (score > value) { value = score; bestMove = m; }
            if (value > alpha) alpha = value;
            if (alpha >= beta)
            {
                _movePick.RecordCutoff(pos, m, ply, depth);
                break;
            }
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

        _movePick.OrderMoves(pos, candidates, ply, Move.None);

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

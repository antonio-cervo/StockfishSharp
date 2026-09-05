// Ispirato a src/search.cpp della fonte upstream (2369 righe) ma NON un porting completo — vedi
// docs/porting-plan.md. Portati con fedeltà i concetti fondamentali (negamax con PVS, quiescenza,
// transposition table, mate distance pruning, null-move pruning, reverse futility pruning, LMR) e
// verificati con la stessa disciplina di ACMyChess: test di matto forzato + nessuna regressione
// prima di considerarli acquisiti. NON ancora portati (elenco completo, tutti candidati per un
// affinamento futuro): ProbCut (entrambi i rami), Singular Extensions, aspiration windows,
// internal iterative reduction, futility pruning per singola mossa, razoring, multi-cut,
// continuation history/countermove in movepick (vedi MovePick.cs), Lazy SMP (multi-thread).

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

    public void Resize(int hashMb) => _tt.Resize(hashMb);

    public void NewGame()
    {
        _tt.Clear();
        _movePick.Clear();
    }

    /// <summary>Iterative deepening con aspiration window semplice: ogni profondità riparte da
    /// finestra piena (nessun restringimento sul punteggio dell'iterazione precedente per ora —
    /// candidato per un affinamento futuro, come le altre tecniche elencate in cima al file).</summary>
    public SearchResult Search_(Position pos, int maxDepth, TimeSpan timeLimit, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeLimit);
        _ct = cts.Token;
        _nodes = 0;
        _tt.NewSearch();

        var result = new SearchResult();

        try
        {
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                int score = Negamax(pos, depth, 0, -Infinity, Infinity);
                var probe = _tt.Probe(pos.Key);
                result.BestMove = probe.Found ? probe.Data.Move : result.BestMove;
                result.ScoreCp = score;
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

    private int Negamax(Position pos, int depth, int ply, int alpha, int beta)
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
        if (!isPvNode && probe.Found && probe.Data.Depth >= depth)
        {
            int ttScore = probe.Data.Value;
            if (probe.Data.Bound == Bound.Exact) return ttScore;
            if (probe.Data.Bound == Bound.Lower && ttScore >= beta) return ttScore;
            if (probe.Data.Bound == Bound.Upper && ttScore <= alpha) return ttScore;
        }

        int staticEval = inCheck ? -Infinity : Evaluate.StaticEval(pos);

        // Reverse futility pruning: a profondità bassa, se la valutazione statica supera già beta
        // di un margine proporzionale alla profondità residua, si taglia senza generare mosse.
        if (!inCheck && !isPvNode && depth <= ReverseFutilityMaxDepth
            && staticEval - ReverseFutilityMarginPerDepth * depth >= beta
            && Math.Abs(beta) < MateScore - Ply.MaxPly)
            return beta;

        // Null-move pruning: mai sotto scacco né in nodi PV, mai con solo re+pedoni (zugzwang).
        if (!inCheck && !isPvNode && depth >= NullMoveMinDepth && HasNonPawnMaterial(pos, pos.SideToMove))
        {
            var nullSt = new StateInfo();
            pos.DoNullMove(nullSt);
            int nullScore = -Negamax(pos, depth - 1 - NullMoveReduction, ply + 1, -beta, -beta + 1);
            pos.UndoNullMove();

            if (nullScore >= beta && Math.Abs(nullScore) < MateScore - Ply.MaxPly) return beta;
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

            int score;
            if (i == 0)
            {
                score = -Negamax(pos, depth - 1, ply + 1, -beta, -alpha);
            }
            else
            {
                bool lmrEligible = depth >= LmrMinDepth && i >= LmrMinMoveIndex && !inCheck && !tactical && !givesCheck;
                int reduction = lmrEligible ? 1 : 0;
                int probeDepth = Math.Max(0, depth - 1 - reduction);

                score = -Negamax(pos, probeDepth, ply + 1, -alpha - 1, -alpha);
                if (score > alpha)
                    score = -Negamax(pos, depth - 1, ply + 1, -beta, -alpha);
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
        _tt.Save(probe.WriteIndex, pos.Key, value, isPvNode, flag, depth, bestMove ?? Move.None, staticEval);

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

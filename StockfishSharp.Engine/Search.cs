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
// La vera formula di riduzione LMR (reduction(), search.cpp:1885-1888 — tabella reductions[]
// logaritmica + rootDelta + statScore da MovePick) e lo Step 15 (potatura a profondità bassa:
// late move pruning, futility/SEE per catture, futility+potatura da history+SEE per mosse quiete,
// search.cpp:1164-1232) sono ora portati, ricontrollati riga per riga contro la fonte.
//
// Singular Extensions (Step 16, search.cpp:1234-1303) ora portate: ricerca di verifica sulla
// STESSA posizione (stesso ply, con la mossa di TT esclusa via nuovo parametro excludedMove su
// Negamax) per stabilire se quella mossa è "l'unica buona" (da estendere) o no (multi-cut /
// estensione negativa). Richiede is_shuffling() (search.cpp:153-160) e riuso della valutazione
// statica già calcolata quando excludedMove è impostata (Step 5, search.cpp:831-832).
//
// Questo ha CONFERMATO l'ipotesi del caveat sullo Step 15 (vedi commit precedente): sulla stessa
// posizione con promozione a donna vincente, aggiungendo le Singular Extensions la mossa ora
// resta stabile su d7c8q da depth 8 in poi con cronologia "scaldata" da ricerche precedenti sulla
// stessa posizione (come avviene naturalmente dentro l'iterative deepening di UNA singola "go
// depth N", che scalda da profondità 1). Resta un residuo di instabilità a "freddo" — la
// primissima "go depth N" su questa posizione senza ricerche precedenti può ancora dare d7c8r a
// depth 9 — non risolto: probabile conseguenza delle parti ancora mancanti (generazione a stadi
// vera, multi-cut, resto della history in OrderMoves) che nella fonte concorrono tutte alla
// stabilità fin dalle prime iterazioni.
//
// Step 16 include già multi-cut (il ramo "singularScore>=beta" che pota l'intero sottoalbero) ed
// estensione negativa (il ramo "else if ttValue>=beta||cutNode").
//
// Step 23, ramo "bonus per il countermove che ha causato il fail-low puro" (search.cpp:1578-1609)
// ora portato: quando nessuna mossa del nodo batte alpha, premia (se quieta) o rinforza (se
// cattura) la mossa del GENITORE che ci ha portato qui. Richiede due nuove cronologie per ply
// (statScore, moveCount) e Position.CapturedPiece() (già esisteva per l'undo, riusata qui).
// Miglioramento incrementale sul caveat dello Step 15/16: la posizione di prova resta stabile su
// d7c8q a depth 7-10 (prima solo 8-10), ancora non a depth 6 e 12 — residuo non risolto.
//
// Hindsight depth adjustment da priorReduction ora portato (search.cpp:807-808,866-870): se il
// genitore ci ha ridotti molto (LMR) ma la sua posizione non peggiora, un ply in più qui compensa
// una riduzione forse eccessiva; se ci ha ridotti un po' e le due valutazioni statiche combinate
// sembrano già buone, un ply in meno evita di scavare inutilmente.
//
// Rilevazione patta/ripetizione ora portata (Flow A5, position.cpp:1496-1568 + tabelle cuckoo di
// Marcel van Kervinck in Zobrist.cs): Step 2 (patta immediata) e il controllo "ripetizione
// imminente" a inizio search() (search.cpp:736-742), entrambi mai portati prima — il motore prima
// non rilevava MAI patte per ripetizione o regola delle 50 mosse durante la ricerca.
//
// NON ancora portato: Lazy SMP (Flow C).

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
    // N9 — accumulatore NNUE aggiornato in modo incrementale invece di ricalcolato da zero a ogni
    // Evaluate.StaticEval; sincronizzato con la ricerca via Push()/Pop() attorno a ogni DoMove/
    // UndoMove REALE (mai per il null-move, search.cpp:674-679/686: nessun pezzo si muove, quindi
    // l'accumulatore resta valido così com'è).
    private readonly Nnue.AccumulatorStack _accumulatorStack = new();
    private CancellationToken _ct;
    private long _nodes;

    private const int Infinity = 32001; // VALUE_INFINITE della fonte, types.h:155
    private const int MateScore = 32000; // VALUE_MATE, types.h:157

    private const int NullMoveMinDepth = 3;
    private const int NullMoveReduction = 3;
    private const int ReverseFutilityMaxDepth = 6;
    private const int ReverseFutilityMarginPerDepth = 90;

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
    // Stack::statScore/moveCount della fonte — per il bonus "countermove" di Step 23
    // (search.cpp:1578-1601), che legge (ss-1)->statScore e (ss-1)->moveCount.
    private readonly int[] _statScoreHistory = new int[Ply.MaxPly + StackOffset + 1];
    private readonly int[] _moveCountHistory = new int[Ply.MaxPly + StackOffset + 1];
    // Stack::reduction della fonte — quanto il GENITORE ha ridotto la profondità per arrivare a
    // questo nodo con una ricerca LMR (search.cpp:1371,1373: impostato solo per la durata di
    // quella singola chiamata, poi azzerato) — letto come (ss-1) per l'hindsight depth adjustment
    // (search.cpp:866-870).
    private readonly int[] _reductionHistory = new int[Ply.MaxPly + StackOffset + 1];
    // Stack::cutoffCnt della fonte — quanti tagli beta ha causato il nodo a QUESTO ply, azzerato
    // dal nodo due ply più in alto (Step 1, search.cpp:810: "(ss+2)->cutoffCnt=0") prima di
    // iniziare il proprio ciclo mosse, e letto dal genitore immediato (ss+1) in Step 18 per la
    // riduzione LMR.
    private readonly int[] _cutoffCntHistory = new int[Ply.MaxPly + StackOffset + 3];

    // Buffer di MovePicker riusati per livello di profondità (uno per ply, mai per due nodi
    // contemporaneamente attivi allo stesso ply: Negamax(depth<=0) delega SEMPRE a Quiesce prima di
    // costruire il proprio MovePicker, quindi i due non si sovrappongono mai sullo stesso ply) —
    // evita che ogni nodo della ricerca allochi ~1.5KB sull'heap (vedi nota in testa a
    // MovePicker.cs), a differenza della fonte dove "moves[MAX_MOVES]" vive sullo stack C++.
    private readonly Move[][] _mpMoveBufs = BuildPerPlyMoveBufs();
    private readonly int[][] _mpValueBufs = BuildPerPlyValueBufs();
    private readonly List<Move>[] _mpGenBufs = BuildPerPlyGenBufs();

    private static Move[][] BuildPerPlyMoveBufs()
    {
        var bufs = new Move[Ply.MaxPly + 1][];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new Move[Ply.MaxMoves];
        return bufs;
    }

    private static int[][] BuildPerPlyValueBufs()
    {
        var bufs = new int[Ply.MaxPly + 1][];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new int[Ply.MaxMoves];
        return bufs;
    }

    private static List<Move>[] BuildPerPlyGenBufs()
    {
        var bufs = new List<Move>[Ply.MaxPly + 1];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new List<Move>(Ply.MaxMoves);
        return bufs;
    }

    // reductions[], search.cpp:712-713 — tabella logaritmica precalcolata, usata da Reduction().
    private static readonly int[] Reductions = BuildReductions();
    private static int[] BuildReductions()
    {
        var r = new int[Ply.MaxMoves];
        for (int i = 1; i < r.Length; i++)
            r[i] = (int)(2872 / 128.0 * Math.Log(i));
        return r;
    }

    // lmrDivisor[], search.cpp:55-56 — usato dallo Step 15 per scalare il contributo della
    // history quieta a "lmrDepth" (indice = min(depth,16)-1).
    private static readonly int[] LmrDivisor =
        [3637, 2787, 2761, 2939, 3171, 3347, 3147, 2762, 2772, 3106, 3107, 3060, 3112, 2991, 3090, 3542];

    // rootDelta, search.cpp:394 — ampiezza della finestra di aspiration alla radice PER QUESTO
    // tentativo (si allarga durante il ciclo fallisce-alto/basso), usata da Reduction() come
    // termine di confronto con l'ampiezza locale del nodo corrente.
    private int _rootDelta = Infinity;

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

    /// <summary><c>Search::Worker::reduction</c>, search.cpp:1885-1888 — quanto ridurre la
    /// profondità per una mossa, in "milliply" (da dividere per 1024 per ottenere ply interi).
    /// <paramref name="delta"/> è l'ampiezza LOCALE della finestra alfa-beta al nodo corrente
    /// (non <see cref="_rootDelta"/>, l'ampiezza alla radice — il rapporto fra le due è uno dei
    /// termini della formula).</summary>
    private int Reduction(bool improving, int depth, int moveNumber, int delta)
    {
        int reductionScale = Reductions[Math.Min(depth, Ply.MaxMoves - 1)] * Reductions[Math.Min(moveNumber, Ply.MaxMoves - 1)];
        return reductionScale - (delta * 577 / _rootDelta) + ((improving ? 0 : 1) * reductionScale * 197 / 512) + 982;
    }

    /// <summary><c>value_to_tt</c>, search.cpp:1911: converte un punteggio di matto/tablebase da
    /// "distanza dal nodo corrente" a "distanza dalla radice" prima di salvarlo in TT — altrimenti
    /// una entry scritta a un ply diverso da dove viene poi letta darebbe una distanza di matto
    /// sbagliata.</summary>
    /// <summary><c>value_draw</c>, search.cpp:134 — piccola componente casuale (±1, dal bit del
    /// contatore nodi) per evitare la "cecità da tripla ripetizione" quando due rami portano
    /// entrambi a una patta ma uno la raggiunge più a fondo dell'altro.</summary>
    private int ValueDraw() => Values.Draw - 1 + (int)(_nodes & 0x2);

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
        _movePick.ResetForSearch(); // lowPlyHistory.fill(102), search.cpp:326
        _accumulatorStack.Reset(); // AccumulatorStack::reset, nnue_accumulator.cpp:71-77

        Array.Clear(_staticEvalHistory);
        for (int i = 0; i < StackOffset; i++) _staticEvalHistory[i] = Values.None; // (ss-7)..(ss-1)
        Array.Clear(_currentMoveHistory);
        Array.Clear(_movedPieceHistory);
        Array.Clear(_inCheckHistory);
        Array.Clear(_captureStageHistory);
        Array.Clear(_cutoffCntHistory);
        Array.Clear(_statScoreHistory);
        Array.Clear(_moveCountHistory);
        Array.Clear(_reductionHistory);
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
                    _rootDelta = beta - alpha; // search.cpp:394, ricalcolato a ogni tentativo
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

    private int Negamax(Position pos, int depth, int ply, int alpha, int beta, bool cutNode, Move excludedMove = default)
    {
        _nodes++;
        if ((_nodes & 2047) == 0) _ct.ThrowIfCancellationRequested();

        bool isPvNode = beta - alpha > 1;
        bool allNode = !isPvNode && !cutNode; // search.cpp:726 — !(PvNode||cutNode)
        // search.cpp:727 — usato da Step 9 (futility) e Step 16 (Singular Extensions); vedi la
        // nota di semplificazione in testa al file sull'estimate di punteggio radice.
        bool seekMate = _rootDepth >= 16 && Math.Abs(_lastCompletedScore) >= 2000;

        // Step 2. Controllo di patta immediata — search.cpp:787-790 (qui senza il controllo di
        // ricerca interrotta, gestito a parte da _ct.ThrowIfCancellationRequested sopra).
        if (ply != 0)
        {
            if (pos.IsDraw(ply) || ply >= Ply.MaxPly)
                return ply >= Ply.MaxPly && pos.Checkers() == 0 ? Evaluate.StaticEval(pos, _accumulatorStack) : ValueDraw();
        }

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

        // Controllo "ripetizione imminente" — search.cpp:736-742: se esiste una mossa disponibile
        // che pareggerebbe per ripetizione e quel pareggio batte già alpha, tronca qui invece di
        // esplorare il sottoalbero per scoprirlo più a fondo.
        if (ply != 0 && alpha < Values.Draw && pos.UpcomingRepetition(ply))
        {
            alpha = ValueDraw();
            if (alpha >= beta) return alpha;
        }

        bool inCheck = pos.Checkers() != 0;

        // search.cpp:810 — "(ss+2)->cutoffCnt=0": azzera il contatore di tagli beta a due ply più
        // in basso, che i NIPOTI (letto come "(ss+1)" dai figli quando calcolano la loro
        // riduzione LMR in Step 18) accumuleranno nel corso del ciclo mosse di questo nodo.
        _cutoffCntHistory[ply + StackOffset + 2] = 0;

        // search.cpp:807-808 — "consuma" la riduzione che il GENITORE ha applicato per arrivare
        // qui con LMR (0 se non è stata una ricerca ridotta), poi la azzera: serve solo una volta,
        // all'hindsight depth adjustment sotto.
        int priorReduction = _reductionHistory[ply + StackOffset - 1];
        _reductionHistory[ply + StackOffset - 1] = 0;

        var probe = _tt.Probe(pos.Key);
        int ttScore = probe.Found ? ValueFromTt(probe.Data.Value, ply, pos.Rule50Count) : Values.None;
        bool ttPv = isPvNode || (probe.Found && probe.Data.IsPv);
        bool ttCapture = probe.Found && probe.Data.Move != Move.None && pos.Capture(probe.Data.Move);

        int correctionValue = CorrectionValue(pos, ply);

        if (excludedMove == default && !isPvNode && probe.Found && probe.Data.Depth >= depth && Values.IsValid(ttScore))
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
        else if (excludedMove != default)
        {
            // Ricerca di verifica delle Singular Extensions (Step 16): stessa posizione, stesso
            // ply della chiamata esterna — riusa la valutazione statica già calcolata lì invece
            // di ricalcolarla (search.cpp:831-832).
            staticEval = eval = unadjustedStaticEval = _staticEvalHistory[ply + StackOffset];
        }
        else
        {
            unadjustedStaticEval = probe.Found && Values.IsValid(probe.Data.Eval) ? probe.Data.Eval : Evaluate.StaticEval(pos, _accumulatorStack);
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

        // Hindsight adjustment of reductions, search.cpp:866-870: se il genitore ci ha ridotti
        // molto ma la sua posizione non sta peggiorando, forse ha ridotto troppo — un ply in più
        // qui compensa; se ci ha ridotti un po' e le due valutazioni statiche combinate sembrano
        // già buone per chi muove, un ply in meno evita di scavare inutilmente.
        if (priorReduction >= 3 && !opponentWorsening) depth++;
        if (priorReduction >= 2 && depth >= 2 && staticEval + _staticEvalHistory[ply + StackOffset - 1] > 166) depth--;

        if (!inCheck)
        {
            // "Use static evaluation difference to improve quiet move ordering", search.cpp:978-
            // 986: non collegato a un taglio o a bestMove — se la mossa del genitore non era né
            // sotto scacco né una cattura, il segno/ampiezza della sorpresa fra la sua valutazione
            // statica e quella di qui aggiorna sempre la sua main history (e, se non era un
            // pedone/promozione e non c'è già un hit di TT qui, anche la sua pawn history).
            Move parentMoveForEvalDiff = _currentMoveHistory[ply + StackOffset - 1];
            if (parentMoveForEvalDiff.IsOk && !_inCheckHistory[ply + StackOffset - 1] && !_captureStageHistory[ply + StackOffset - 1])
            {
                int evalDiff = Math.Clamp(-(_staticEvalHistory[ply + StackOffset - 1] + staticEval), -189, 194) + 60;
                _movePick.ApplyEvalDiffMainBonus(Types.Opposite(pos.SideToMove), parentMoveForEvalDiff, evalDiff * 11);

                Square prevSqForEvalDiff = parentMoveForEvalDiff.ToSq;
                if (!probe.Found && Types.TypeOf(pos.PieceOn(prevSqForEvalDiff)) != PieceType.Pawn
                    && parentMoveForEvalDiff.TypeOf != MoveType.Promotion)
                    _movePick.ApplyEvalDiffPawnBonus(pos, pos.PieceOn(prevSqForEvalDiff), prevSqForEvalDiff, evalDiff * 13);
            }

            // Step 8. Razoring — search.cpp:989-992: se la valutazione statica è già molto sotto
            // alpha (margine che cresce col quadrato della profondità), la posizione non si
            // riprenderà: si passa direttamente alla quiescenza.
            if (!isPvNode && eval < alpha - 482 * depth * depth)
                return Quiesce(pos, alpha, beta, ply);

            // Step 9. Futility pruning: nodo figlio — search.cpp:994-1008. La condizione sulla
            // profondità (6 se si "cerca il matto", 19 altrimenti) non va tarata: serve a non
            // troncare la ricerca quando un punteggio già alto suggerisce un matto vicino.
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
            // "followPV" (segue la riga principale dell'iterazione precedente) non è portato —
            // condizione qui leggermente più ampia.
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
                // Riusa il buffer per-ply di MovePicker (non ancora costruito a questo punto del
                // nodo) invece di allocare una lista nuova — stessa ottimizzazione di
                // MovePicker.cs, vedi nota lì.
                var probCutCandidates = _mpGenBufs[ply];
                probCutCandidates.Clear();
                MoveGen.Generate(GenType.Captures, pos, probCutCandidates);

                foreach (var pcMove in probCutCandidates)
                {
                    if (!pos.Legal(pcMove)) continue;
                    if (!pos.SeeGe(pcMove, probCutBeta - staticEval)) continue;

                    var pcFrame = _accumulatorStack.Push();
                    var pcSt = new StateInfo();
                    pos.DoMove(pcMove, pcSt, pos.GivesCheck(pcMove), pcFrame.DirtyThreats, pcFrame.DirtyPiece, pcFrame.DirtyPawnPairs);

                    int pcValue = -Quiesce(pos, -probCutBeta, -probCutBeta + 1, ply + 1);

                    if (pcValue >= probCutBeta && probCutDepth > 0)
                        pcValue = -Negamax(pos, probCutDepth, ply + 1, -probCutBeta, -probCutBeta + 1, cutNode: !cutNode);

                    pos.UndoMove(pcMove);
                    _accumulatorStack.Pop();

                    if (pcValue >= probCutBeta)
                    {
                        _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(pcValue, ply), ttPv, Bound.Lower, probCutDepth + 1, pcMove, unadjustedStaticEval);
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

        var contRefs = BuildContinuationRefs(ply);
        var mp = new MovePicker(pos, _movePick, probe.Data.Move, depth, ply, contRefs,
            _mpMoveBufs[ply], _mpValueBufs[ply], _mpGenBufs[ply]);

        int origAlpha = alpha;
        int value = -Infinity;
        Move? bestMove = null;
        int moveCount = 0; // search.cpp:1116

        // search.cpp:761-762/1543-1551: mosse quiete/catture provate ma non risultate la
        // migliore, per aggiornare le loro statistiche di ordinamento a fine ciclo (Step 23).
        var quietsSearched = new List<Move>();
        var capturesSearched = new List<Move>();
        const int SearchedListCapacity = 32; // SEARCHEDLIST_CAPACITY, search.cpp:73

        // Step 14. Generazione a stadi vera (MovePicker, movepick.cpp) invece della lista
        // pre-generata+ordinata: mp.NextMove() emette mosse pseudo-legali una alla volta
        // nell'ordine di merito stimato, search.cpp:1118-1129.
        Move m;
        while ((m = mp.NextMove()) != Move.None)
        {
            if (m == excludedMove) continue;
            if (!pos.Legal(m)) continue;

            moveCount++;
            _moveCountHistory[ply + StackOffset] = moveCount; // Stack::moveCount, search.cpp:1137

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

            // Step 18 (prima parte, prima di fare la mossa), search.cpp:1152-1162: r è in
            // "milliply" (/1024 per ply interi). "delta" qui è l'ampiezza LOCALE alfa-beta di
            // QUESTO nodo (diversa da _rootDelta).
            int newDepth = depth - 1;
            int localDelta = beta - alpha;
            int r = Reduction(improving, depth, moveCount, localDelta);
            if (ttPv) r += 929;

            // Step 15. Potatura a profondità bassa — search.cpp:1164-1232, trascritta e
            // ri-confrontata riga per riga con la fonte (incluso il caso limite di see_ge sulle
            // mosse non-Normal: "return 0>=threshold" È il comportamento REALE della fonte per
            // le promozioni, non una nostra semplificazione — position.cpp:1393-1395). "followPV"
            // (segue la riga principale dell'iterazione precedente) non è portato: qui questa
            // potatura si applica sempre alle mosse quiete, piccola differenza dalla fonte.
            //
            // CAVEAT osservato: su una posizione con una promozione a donna vincente (d7c8q,
            // combaciante con l'oracolo nei commit precedenti), con questo Step attivo la mossa
            // scelta oscilla fra profondità vicine (6-9→q, 10→r, 12→q) invece di restare stabile
            // come fa l'oracolo reale (d7c8q a ogni profondità, verificato). SENZA questo Step la
            // stabilità torna (costante 6-10). Ipotesi più probabile dopo un secondo confronto
            // riga-per-riga (nessun errore di trascrizione trovato): nella fonte questo Step
            // lavora IN COPPIA con le Singular Extensions (Step 16, sotto — non ancora portate),
            // che ri-verificano con una ricerca ridotta se la mossa "ovviamente migliore" lo è
            // davvero, proprio per correggere i casi in cui la potatura aggressiva di questo Step
            // sceglie male a profondità bassa. Portarlo da solo, senza quella rete di sicurezza,
            // può quindi essere legittimamente più instabile a profondità basse — analogo a
            // quanto osservato in Flow A2 (i nodi non calavano finché il sistema di history non
            // era quasi completo). Prossimo passo naturale: Singular Extensions, poi riverificare
            // questa posizione.
            if (ply != 0 && !Values.IsLoss(value) && pos.NonPawnMaterial(pos.SideToMove) != 0)
            {
                // search.cpp:1168-1170 — late move pruning: oltre questa soglia mp smette di
                // generare/emettere mosse quiete a questo nodo (le catture restanti si provano
                // comunque) — MovePicker.SkipQuietMoves, non più un flag di filtro locale.
                if (moveCount >= (3 + (depth * depth)) / (improving ? 1 : 2))
                    mp.SkipQuietMoves();

                int lmrDepth = newDepth - (r / 1024);

                if (captureStage || givesCheck)
                {
                    Piece capturedForPrune = pos.PieceOn(m.ToSq);
                    int captHist = _movePick.GetCaptureHistory(pos.MovedPiece(m), m.ToSq, Types.TypeOf(capturedForPrune));

                    if (!givesCheck && lmrDepth < 8)
                    {
                        int futilityValue = staticEval + 234 + (247 * lmrDepth) + Values.PieceValue[(byte)capturedForPrune] + (134 * captHist / 1024);
                        if (futilityValue <= alpha) continue;
                    }

                    int margin = (177 * depth) + (captHist * 34 / 1024);
                    if ((alpha >= Values.Draw || pos.NonPawnMaterial(pos.SideToMove) != Values.PieceValue[(byte)pos.MovedPiece(m)])
                        && !pos.SeeGe(m, -margin))
                        continue;
                }
                else
                {
                    int dIndex = Math.Min(depth, LmrDivisor.Length) - 1;
                    int history = _movePick.ComputeQuietPruningHistory(pos, m, contRefs);

                    if (history < -4136 * depth) continue;

                    history += 69 * _movePick.GetMainHistoryRaw(pos.SideToMove, m) / 32;
                    lmrDepth += history / LmrDivisor[dIndex];

                    int futilityValue2 = staticEval + (119 * lmrDepth) + (90 * (staticEval > alpha ? 1 : 0)) + 164;

                    if (!inCheck && lmrDepth < 12 && futilityValue2 <= alpha)
                    {
                        if (value <= futilityValue2 && !Values.IsDecisive(value) && !Values.IsWin(futilityValue2))
                            value = futilityValue2;
                        continue;
                    }

                    lmrDepth = Math.Max(lmrDepth, 0);
                    if (!pos.SeeGe(m, -23 * lmrDepth * lmrDepth)) continue;
                }
            }

            // Step 16. Singular Extensions — search.cpp:1234-1303. Verifica se la mossa di TT è
            // "l'unica buona" ricercando la STESSA posizione (stesso ply, nessuna mossa fatta) con
            // quella mossa esclusa e una finestra molto stretta sotto il suo valore di TT: se
            // nessun'altra mossa riesce ad avvicinarsi, la mossa di TT è singolare e va estesa
            // (stima meglio la linea forzata); se un'altra mossa la eguaglia o supera, si può
            // potare l'intero sottoalbero (multi-cut) o comunque ridurre la fiducia nella mossa
            // di TT (estensione negativa). "seekMate" già calcolato sopra (Step 9).
            int extension = 0;
            if (ply != 0 && m == probe.Data.Move && excludedMove == default && depth >= 6 + (ttPv ? 1 : 0)
                && Values.IsValid(ttScore) && !Values.IsDecisive(ttScore) && (probe.Data.Bound & Bound.Lower) != Bound.None
                && probe.Data.Depth >= depth - 3 && !IsShuffling(m, ply, pos) && !seekMate)
            {
                int singularBeta = ttScore - (((59 + (66 * ((ttPv && !isPvNode) ? 1 : 0))) * depth) / 63);
                int singularDepth = newDepth / 2;

                int singularScore = Negamax(pos, singularDepth, ply, singularBeta - 1, singularBeta, cutNode, excludedMove: m);

                if (singularScore < singularBeta)
                {
                    int corrValAdj = Math.Abs(correctionValue) / 198368;
                    int doubleMargin = -2 + (204 * (isPvNode ? 1 : 0)) - (152 * (ttCapture ? 0 : 1)) - corrValAdj
                        - (1175 * _movePick.TtMoveHistory / 114178) - (ply > _rootDepth ? 38 : 0);
                    int tripleMargin = 70 + (279 * (isPvNode ? 1 : 0)) - (188 * (ttCapture ? 0 : 1)) + (81 * (ttPv ? 1 : 0))
                        - corrValAdj - (ply > _rootDepth ? 43 : 0);

                    extension = 1 + (singularScore < singularBeta - doubleMargin ? 1 : 0) + (singularScore < singularBeta - tripleMargin ? 1 : 0);
                    depth++;
                }
                else if (singularScore >= beta && !Values.IsDecisive(singularScore))
                {
                    _movePick.UpdateTtMoveHistory(-421 - (110 * depth));

                    if (!inCheck && singularScore > staticEval)
                    {
                        int mcBonus = Math.Clamp((singularScore - staticEval) * singularDepth * 177 / 1024,
                            -CorrectionHistoryLimit / 4, CorrectionHistoryLimit / 4);
                        UpdateCorrectionHistory(pos, ply, mcBonus);
                    }

                    return singularScore;
                }
                else if (ttScore >= beta || cutNode)
                {
                    extension = -3;
                }
            }
            newDepth += extension;

            var frame = _accumulatorStack.Push();
            var st = new StateInfo();
            pos.DoMove(m, st, givesCheck, frame.DirtyThreats, frame.DirtyPiece, frame.DirtyPawnPairs);

            // Step 18 (continua dopo aver fatto la mossa), search.cpp:1316-1359.
            if (ttPv)
                r -= 3023 + (isPvNode ? 1004 : 0) + (Values.IsValid(ttScore) && ttScore > alpha ? 885 : 0)
                    + (probe.Data.Depth >= depth ? 816 + (cutNode ? 940 : 0) : 0);
            r += 697;
            r -= moveCount * 65;
            r -= Math.Abs(correctionValue) / 26310;
            if (cutNode) r += 4026 + (probe.Data.Move == Move.None ? 933 : 0);
            if (ttCapture) r += 1079;

            int childCutoffCnt = _cutoffCntHistory[ply + StackOffset + 1];
            if (childCutoffCnt > 1) r += 264 + (childCutoffCnt > 2 ? 1095 : 0) + (allNode ? 1138 : 0);
            else if (m == probe.Data.Move) r -= 2179;

            int statScore = _movePick.ComputeStatScore(pos, m, captureStage, contRefs);
            _statScoreHistory[ply + StackOffset] = statScore; // Stack::statScore, search.cpp:1342-1349
            r -= statScore * 439 / 4096;

            if (!captureStage && !Values.IsDecisive(alpha))
                r += 3 * Math.Clamp(alpha - eval, -64, 96);

            if (allNode) r += r * 276 / ((256 * depth) + 268);

            // cutNode del figlio — search.cpp:1372/1387/1403/1422: la ricerca a finestra piena di
            // "Step 20" (solo nei nodi PV, sulla prima mossa o dopo un fallimento alto) passa
            // sempre cutNode=false; la ricerca a finestra nulla (Step 18/19) passa true quando è
            // ridotta da LMR, altrimenti !cutNode del genitore.
            int score;
            if (depth >= 2 && moveCount > 1)
            {
                int d = Math.Max(1, Math.Min(newDepth - (r / 1024), newDepth + 2)) + (isPvNode ? 1 : 0);
                _reductionHistory[ply + StackOffset] = newDepth - d; // search.cpp:1371
                score = -Negamax(pos, d, ply + 1, -(alpha + 1), -alpha, cutNode: true);
                _reductionHistory[ply + StackOffset] = 0; // search.cpp:1373

                if (score > alpha)
                {
                    bool doDeeperSearch = d < newDepth && score > value + 53;
                    bool doShallowerSearch = score < value + 8;
                    int researchDepth = newDepth + (doDeeperSearch ? 1 : 0) - (doShallowerSearch ? 1 : 0);

                    if (researchDepth > d)
                        score = -Negamax(pos, researchDepth, ply + 1, -beta, -alpha, cutNode: !cutNode);
                }
            }
            else if (!isPvNode || moveCount > 1)
            {
                int rNoTt = r + (probe.Data.Move == Move.None ? 1127 : 0);
                int searchDepth = newDepth - (rNoTt > 5234 ? 1 : 0) - (rNoTt > 5487 && newDepth > 2 ? 1 : 0);
                score = -Negamax(pos, searchDepth, ply + 1, -(alpha + 1), -alpha, cutNode: !cutNode);
            }
            else
            {
                score = -Infinity; // PV, prima mossa: sovrascritto incondizionatamente sotto (Step 20)
            }

            if (isPvNode && (moveCount == 1 || score > alpha))
                score = -Negamax(pos, newDepth, ply + 1, -beta, -alpha, cutNode: false);

            pos.UndoMove(m);
            _accumulatorStack.Pop();

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
                        _cutoffCntHistory[ply + StackOffset]++; // search.cpp:1529 (extension<2||PvNode semplificato a sempre vero, niente estensioni qui)
                        break;
                    }
                }
            }

            // search.cpp:1543-1551: "se la mossa è peggiore di una già provata, ricordarla per
            // aggiornarne le statistiche dopo" — qui bestMove riflette già l'eventuale
            // aggiornamento appena fatto sopra, quindi la mossa che ha appena causato il taglio
            // beta (bestMove) non entra mai in queste liste (il break sopra la esclude comunque).
            if (moveCount <= SearchedListCapacity && m != bestMove)
            {
                if (captureStage) capturesSearched.Add(m); else quietsSearched.Add(m);
            }
        }

        // search.cpp:1558-1560: smussa bestValue verso beta quando fallisce alto per punteggi non
        // decisivi — evita che un singolo taglio beta "profondo poco" venga preso alla lettera.
        if (value >= beta && !Values.IsDecisive(value) && !Values.IsDecisive(alpha))
            value = ((value * depth) + beta) / (depth + 1);

        // search.cpp:1562-1566: nessuna mossa pseudo-legale generata da mp è risultata legale (o
        // l'unica legale era quella esclusa da una ricerca Singular Extensions) — matto, stallo, o
        // (nella ricerca di verifica con excludedMove impostata) solo un fail-low, non un matto
        // vero.
        if (moveCount == 0)
        {
            value = excludedMove != default ? alpha : inCheck ? -(MateScore - ply) : 0;
        }
        else if (bestMove != null)
        {
            _movePick.UpdateStats(pos, ply, bestMove.Value, quietsSearched, capturesSearched, depth, probe.Data.Move, isPvNode, contRefs, inCheck);
        }
        else if (ply != 0)
        {
            // Step 23, ramo "bonus per il countermove che ha causato il fail-low puro"
            // (search.cpp:1578-1609): nessuna mossa di QUESTO nodo ha battuto alpha, quindi la
            // mossa del genitore (ss-1) che ci ha portato qui va probabilmente premiata (se
            // quieta) o già lo è a sufficienza dal materiale guadagnato (se cattura).
            Move parentMove = _currentMoveHistory[ply + StackOffset - 1];
            if (parentMove.IsOk)
            {
                Square prevSq = parentMove.ToSq;
                Piece prevPiece = pos.PieceOn(prevSq); // il pezzo del genitore, ora su prevSq
                bool priorCapture = _captureStageHistory[ply + StackOffset - 1];

                if (!priorCapture)
                {
                    int parentInCheckInt = _inCheckHistory[ply + StackOffset - 1] ? 1 : 0;
                    int bonusScale = -241;
                    bonusScale -= _statScoreHistory[ply + StackOffset - 1] / 98;
                    bonusScale += Math.Min(59 * depth, 420);
                    bonusScale += 186 * (_moveCountHistory[ply + StackOffset - 1] > 9 ? 1 : 0);
                    bonusScale += 142 * ((!inCheck && value <= staticEval - 106) ? 1 : 0);
                    bonusScale += 159 * ((parentInCheckInt == 0 && value <= -_staticEvalHistory[ply + StackOffset - 1] - 68) ? 1 : 0);
                    bonusScale = Math.Max(bonusScale, 0);

                    int scaledBonus = Math.Min((150 * depth) - 85, 1337) * bonusScale;

                    var parentContRefs = BuildContinuationRefs(ply - 1);
                    _movePick.ApplyCountermoveQuietBonus(pos, prevPiece, prevSq, parentMove,
                        parentContRefs, _inCheckHistory[ply + StackOffset - 1], scaledBonus, Types.Opposite(pos.SideToMove));
                }
                else
                {
                    PieceType capturedType = Types.TypeOf(pos.CapturedPiece());
                    _movePick.ApplyCountermoveCaptureBonus(prevPiece, prevSq, capturedType);
                }
            }
        }

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
        int standPat = inCheck ? -MateScore + ply : Evaluate.StaticEval(pos, _accumulatorStack);

        if (!inCheck)
        {
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        // MovePicker con depth=DEPTH_QS (0): sotto scacco genera le evasioni (tutte, come
        // "candidates" faceva prima aggiungendole indiscriminatamente); altrimenti solo le
        // catture (stadio QCAPTURE) — search.cpp:1766-1770. Restituisce mosse pseudo-legali: il
        // filtro di legalità/SEE resta nel ciclo sotto, come nella fonte (search.cpp:1778-1820).
        // Continuation history non tracciata in quiescenza (Quiesce non scrive
        // _currentMoveHistory/_movedPieceHistory) — contRefs vuoti disattiva il termine nella
        // formula di score, come già prima di questo Step.
        var mp = new MovePicker(pos, _movePick, Move.None, Ply.DepthQs, ply, EmptyContinuationRefs,
            _mpMoveBufs[ply], _mpValueBufs[ply], _mpGenBufs[ply]);

        int moveCount = 0;
        Move m;
        while ((m = mp.NextMove()) != Move.None)
        {
            if (!pos.Legal(m)) continue;

            if (!inCheck)
            {
                if (!pos.Capture(m)) continue;
                if (!pos.SeeGe(m)) continue;
            }

            moveCount++;

            var qFrame = _accumulatorStack.Push();
            var st = new StateInfo();
            pos.DoMove(m, st, pos.GivesCheck(m), qFrame.DirtyThreats, qFrame.DirtyPiece, qFrame.DirtyPawnPairs);
            int score = -Quiesce(pos, -beta, -alpha, ply + 1);
            pos.UndoMove(m);
            _accumulatorStack.Pop();

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        if (inCheck && moveCount == 0)
            return -(MateScore - ply);

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

    /// <summary><c>is_shuffling</c>, search.cpp:153-160 — rileva mosse che vanno-e-vengono senza
    /// scopo (limita esplosioni di ricerca in finali con regola delle 50 mosse alta).</summary>
    private bool IsShuffling(Move m, int ply, Position pos)
    {
        if (pos.CaptureStage(m) || pos.Rule50Count < 10) return false;
        if (pos.PliesFromNull < 6 || ply < 20) return false;

        Move m2 = _currentMoveHistory[ply + StackOffset - 2]; // (ss-2)->currentMove
        Move m4 = _currentMoveHistory[ply + StackOffset - 4]; // (ss-4)->currentMove
        return m.FromSq == m2.ToSq && m2.FromSq == m4.ToSq;
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

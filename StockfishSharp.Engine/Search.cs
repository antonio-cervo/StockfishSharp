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
// - RootMove/RootMoves (search.h:135-168) ora portate con fedeltà (RootMove.cs): una voce per
//   ogni mossa legale alla radice, ricreata a ogni Search_ (equivalente di
//   ThreadPool::start_thinking, thread.cpp:309-321, senza il filtro "searchmoves"). "seekMate"
//   (Step 9) legge ora rootMoves[pvIdx].score come nella fonte, non più un'approssimazione
//   dall'ultima iterazione completata. Le aspiration windows usano ora la vera media mobile pesata
//   per "effort" (search.cpp:1437-1468) invece del valore grezzo dell'iterazione precedente.
//   L'optimism derivato da rootMoves[pvIdx].averageScore (search.cpp:381-383) è ora wired dentro
//   Evaluate.StaticEval (parametro optimism). L'ordinamento/restrizione delle mosse radice via
//   tablebase (Tablebases::rank_root_moves, TB9) è ora wired su RootMove.TbRank/.TbScore, incluso
//   il filtro pvFirst/pvLast del ciclo mosse radice (search.cpp:1131-1135). Ancora NON portati: il
//   ciclo MultiPV (search.cpp:360-503, qui multiPV resta fissato a 1), Skill Level, il filtro
//   "searchmoves".
// cutNode è ora tracciato attraverso la ricorsione (Step 11 Internal Iterative Reduction,
// search.cpp:1048-1052, ne dipende) con la stessa convenzione di chiamata della fonte — vedi i
// commenti sui singoli punti di ricorsione. "followPV" (segue la riga principale dell'iterazione
// precedente, search.cpp:772-775) ora portato (Step 11 e Step 15).
// ProbCut (Step 12, la verifica vera con quiescenza + ricerca ridotta sulle catture con SEE sopra
// soglia; Step 13, la "piccola idea" solo da TT, attiva anche sotto scacco) è ora portato usando
// il vero MovePicker in modalità ProbCut (movepick.cpp:181-189, già presente in MovePicker.cs) al
// posto della generazione manuale usata inizialmente — ordina le catture per MVV+capture history
// invece di provarle nell'ordine di generazione grezzo.
// Gestione tempo adattiva reale (search.cpp:568-618) ora portata: quando Search_ riceve un
// optimumMs (equivalente di limits.use_time_management(), search.h:182 — vero solo con wtime/
// btime reali), la ricerca può fermarsi PRIMA del tetto massimo in base a quanto la mossa migliore
// è stabile (fallingEval/bestMoveInstability/nodesEffort). bestPreviousScore/
// bestPreviousAverageScore/previousTimeReduction persistono fra chiamate a Search_ nella stessa
// partita (azzerati da NewGame). "totBestMoveChanges" usa qui solo il valore del thread che sta
// eseguendo invece della somma su tutti i thread del pool divisa per il loro numero — con thread
// indipendenti che condividono la stessa posizione, il valore di un singolo thread è già un buon
// proxy della media (nessuna sincronizzazione cross-thread aggiunta per un guadagno di fedeltà
// marginale). Non portato: ponder/stopOnPonderhit (il protocollo ponder non è gestito da
// Program.cs) — il ramo "ferma la ricerca" è quindi sempre quello percorso.
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

using System.Linq;
using StockfishSharp.Engine.Tablebases;

namespace StockfishSharp.Engine;

public sealed class SearchResult
{
    public Move? BestMove;
    public List<Move> Pv = [];
    public int SelDepth;
    public int ScoreCp;
    // RootMove.AverageScore della mossa migliore — search.cpp:248, propagato da
    // SearchThreadPool.GetBestResult al thread principale via Search.SetPreviousScores per la
    // gestione tempo adattiva della PROSSIMA chiamata a Search_ nella stessa partita.
    public int AverageScore;
    public long Nodes;
    public int Depth;
    public long TbHits;
}

public sealed class Search
{
    private readonly TranspositionTable _tt;
    private readonly MovePick _movePick = new();

    /// <summary>Le history/MovePick/AccumulatorStack di ogni istanza restano sempre private (come
    /// i campi non condivisi di <c>Search::Worker</c> nella fonte); <paramref name="sharedTt"/>
    /// (facoltativa) permette a più istanze di condividere la STESSA transposition table — il
    /// prerequisito per <c>SearchThreadPool</c> (C1, Lazy SMP): la TT è l'UNICA cosa che i thread
    /// della fonte condividono davvero (thread.h: <c>Search::SharedState</c> la passa per
    /// riferimento a ogni Worker). Senza argomento, il comportamento resta quello di sempre (TT
    /// privata, uso a thread singolo).</summary>
    /// <summary><c>Search::Worker::threadIdx</c>, search.cpp:173 — indice di questo thread nel
    /// pool. Serve a UNA cosa sola nella fonte (search.cpp:376), ma importante: ogni thread parte
    /// da una finestra di aspirazione leggermente diversa ("5 + threadIdx % 8"), che e' una delle
    /// poche fonti di DIVERSITA' del Lazy SMP — senza, tutti i thread cercano la stessa finestra e
    /// duplicano lavoro invece di esplorare parti diverse dell'albero.</summary>
    private readonly int _threadIdx;

    public Search(TranspositionTable? sharedTt = null, int threadIdx = 0)
    {
        _tt = sharedTt ?? new TranspositionTable();
        _threadIdx = threadIdx;
    }
    // N9 — accumulatore NNUE aggiornato in modo incrementale invece di ricalcolato da zero a ogni
    // Evaluate.StaticEval; sincronizzato con la ricerca via Push()/Pop() attorno a ogni DoMove/
    // UndoMove REALE (mai per il null-move, search.cpp:674-679/686: nessun pezzo si muove, quindi
    // l'accumulatore resta valido così com'è).
    private readonly Nnue.AccumulatorStack _accumulatorStack = new();
    private CancellationToken _ct;
    private long _nodes;
    private long _tbHits;

    /// <summary><c>Search::Worker::rootMoves</c>, search.h:168 — una voce per ogni mossa legale
    /// della posizione radice corrente, ricreata da zero a ogni chiamata a <see cref="Search_"/>
    /// (equivalente di <c>ThreadPool::start_thinking</c>, thread.cpp:309-321: qui senza il filtro
    /// "searchmoves", non ancora un'opzione UCI portata). Sostituisce la ri-sonda della TT a
    /// posteriori usata in precedenza per il bestmove (mai necessaria nella fonte, che usa sempre
    /// rootMoves[0].pv[0]) — quel meccanismo interinale poteva restituire, sotto Lazy SMP, una
    /// mossa non verificata legale per la posizione attuale: bug reale trovato in una partita del
    /// bot, 2026-09-06. Con rootMoves reali il bestmove è sempre <c>_rootMoves[0].Pv[0]</c> dopo
    /// l'ordinamento a fine iterazione, esattamente come nella fonte.</summary>
    private readonly List<RootMove> _rootMoves = [];

    /// <summary><c>Search::Worker::pvIdx</c>, search.h — indice della riga MultiPV in corso.
    /// Sempre 0: il ciclo MultiPV di iterative_deepening (search.cpp:360-503) non è ancora
    /// portato — il campo esiste già con lo stesso nome/ruolo della fonte per quando MultiPV
    /// (l'opzione UCI) verrà aggiunto.</summary>
    private readonly int _pvIdx;

    /// <summary><c>Search::Worker::pvFirst</c>/<c>pvLast</c>, search.h — delimitano, dentro
    /// <see cref="_rootMoves"/>, il gruppo di mosse di pari "tbRank" attualmente in gioco per
    /// <see cref="_pvIdx"/> (search.cpp:362-368). Ricalcolati una volta per profondità in
    /// <see cref="Search_"/>, letti da <see cref="Negamax"/> per filtrare il ciclo mosse radice.</summary>
    private int _pvFirst;
    private int _pvLast;

    /// <summary><c>Search::Worker::lastIterationIdxPV</c>, search.cpp:370 —
    /// <c>rootMoves[pvIdx].previousPV</c> snapshottato una volta per profondità, prima che il
    /// ciclo mosse di questa iterazione lo sovrascriva: la riga da "seguire" per calcolare
    /// <c>followPV</c> in ogni nodo di questa iterazione.</summary>
    private List<Move> _lastIterationIdxPv = [];

    /// <summary><c>Search::Worker::selDepth</c>, search.h — azzerato a ogni profondità
    /// (iterative_deepening, search.cpp:373), aggiornato dal primo nodo PV di ogni ramo che
    /// raggiunge un nuovo ply massimo (search.cpp:781-783).</summary>
    private int _selDepth;

    /// <summary><c>Search::Worker::bestMoveChanges</c>, search.h — quante volte la mossa migliore
    /// alla radice è cambiata in questa iterazione (search.cpp:1498-1499). Consumato dalla
    /// gestione tempo adattiva (search.cpp:562-566,586) — MAI auto-azzerato dentro Negamax/
    /// Search_: nella fonte solo il thread principale lo azzera, per OGNI thread del pool
    /// (compreso se stesso), dentro il ciclo "for (auto&&th:threads)" di search.cpp:562-566 — un
    /// thread helper accumula qui senza mai leggersi/azzerarsi da solo. Vedi
    /// <see cref="PeekAndResetBestMoveChanges"/>.</summary>
    private ulong _bestMoveChanges;

    /// <summary>Legge e azzera <see cref="_bestMoveChanges"/> — usato da <see
    /// cref="SearchThreadPool"/> per replicare il ciclo "for (auto&&th:threads)" di
    /// search.cpp:562-566 dal thread principale su OGNI thread del pool (compreso se stesso),
    /// invece di lasciare che ciascun thread azzeri solo il proprio contatore (che romperebbe la
    /// sincronizzazione: il thread principale leggerebbe sempre 0 dagli altri thread).</summary>
    internal ulong PeekAndResetBestMoveChanges()
    {
        ulong v = _bestMoveChanges;
        _bestMoveChanges = 0;
        return v;
    }

    /// <summary><c>SearchManager::stopOnPonderhit</c>, search.h:313 — vero quando, durante il
    /// pondering, l'ultima iterazione completata ha già superato il tempo che avremmo usato in una
    /// ricerca normale. Letto da <see cref="SearchThreadPool.StopOnPonderhit"/> dal comando
    /// "ponderhit" (Program.cs) per decidere se fermarsi SUBITO invece di aspettare il vero
    /// tetto massimo — <c>volatile</c> perché letto da un thread diverso da quello di ricerca.</summary>
    private volatile bool _stopOnPonderhit;
    public bool StopOnPonderhit => _stopOnPonderhit;

    /// <summary>Equivalente di <c>Search::Worker::tbConfig</c> (search.cpp:922-973, Step 7) — non
    /// più impostato dall'esterno: ricalcolato da <see cref="Tablebase.RankRootMoves"/> a ogni
    /// <see cref="Search_"/> (<c>ThreadPool::start_thinking</c>, thread.cpp:323, chiamato a ogni
    /// "go"), che lo deriva dalle opzioni UCI grezze sotto combinate con
    /// <see cref="Tablebase.MaxCardinality"/> (quante tabelle sono davvero caricate) e il numero
    /// di pezzi della posizione corrente — <c>Cardinality=0</c> risulta automaticamente finché
    /// <c>Tablebase.Init</c> non ha caricato nulla, senza bisogno di un caso speciale esplicito.</summary>
    private TbConfig _tbConfig;

    // Opzioni UCI Syzygy grezze (SyzygyPath è gestito a parte da Tablebase.Init — carica le
    // tabelle e aggiorna MaxCardinality, letto indirettamente da RankRootMoves). Default identici
    // a engine.cpp:117-123 ("SyzygyProbeDepth" 1, "Syzygy50MoveRule" true, "SyzygyProbeLimit" 7).
    private bool _syzygyUseRule50 = true;
    private int _syzygyProbeDepth = 1;
    private int _syzygyProbeLimit = 7;

    public void SetSyzygyOptions(bool useRule50, int probeDepth, int probeLimit)
    {
        _syzygyUseRule50 = useRule50;
        _syzygyProbeDepth = probeDepth;
        _syzygyProbeLimit = probeLimit;
    }

    private const int Infinity = 32001; // VALUE_INFINITE della fonte, types.h:155
    private const int MateScore = 32000; // VALUE_MATE, types.h:157

    // TimePoint massimo/"nessun limite", timeman.h — usato come sentinella per optimumMs quando
    // la gestione tempo adattiva non è attiva (equivalente di !limits.use_time_management()).
    public const long NoBound = long.MaxValue / 2;

    // Mitigazione pratica, non di fonte — vedi il commento su "previousIterationElapsedMs" in
    // Search_. Soglia scelta con margine ampio apposta (un'iterazione può legittimamente costare
    // qualche volta più della precedente anche in casi normali): serve a intercettare un'anomalia
    // netta, non la normale crescita del fattore di ramificazione.
    private const double IterationCostSafetyMultiplier = 2.5;

    /// <summary>Tetto PRATICO (non di fonte) al budget adattivo: <c>totalTime</c> non può mai
    /// superare questo multiplo di <c>optimum</c>. Misurato dal vivo (2026-09-07, replay della
    /// partita persa a tempo scaduto): con optimum=20,1s il prodotto dei moltiplicatori della
    /// fonte (<c>fallingEval * reduction * bestMoveInstability * highBestMoveEffort</c>) portava
    /// <c>totalTime</c> a 58 secondi ANCHE con fallingEval al minimo, e il tetto assoluto
    /// <c>tm.maximum()</c> concedeva fino a ~138s su una sola mossa (la fonte lo calcola come
    /// 0,81 volte l'orologio residuo).
    ///
    /// Nella fonte quei moltiplicatori stanno quasi sempre vicino a 1 perché la sua ricerca si
    /// stabilizza in una frazione di secondo; la nostra, che ha bisogno di ~5x più nodi per
    /// profondità, cambia idea molto più spesso e li fa compounding fino a 6x. Il risultato era
    /// un budget "improponibile in partita" (parole dell'utente): mosse da 40-70 secondi con
    /// l'orologio a 3 minuti, che hanno prodotto sconfitte reali a tempo scaduto.
    ///
    /// Questo limite NON tocca la formula portata (che resta fedele e continua a decidere QUANTO
    /// estendere entro il tetto): mette solo un massimale al risultato, come farebbe un
    /// allenatore che dice "puoi pensarci di più, ma non oltre il doppio del previsto".</summary>
    private const double MaxBudgetOverOptimum = 2.0;

    /// <summary>Scadenza oltre la quale la ricerca si interrompe ANCHE a meta' di un'iterazione
    /// (0 = disattivata). Aggiornata a ogni confine di iterazione con il budget corrente; letta dal
    /// controllo periodico in <see cref="Negamax"/>. Pratica, non di fonte — vedi il commento li'.
    /// Campo semplice, non <c>volatile</c>: viene scritto e letto sempre dallo STESSO thread (il
    /// ciclo di iterative deepening e la ricorsione di Negamax girano entrambi nel thread di
    /// questa istanza di Search).</summary>
    private double _softDeadlineMs;

    /// <summary><c>Worker::nmpMinPly</c>, search.h — mentre è diverso da zero il null move resta
    /// disattivato (fino a quel ply), per impedire che la ricerca di VERIFICA del null move ne
    /// avvii un'altra ricorsivamente: search.cpp:1030-1040 lo dice esplicitamente ("Recursive
    /// verification is not allowed"). Azzerato a ogni nuova ricerca e a ogni nuova partita.</summary>
    private int _nmpMinPly;
    private System.Diagnostics.Stopwatch? _elapsedStopwatch;

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
    // Stack::followPV della fonte — vedi la nota sopra la sua unica lettura/scrittura in Negamax.
    private readonly bool[] _followPvHistory = new bool[Ply.MaxPly + StackOffset + 1];
    private readonly bool[] _captureStageHistory = new bool[Ply.MaxPly + StackOffset + 1];

    // Stack::ttPv della fonte. Serve per ply (non come variabile locale) perche' la ricerca di
    // verifica delle Singular Extensions richiama Negamax allo STESSO ply con excludedMove
    // impostata, e li' la fonte NON ricalcola ttPv: lo eredita dalla chiamata esterna
    // (search.cpp:822, "ss->ttPv = excludedMove ? ss->ttPv : ...").
    private readonly bool[] _ttPvHistory = new bool[Ply.MaxPly + StackOffset + 1];

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

    // Buffer FISSI per ply, allocati una volta sola alla creazione di questa Search — nella fonte
    // sono tutti oggetti sullo stack della funzione ("StateInfo st;", "ValueList<Move,32>
    // quietsSearched", "const PieceToHistory* contHist[]"), quindi a costo zero; qui erano invece
    // allocazioni sull'heap a OGNI nodo (contRefs, le due liste) o a OGNI MOSSA (StateInfo, che
    // essendo una classe con 5 campi array costa da solo 6 allocazioni). Un solo esemplare per ply
    // basta: a un dato ply non ci sono mai due nodi vivi insieme (do_move/undo_move sono sempre
    // appaiati, e la ricerca di verifica delle Singular Extensions gira PRIMA che il nodo faccia la
    // propria mossa, quindi trova lo slot libero).
    private readonly StateInfo[] _stateInfoPool = BuildStateInfoPool();
    private readonly ContinuationRef[][] _contRefsBufs = BuildPerPlyContRefs();
    private readonly ContinuationRef[][] _parentContRefsBufs = BuildPerPlyContRefs();

    // DUE slot per ply, non uno: la ricerca di verifica delle Singular Extensions (Step 16) richiama
    // Negamax allo STESSO ply con excludedMove impostata, e quella chiamata annidata accumula le
    // proprie mosse mentre il nodo esterno sta ancora accumulando le sue. Nella fonte non e' un
    // problema perche' sono variabili locali sullo stack di ogni invocazione. Bastano due livelli:
    // il ramo con excludedMove non puo' entrare a sua volta nelle Singular Extensions (il gate
    // richiede excludedMove vuota), quindi non esiste un terzo livello allo stesso ply.
    private readonly List<Move>[] _quietsSearchedBufs = BuildPerPlyGenBufs(2);
    private readonly List<Move>[] _capturesSearchedBufs = BuildPerPlyGenBufs(2);
    private readonly List<Move>[] _legalScratchBufs = BuildPerPlyGenBufs();

    private static StateInfo[] BuildStateInfoPool()
    {
        var pool = new StateInfo[Ply.MaxPly + StackOffset + 2];
        for (int i = 0; i < pool.Length; i++) pool[i] = new StateInfo();
        return pool;
    }

    private static ContinuationRef[][] BuildPerPlyContRefs()
    {
        var bufs = new ContinuationRef[Ply.MaxPly + StackOffset + 2][];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new ContinuationRef[6];
        return bufs;
    }

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

    private static List<Move>[] BuildPerPlyGenBufs(int slotsPerPly = 1)
    {
        var bufs = new List<Move>[((Ply.MaxPly + StackOffset + 2) * slotsPerPly) + 1];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new List<Move>(Ply.MaxMoves);
        return bufs;
    }

    // Stack::pv, search.h:117 + PVMoves::update, search.h:95-104 — un genitore legge il PV del
    // proprio figlio a (ss+1) subito dopo che questo ritorna (mai più tardi: la mossa successiva
    // allo stesso ply sovrascrive lo stesso slot), esattamente come qui: riutilizzato per ogni
    // mossa provata a un ply, mai due sottoalberi vivi contemporaneamente sullo stesso indice.
    private readonly List<Move>[] _pvBuf = BuildPerPlyPvBufs();
    private static List<Move>[] BuildPerPlyPvBufs()
    {
        var bufs = new List<Move>[Ply.MaxPly + 2];
        for (int i = 0; i < bufs.Length; i++) bufs[i] = new List<Move>(Ply.MaxPly);
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

    // rootDepth, search.cpp — profondità dell'iterazione corrente, usata da "seekMate".
    private int _rootDepth;

    // Worker::optimism[], search.cpp:381-383,1901-1904 — derivato dalla media mobile
    // (rootMoves[pvIdx].averageScore) a ogni profondità: positivo per il colore al tratto alla
    // radice, negativo (stesso modulo) per l'altro. _rootColor è quello di ROOTPOS, fissato per
    // tutta la durata di una chiamata a Search_ (la posizione radice non cambia fra iterazioni).
    private Color _rootColor;
    private int _rootOptimism;

    /// <summary><c>Search::Worker::evaluate</c>, search.cpp:1901-1904: <c>optimism[pos.side_to_move()]</c>.</summary>
    private int Optimism(Color sideToMove) => sideToMove == _rootColor ? _rootOptimism : -_rootOptimism;

    // SearchManager::bestPreviousScore/bestPreviousAverageScore/previousTimeReduction,
    // search.h:310-312 — PERSISTONO fra chiamate a Search_ nella STESSA partita (azzerati solo da
    // NewGame, come ThreadPool::clear, thread.cpp:272-278), usati dalla gestione tempo adattiva
    // reale (search.cpp:568-618) per confrontare l'iterazione corrente con l'ULTIMA ricerca
    // completata (non l'ultima iterazione di QUESTA ricerca). Valori iniziali identici a
    // thread.cpp:273-277 (prima ancora di un "ucinewgame" esplicito).
    private int _bestPreviousScore = Values.Infinite;
    private int _bestPreviousAverageScore = Values.Infinite;
    private double _previousTimeReduction = 0.85;

    /// <summary>Permette a <see cref="SearchThreadPool"/> di propagare al thread principale i
    /// valori del "bestThread" scelto da <c>get_best_thread</c> (search.cpp:247-248: la fonte
    /// aggiorna sempre <c>main_manager()</c>, anche quando il thread vincente è un helper) — senza
    /// questo, un thread principale che non ha trovato la riga migliore userebbe i PROPRI valori
    /// invece di quelli del vincitore per calibrare la prossima mossa.</summary>
    public void SetPreviousScores(int score, int averageScore)
    {
        _bestPreviousScore = score;
        _bestPreviousAverageScore = averageScore;
    }

    /// <summary><c>interpolate</c>, misc.h:485-489 — interpolazione lineare fra due punti, non
    /// clampata (il clamp è sempre applicato dal chiamante).</summary>
    private static double Interpolate(double x, double x0, double x1, double y0, double y1) =>
        y0 + ((y1 - y0) * (x - x0) / (x1 - x0));

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

        // ThreadPool::clear(), thread.cpp:272-278 — questi due valori influenzano il tempo
        // impiegato sulla prima mossa della nuova partita.
        _bestPreviousAverageScore = Values.Infinite;
        _previousTimeReduction = 0.85;
        _bestPreviousScore = Values.Infinite;

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
    /// search.cpp:270-618. Qui ridotto a MultiPV=1, thread singolo, nessuno Skill Level: il ciclo
    /// MultiPV (search.cpp:360-503) e lo scambio della riga con Skill Level abilitato
    /// (search.cpp:626-629) non sono ancora portati. La gestione tempo adattiva reale
    /// (search.cpp:568-618) è invece ora portata — vedi <paramref name="optimumMs"/>.</summary>
    // callNewSearch: false quando il chiamante è un pool multi-thread (SearchThreadPool), che
    // replica "is_mainthread()" della fonte (search.cpp:191-204) — SOLO il thread principale
    // chiama tt.new_search(), una volta, PRIMA di avviare gli helper (threads.start_searching()
    // arriva dopo, riga 216); gli helper (righe 196-199) vanno dritti a iterative_deepening()
    // senza mai chiamarlo. Un incremento di _generation per thread romperebbe l'invecchiamento
    // della TT condivisa (byte non atomico, corsa tra thread).
    //
    // optimumMs: equivalente di "limits.use_time_management()" (search.h:182: vero solo quando la
    // GUI ha fornito wtime/btime reali) — di default NoBound, che disattiva la formula adattiva
    // sotto esattamente come la fonte fa per "go movetime"/"go depth"/"go infinite" (nessuno di
    // questi imposta i tempi dell'orologio). <paramref name="timeLimit"/> resta comunque il tetto
    // assoluto (equivalente di tm.maximum() quando la gestione tempo è attiva, o il budget fisso
    // altrimenti) — la ricerca non supera MAI questo limite, la formula sotto può solo fermarsi
    // PRIMA.
    //
    // crossThreadBestMoveChanges/threadCountForInstability: replicano "for(auto&&th:threads)
    // {totBestMoveChanges+=th->worker->bestMoveChanges; th->worker->bestMoveChanges=0;}" seguito
    // da "totBestMoveChanges/threads.size()" (search.cpp:562-566,586) — SearchThreadPool passa qui
    // un delegato che somma e azzera bestMoveChanges di OGNI thread del pool (vedi
    // PeekAndResetBestMoveChanges) e il conteggio dei thread; senza pool (default, standalone),
    // legge/azzera solo se stesso e divide per 1 — matematicamente lo stesso caso limite
    // "threads.size()==1" della fonte.
    // isPondering/maximumMsOverride: SearchManager::ponder (search.h:307) + il vero tm.maximum()
    // (search.cpp:602). "timeLimit" resta l'unico argomento del CancelAfter esterno sotto — per una
    // ricerca "go ponder" il chiamante (Program.cs) gli passa un tetto fittizio enorme (il vero
    // limite scatta solo al "ponderhit", riarmando dall'esterno il CancellationTokenSource
    // collegato a "ct") mentre "maximumMsOverride" porta qui il vero tm.maximum() per il confronto
    // interno di fine-iterazione (search.cpp:602) — per ogni altro chiamante (non pondering)
    // "maximumMsOverride" resta NoBound e si usa "timeLimit" come sempre, comportamento invariato.
    public SearchResult Search_(Position pos, int maxDepth, TimeSpan timeLimit, CancellationToken ct = default, bool callNewSearch = true, long optimumMs = NoBound,
        Func<ulong>? crossThreadBestMoveChanges = null, int threadCountForInstability = 1,
        Func<bool>? isPondering = null, long maximumMsOverride = NoBound)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeLimit);
        _ct = cts.Token;
        _nodes = 0;
        _tbHits = 0;
        _stopOnPonderhit = false; // ThreadPool::start_thinking, thread.cpp:304
        _nmpMinPly = 0;           // Worker::clear, search.cpp:696
        var elapsedStopwatch = System.Diagnostics.Stopwatch.StartNew(); // elapsed(), search.h
        _elapsedStopwatch = elapsedStopwatch;
        _softDeadlineMs = 0; // nessuna scadenza finche' la prima iterazione non ne calcola una
        if (callNewSearch) _tt.NewSearch();
        _movePick.ResetForSearch(); // lowPlyHistory.fill(102), search.cpp:326
        _accumulatorStack.Reset(); // AccumulatorStack::reset, nnue_accumulator.cpp:71-77

        Array.Clear(_staticEvalHistory);
        for (int i = 0; i < StackOffset; i++) _staticEvalHistory[i] = Values.None; // (ss-7)..(ss-1)
        Array.Clear(_currentMoveHistory);
        Array.Clear(_movedPieceHistory);
        Array.Clear(_inCheckHistory);
        Array.Clear(_followPvHistory);
        Array.Clear(_captureStageHistory);
        Array.Clear(_cutoffCntHistory);
        Array.Clear(_statScoreHistory);
        Array.Clear(_moveCountHistory);
        Array.Clear(_reductionHistory);
        _bestMoveChanges = 0;

        var result = new SearchResult();

        // ThreadPool::start_thinking, thread.cpp:309-321 (senza il filtro "searchmoves", non
        // ancora un'opzione UCI portata): una RootMove per ogni mossa legale della posizione.
        _rootMoves.Clear();
        var rootLegalMoves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, rootLegalMoves);
        foreach (var rlm in rootLegalMoves)
            _rootMoves.Add(new RootMove(rlm));

        // Tablebases::rank_root_moves, thread.cpp:323 — chiamato SEMPRE (anche a lista vuota:
        // RankRootMoves gestisce quel caso da sé, ritornando la config di default) subito dopo
        // aver popolato rootMoves, PRIMA del controllo "nessuna mossa legale" sotto (stesso ordine
        // della fonte: start_thinking chiama rank_root_moves, start_searching controlla
        // rootMoves.empty() solo più tardi). TB9 (ordinamento/classificazione) e TB10 (probing
        // "in-tree", Step 7 di Negamax) condividono così la stessa Config, come nella fonte.
        _tbConfig = Tablebase.RankRootMoves(pos, _rootMoves, _syzygyUseRule50, _syzygyProbeDepth, _syzygyProbeLimit);

        // start_searching(), search.cpp:207-213: nessuna mossa legale (matto o stallo), nessuna
        // ricerca da fare.
        if (_rootMoves.Count == 0)
        {
            result.ScoreCp = pos.Checkers() != 0 ? -MateScore : Values.Draw;
            return result;
        }

        _rootColor = pos.SideToMove;

        // search.cpp:283,305-311 — locali a iterative_deepening, quindi resettate a ogni Search_
        // (a differenza di _bestPreviousScore/_bestPreviousAverageScore/_previousTimeReduction,
        // che sopravvivono da una chiamata all'altra nella stessa partita).
        double timeReduction = 1;
        double totBestMoveChanges = 0;
        int lastBestMoveDepth = 0;
        List<Move> lastBestMovePv = [];
        var iterValue = new int[4];
        int iterIdx = 0;
        Array.Fill(iterValue, _bestPreviousScore == Values.Infinite ? Values.Zero : _bestPreviousScore);

        // search.cpp:307,356-357,392-393 — freno mancato nel primo porting delle aspiration
        // windows: se l'iterazione precedente ha già speso più della metà del tempo stimato
        // (increaseDepth=false, sotto), le iterazioni successive cercano una profondità EFFETTIVA
        // ridotta (adjustedDepth, non rootDepth) finché il "debito" non viene ripagato — un
        // incremento pieno ogni 4 passi di searchAgainCounter (commento della fonte: "issue
        // #2717"). Senza questo, il ciclo continuava a tentare iterazioni sempre più profonde e
        // costose anche quando la ricerca aveva già segnalato di essere a corto di tempo — causa
        // reale, verificata offline, di una mossa che ha impiegato l'intero MaximumTime (fino a
        // ~170s) su una posizione dove la mossa migliore cambiava spesso da un'iterazione
        // all'altra: senza questo freno ogni iterazione veniva comunque tentata a piena profondità.
        int searchAgainCounter = 0;
        bool increaseDepth = true;

        // Mitigazione PRATICA, non di fonte (2026-09-07, richiesta esplicitamente dall'utente dopo
        // un'analisi diretta di una sconfitta reale a tempo scaduto — vedi
        // docs/porting-master-plan.md): traccia quanto tempo l'ULTIMA iterazione completata ha
        // consumato DA SOLA (non l'elapsed cumulativo). Stockfish reale non ha bisogno di questo:
        // sulla stessa posizione reale usa ~20 volte meno nodi alla stessa profondità (misurato
        // contro l'oracolo), quindi il salto di costo fra un'iterazione e la successiva resta quasi
        // sempre piccolo — da noi può essere drastico (un'iterazione da 20s+ dopo una da 2s), e una
        // volta iniziata non c'è modo di interromperla a metà (il controllo periodico rispetta solo
        // il tetto assoluto, molto più alto di quello stimato). Vedi IterationCostSafetyMultiplier
        // sotto.
        double previousIterationElapsedMs = 0;

        try
        {
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                _rootDepth = depth;
                totBestMoveChanges /= 2; // search.cpp:341, invecchia la metrica di instabilità

                // search.cpp:356-357 — se l'iterazione precedente non ha lasciato margine
                // (increaseDepth=false, impostato a fine iterazione precedente sotto), conta
                // quanti passi di "recupero" servono prima di tornare a una profondità piena.
                if (!increaseDepth) searchAgainCounter++;

                _selDepth = 0; // search.cpp:373, dentro il ciclo pvIdx (qui multiPV=1, una volta)

                // search.cpp:347-352 — salva i punteggi dell'iterazione precedente prima che il
                // ciclo mosse di questa li sovrascriva; "previousScoreExact" vale per il solo
                // indice _pvIdx finché multiPV resta a 1 (i < multiPV nella fonte).
                for (int i = 0; i < _rootMoves.Count; i++)
                {
                    var prm = _rootMoves[i];
                    prm.PreviousScore = prm.Score;
                    prm.PreviousPv = [.. prm.Pv];
                    prm.PreviousScoreExact = i == _pvIdx;
                }

                // search.cpp:362-368 — pvFirst/pvLast delimitano il gruppo di mosse di pari
                // "tbRank" a cui la ricerca radice si limita per questo pvIdx: con TB9
                // (Tablebase.RankRootMoves) ora wired, quando la radice è in tablebase le mosse di
                // rango inferiore (perdenti o che convertono più lentamente) restano nell'array
                // ma NON vengono più cercate finché quelle di rango pari/superiore non sono
                // esaurite — esattamente come la fonte. Senza tablebase attiva tbRank è uniforme
                // (0 per tutte), quindi il gruppo copre sempre l'intero array: nessuna differenza
                // di comportamento nel caso comune.
                _pvFirst = _pvIdx;
                _pvLast = _pvIdx;
                for (_pvLast++; _pvLast < _rootMoves.Count; _pvLast++)
                    if (_rootMoves[_pvLast].TbRank != _rootMoves[_pvFirst].TbRank)
                        break;

                // search.cpp:370 — la riga da seguire per followPV in questa iterazione è la PV
                // dell'iterazione PRECEDENTE (già salvata sopra in PreviousPv prima di essere
                // sovrascritta).
                _lastIterationIdxPv = _rootMoves[_pvIdx].PreviousPv;

                // search.cpp:376-383 — ampiezza dell'aspiration window e optimism dalla vera media
                // mobile pesata per "effort" della mossa radice corrente (RootMove.cs), non più dal
                // valore grezzo dell'iterazione precedente.
                var pvRootMove = _rootMoves[_pvIdx];
                int delta = 5 + (_threadIdx % 8) + (int)(Math.Abs(pvRootMove.MeanSquaredScore) / 10193); // search.cpp:376
                int avg = pvRootMove.AverageScore;
                int alpha = Math.Max(avg - delta, -Infinity);
                int beta = Math.Min(avg + delta, Infinity);
                _rootOptimism = 114 * avg / (Math.Abs(avg) + 85);

                int bestValue;
                int failedHighCnt = 0; // search.cpp:387 — locale a QUESTA profondità, mai invecchiato
                while (true)
                {
                    // search.cpp:390-393 — profondità EFFETTIVA di questo tentativo: ridotta sia
                    // dai fail-high ripetuti all'interno di questa stessa profondità (failedHighCnt)
                    // sia dal "debito" accumulato da iterazioni precedenti troppo lente
                    // (searchAgainCounter) — mai meno di 1 ply. "assicura almeno un incremento
                    // effettivo ogni 4 passi di searchAgain" (commento della fonte, issue #2717).
                    int adjustedDepth = Math.Max(1, depth - failedHighCnt - (3 * (searchAgainCounter + 1) / 4));
                    _rootDelta = beta - alpha; // search.cpp:394, ricalcolato a ogni tentativo
                    bestValue = Negamax(pos, adjustedDepth, 0, alpha, beta, cutNode: false);

                    // search.cpp:403 — stable_sort: le mosse a pari punteggio (tutte le non-PV,
                    // rimaste a -Infinity) mantengono l'ordine relativo che avevano.
                    var sortedRootMoves = RootMove.SortDescending(_rootMoves).ToList();
                    _rootMoves.Clear();
                    _rootMoves.AddRange(sortedRootMoves);

                    if (bestValue <= alpha)
                    {
                        beta = alpha;
                        alpha = Math.Max(bestValue - delta, -Infinity);
                        failedHighCnt = 0; // search.cpp:425
                        _stopOnPonderhit = false; // search.cpp:427 — un fail-low invalida la decisione presa
                    }
                    else if (bestValue >= beta)
                    {
                        alpha = Math.Max(beta - delta, alpha);
                        beta = Math.Min(bestValue + delta, Infinity);
                        failedHighCnt++; // search.cpp:433
                    }
                    else break;

                    delta += 47 * delta / 128;
                }

                // Bestmove/punteggio/PV presi dalla vera rootMoves[0] dopo l'ordinamento — non più
                // da una ri-sonda della TT a posteriori (mai necessaria nella fonte, che usa sempre
                // rootMoves[0].pv[0]): un meccanismo interinale con quella ri-sonda poteva
                // restituire, sotto Lazy SMP, una mossa non verificata legale per la posizione
                // attuale — bug reale trovato in una partita del bot, 2026-09-06. rootMoves[0].pv[0]
                // è per costruzione sempre legale (creata da MoveGen.Generate(Legal,...) sopra, mai
                // sovrascritta con altro che mosse passate da pos.Legal(m) nel ciclo di Negamax).
                var bestRootMove = _rootMoves[0];
                result.BestMove = bestRootMove.Pv[0];
                result.Pv = [.. bestRootMove.Pv];
                result.ScoreCp = bestRootMove.Score;
                result.AverageScore = bestRootMove.AverageScore;
                result.Depth = depth;
                result.SelDepth = bestRootMove.SelDepth;

                // search.cpp:510-521 — traccia da quanto la mossa migliore è stabile.
                // "forgottenMate"/l'aggancio a un matto di un'iterazione interrotta a metà
                // (search.cpp:505-547) non servono qui: un'iterazione interrotta a metà lancia
                // OperationCanceledException PRIMA di arrivare a questo punto, quindi il ciclo
                // "for" non la raggiunge mai e result mantiene per costruzione l'ultimo risultato
                // completato — lo stesso identico effetto che quella logica ottiene nella fonte
                // con un flag cooperativo, qui gratis grazie al modello a eccezioni.
                if (lastBestMovePv.Count == 0 || lastBestMovePv[0] != bestRootMove.Pv[0])
                    lastBestMoveDepth = depth;
                lastBestMovePv = bestRootMove.Pv;

                // search.cpp:568-614 — gestione tempo adattiva reale: SOLO quando optimumMs è
                // stato fornito (equivalente di limits.use_time_management()) E non abbiamo già
                // deciso di fermarci al prossimo ponderhit (search.cpp:569, "!mainThread->
                // stopOnPonderhit" — una volta vero, questo blocco intero smette di rieseguire,
                // congelando totBestMoveChanges/timeReduction ai valori dell'ultima iterazione
                // valida, finché un fail-low non lo azzera di nuovo sopra).
                //
                // Il ciclo "for(auto&&th:threads)" della fonte (search.cpp:562-566) gira SEMPRE
                // per il thread principale, non solo quando use_time_management è attivo — ma dato
                // che senza gestione tempo totBestMoveChanges non viene mai letto, per noi non
                // azzerare _bestMoveChanges in quel caso è innocuo (resta comunque azzerato
                // all'inizio del prossimo "go"): innestare tutto qui dentro semplifica senza
                // cambiare comportamento osservabile.
                if (optimumMs < NoBound && !_stopOnPonderhit)
                {
                    // search.cpp:561-566 — accumula quante volte la mossa migliore è cambiata in
                    // questa iterazione, mediata su tutti i thread del pool (vedi
                    // crossThreadBestMoveChanges sopra).
                    ulong changesThisRead = crossThreadBestMoveChanges?.Invoke() ?? PeekAndResetBestMoveChanges();
                    totBestMoveChanges += (double)changesThisRead / Math.Max(1, threadCountForInstability);

                    ulong nodesEffort = bestRootMove.Effort * 100000UL / (ulong)Math.Max(1L, _nodes);

                    double fallingEval = (11.48 + (2.30 * (_bestPreviousAverageScore - bestValue))
                                          + (1.1 * (iterValue[iterIdx] - bestValue))) / 100.0;
                    fallingEval = Math.Clamp(fallingEval, 0.576, 1.728);

                    // Se la mossa migliore è stabile da diverse iterazioni, riduce il tempo di conseguenza.
                    timeReduction = Math.Clamp(
                        Interpolate(depth - lastBestMoveDepth, 4.96, 18.79, 0.639, 1.712), 0.629, 1.544);

                    double reduction = (1.468 + _previousTimeReduction) / (2.284 * timeReduction);

                    double bestMoveInstability = 1.077 + (2.229 * totBestMoveChanges);

                    double highBestMoveEffort = Math.Clamp(
                        Interpolate(nodesEffort, 75800, 104510, 0.969, 0.714), 0.693, 0.838);

                    double totalTime = optimumMs * fallingEval * reduction * bestMoveInstability * highBestMoveEffort;

                    // Tetto pratico al budget adattivo — vedi MaxBudgetOverOptimum. La formula
                    // sopra resta quella della fonte, qui se ne limita solo il risultato.
                    totalTime = Math.Min(totalTime, optimumMs * MaxBudgetOverOptimum);

                    if (_rootMoves.Count == 1)
                        totalTime = Math.Min(500.0, totalTime); // limita a 0.5s per una miglior esperienza visiva

                    double elapsedMs = elapsedStopwatch.Elapsed.TotalMilliseconds;

                    // tm.maximum() vero (search.cpp:602): quando si sta pondering, timeLimit passato
                    // qui da Program.cs è un tetto fittizio enorme (il vero CancelAfter esterno viene
                    // riarmato solo al "ponderhit" — vedi HandleGo/HandlePonderhit) e maximumMsOverride
                    // porta il vero tm.maximum() per questo confronto interno; per una ricerca normale
                    // (non pondering) maximumMsOverride resta NoBound e si usa timeLimit come sempre.
                    double effectiveMaximumMs = maximumMsOverride != NoBound ? maximumMsOverride : (double)timeLimit.TotalMilliseconds;
                    bool pondering = isPondering?.Invoke() ?? false;

                    // Aggiorna la scadenza morbida per l'iterazione che sta per iniziare: mai
                    // oltre il budget appena calcolato, ne' oltre il tetto assoluto. Disattivata
                    // durante il pondering, dove il tempo extra non e' mai davvero a rischio.
                    _softDeadlineMs = (isPondering?.Invoke() ?? false)
                        ? 0
                        : Math.Min(totalTime, effectiveMaximumMs);

                    if (elapsedMs > Math.Min(totalTime, effectiveMaximumMs)
                        || bestRootMove.Score >= MateScore - 3
                        || bestRootMove.Score == -MateScore + 2)
                    {
                        // search.cpp:605-610 — se stiamo pondering non fermiamo la ricerca ora, ma
                        // segnaliamo che lo faremmo se non lo fossimo: il "ponderhit" (o il vero
                        // check_time via il riarmo esterno di CancelAfter) userà questo per fermarsi
                        // quasi subito invece di aspettare la fine dell'iterazione in corso.
                        if (pondering)
                            _stopOnPonderhit = true;
                        else
                            break;
                    }
                    else
                    {
                        // search.cpp:612-613 — se non ci fermiamo, decide se la prossima iterazione
                        // meriti un incremento pieno di profondità: sempre sì mentre si sta pondering
                        // (mainThread->ponder), altrimenti solo se abbiamo usato meno di metà del
                        // tempo stimato finora.
                        increaseDepth = pondering || elapsedMs <= totalTime * 0.50;

                        // Mitigazione pratica, non di fonte (vedi il commento su
                        // "previousIterationElapsedMs" sopra il ciclo): se l'iterazione APPENA
                        // CONCLUSA ha già consumato da sola più di IterationCostSafetyMultiplier
                        // volte il budget stimato, non arrischiare di avviarne un'altra — a
                        // differenza di "increaseDepth=false" (che riduce solo la profondità
                        // EFFETTIVA della prossima iterazione via searchAgainCounter, restando
                        // comunque esposti a una nuova iterazione altrettanto costosa) questo ferma
                        // del tutto la deepening. Non si applica mentre si sta pondering: lì il
                        // tempo "extra" non è mai davvero a rischio (search.cpp:613 lo forza sempre
                        // a continuare).
                        double iterationCostMs = elapsedMs - previousIterationElapsedMs;
                        if (!pondering && iterationCostMs > totalTime * IterationCostSafetyMultiplier)
                            break;
                    }

                    previousIterationElapsedMs = elapsedMs;
                }

                iterValue[iterIdx] = bestValue;
                iterIdx = (iterIdx + 1) & 3;
            }
        }
        catch (OperationCanceledException)
        {
            // Iterazione in corso interrotta a metà: si tiene il risultato dell'ultima completata.
        }

        // search.cpp:247-248 — sempre eseguito (indipendentemente da use_time_management), per la
        // prossima chiamata a Search_ nella stessa partita. Nella fonte usa "bestThread" (che può
        // essere un thread diverso da mainThread, scelto da get_best_thread) — qui ogni Search
        // aggiorna se stessa; SearchThreadPool.SetPreviousScores corregge il thread principale se
        // il vincitore del pool è un altro thread.
        if (_rootMoves.Count > 0)
        {
            _bestPreviousScore = _rootMoves[0].Score;
            _bestPreviousAverageScore = _rootMoves[0].AverageScore;
        }

        result.Nodes = _nodes;
        result.TbHits = _tbHits;
        return result;
    }

    private int Negamax(Position pos, int depth, int ply, int alpha, int beta, bool cutNode, Move excludedMove = default)
    {
        _nodes++;
        if ((_nodes & 2047) == 0)
        {
            _ct.ThrowIfCancellationRequested();

            // Scadenza MORBIDA (pratica, non di fonte — vedi _softDeadlineMs): il controllo fra
            // un'iterazione e l'altra non basta a limitare il tempo per mossa, perche' una singola
            // iterazione puo' costare piu' dell'intero budget (misurato: profondita' 16 costata 36s
            // su un budget di 40s, iniziata quando ne erano trascorsi solo 13). La fonte non ne ha
            // bisogno: le sue iterazioni sono piccole rispetto al budget. Qui l'interruzione a meta'
            // e' sicura e gia' supportata: OperationCanceledException viene catturata da Search_ e
            // si tiene il risultato dell'ultima iterazione COMPLETATA.
            double soft = _softDeadlineMs;
            if (soft > 0 && _elapsedStopwatch!.Elapsed.TotalMilliseconds > soft)
                throw new OperationCanceledException();
        }

        bool isPvNode = beta - alpha > 1;
        bool allNode = !isPvNode && !cutNode; // search.cpp:726 — !(PvNode||cutNode)
        // search.cpp:727 — usato da Step 9 (futility) e Step 16 (Singular Extensions). Con
        // rootMoves reali, "rootMoves[pvIdx].score" letto qui è ORA lo stesso identico riferimento
        // della fonte (non più un'approssimazione dall'ultima iterazione completata): quel campo
        // resta stabile per tutta la durata di QUESTA chiamata a Negamax(ply=0,...) — il
        // riordinamento dell'array avviene solo DOPO che questa chiamata ritorna (in Search_), mai
        // durante la ricorsione — esattamente come rootMoves[pvIdx] nella fonte fra due
        // stable_sort successivi (search.cpp:403).
        bool seekMate = _rootDepth >= 16 && Math.Abs(_rootMoves[_pvIdx].Score) >= 2000;

        // search.cpp:781-783 — selDepth conta da 1 (ply conta da 0), aggiornato dal primo nodo PV
        // che raggiunge un nuovo ply massimo in questa iterazione.
        if (isPvNode && _selDepth < ply + 1) _selDepth = ply + 1;

        // search.cpp:772-775 — vero se questo nodo è ancora sulla riga principale
        // dell'iterazione PRECEDENTE (radice sempre vera; altrimenti il genitore la seguiva E la
        // mossa che ci ha portati qui è esattamente quella della PV precedente a questo ply).
        // Posizionato qui (non subito dopo "Step 1" come nella fonte) perché in questo porting
        // Step 1/2/3 sono in un ordine leggermente diverso — nessuna dipendenza di dati fra
        // followPV e i controlli di patta/mate distance pruning sotto, quindi la posizione non
        // cambia il risultato.
        bool followPv = ply == 0
            || (_followPvHistory[ply + StackOffset - 1]
                && ply - 1 < _lastIterationIdxPv.Count
                && _currentMoveHistory[ply + StackOffset - 1] == _lastIterationIdxPv[ply - 1]);
        _followPvHistory[ply + StackOffset] = followPv;

        // Step 2. Controllo di patta immediata — search.cpp:787-790 (qui senza il controllo di
        // ricerca interrotta, gestito a parte da _ct.ThrowIfCancellationRequested sopra).
        if (ply != 0)
        {
            if (pos.IsDraw(ply) || ply >= Ply.MaxPly)
                return ply >= Ply.MaxPly && pos.Checkers() == 0 ? Evaluate.StaticEval(pos, _accumulatorStack, Optimism(pos.SideToMove)) : ValueDraw();

            // Step 3. Mate distance pruning — search.cpp:797-799. Esatta, non euristica: da
            // questo ply il miglior esito possibile è dare matto alla PROSSIMA mossa, il peggiore
            // essere già sotto matto adesso.
            //
            // Trascrizione corretta il 2026-09-07: il limite superiore della fonte è
            // "mate_in(ss->ply + 1)" (= MATE - ply - 1), non "mate_in(ss->ply)" — questo porting
            // usava un beta di un punto più largo, quindi tagliava meno proprio nelle posizioni di
            // matto. Anche la forma è tornata quella della fonte (due clamp + un solo controllo
            // "alpha >= beta" che ritorna alpha), invece di due rami separati che ritornavano il
            // valore di matto.
            alpha = Math.Max(-MateScore + ply, alpha);   // mated_in(ss->ply)
            beta = Math.Min(MateScore - ply - 1, beta);  // mate_in(ss->ply + 1)
            if (alpha >= beta) return alpha;
        }

        if (depth <= 0) return Quiesce(pos, alpha, beta, ply, isPvNode); // search.cpp:731

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

        // search.cpp:778 ("ss->moveCount = 0") e :809 ("ss->statScore = 0") — MAI PORTATI fino al
        // 2026-09-07. Nella fonte sono campi dello Stack azzerati all'ingresso di OGNI nodo;
        // questo porting li scriveva solo dentro il ciclo mosse, quindi un nodo che esce prima
        // (taglio da TT, null-move, razoring, tuffo in quiescenza...) lasciava allo stesso ply i
        // valori stantii di un fratello gia' cercato. Chi li legge sono i due rami che guardano
        // "com'e' andato il GENITORE": il malus alle mosse quiete provate presto (Step 6) e la
        // scala del bonus countermove su fail-low puro (Step 23) — entrambi leggevano quindi il
        // moveCount/statScore di un nodo che non era il proprio genitore.
        _moveCountHistory[ply + StackOffset] = 0;
        _statScoreHistory[ply + StackOffset] = 0;

        // search.cpp:807-808 — "consuma" la riduzione che il GENITORE ha applicato per arrivare
        // qui con LMR (0 se non è stata una ricerca ridotta), poi la azzera: serve solo una volta,
        // all'hindsight depth adjustment sotto.
        int priorReduction = _reductionHistory[ply + StackOffset - 1];
        _reductionHistory[ply + StackOffset - 1] = 0;

        var probe = _tt.Probe(pos.Key);
        int ttScore = probe.Found ? ValueFromTt(probe.Data.Value, ply, pos.Rule50Count) : Values.None;
        bool ttPv = excludedMove != default
            ? _ttPvHistory[ply + StackOffset]
            : isPvNode || (probe.Found && probe.Data.IsPv);
        _ttPvHistory[ply + StackOffset] = ttPv;

        // search.cpp:820 — "ttData.move = rootNode ? rootMoves[pvIdx].pv[0] : ttHit ? ttData.move
        // : Move::none();": alla radice la mossa usata per l'ordinamento di MovePicker e per tutti
        // i confronti "è la mossa di TT?" sotto è SEMPRE la miglior stima nota per QUESTA riga
        // (rootMoves[pvIdx].pv[0] — già legale per costruzione, mai vuota), non quella
        // eventualmente trovata nella TT reale (che potrebbe essere di profondità inferiore o
        // assente). Valore/profondità/bound/eval della TT restano invece sempre quelli
        // effettivamente sondati, SOLO la mossa è sostituita.
        Move ttMove = ply == 0 ? _rootMoves[_pvIdx].Pv[0] : probe.Found ? probe.Data.Move : Move.None;
        // search.cpp:824 usa capture_stage (che comprende anche le promozioni a donna su casa
        // vuota), non capture. Alimenta la riduzione LMR e i margini delle Singular Extensions.
        bool ttCapture = ttMove != Move.None && pos.CaptureStage(ttMove);

        int correctionValue = CorrectionValue(pos, ply);

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
            unadjustedStaticEval = probe.Found && Values.IsValid(probe.Data.Eval) ? probe.Data.Eval : Evaluate.StaticEval(pos, _accumulatorStack, Optimism(pos.SideToMove));
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

        // POSIZIONE: nella fonte lo Step 6 sta QUI (search.cpp:872), dopo lo Step 5 e dopo
        // l'hindsight adjustment appena sopra — non prima come stava in questo porting fino al
        // 2026-09-07. La differenza non e' cosmetica: tutte le soglie dello Step 6 (depth > 4,
        // depth >= 7, depth > 5, il bonus 112*depth) leggono la profondita' GIA' aggiustata, e la
        // scrittura in TT della sola valutazione statica (Step 5, ramo "!probe.Found") avviene
        // prima dell'eventuale taglio, non dopo.
        // Step 6. Taglio anticipato da transposition table nei nodi non-PV — search.cpp:872-921.
        // PORTATO FEDELMENTE il 2026-09-07. Prima c'era l'abbozzo del primissimo commit del
        // progetto: tre confronti Exact/Lower/Upper con "Depth >= depth" e nient'altro. Rispetto
        // alla fonte tagliava di PIU' (nessuna delle due guardie sotto) e senza nessuno dei tre
        // effetti collaterali: gli aggiornamenti di history sul taglio, la verifica del taglio a
        // profondita' alta, e l'invecchiamento della entry quando il taglio non scatta.
        //
        // Le due guardie mancanti:
        //  - "ttData.depth > depth - (ttData.value <= beta)": chiede UN PLY IN PIU' di profondita'
        //    memorizzata quando il valore in TT e' sopra beta (il caso in cui un taglio errato
        //    costa di piu');
        //  - "cutNode == (ttData.value >= beta) || depth > 4": sotto profondita' 5 non ci si fida
        //    di una entry che "va contro" l'aspettativa del nodo (un cutNode che dovrebbe fallire
        //    alto e trova un fail-low, o viceversa) — sono i casi in cui la entry e' piu' spesso
        //    frutto di una finestra di ricerca diversa.
        if (!isPvNode && excludedMove == default
            && probe.Data.Depth > depth - (ttScore <= beta ? 1 : 0)
            && Values.IsValid(ttScore)
            && (probe.Data.Bound & (ttScore >= beta ? Bound.Lower : Bound.Upper)) != Bound.None
            && (cutNode == (ttScore >= beta) || depth > 4))
        {
            // Se la mossa di TT e' quieta e fa fallire alto, premiala nell'ordinamento: la TT ci
            // sta risparmiando l'intero sottoalbero, ma senza questo la mossa non verrebbe mai
            // realmente cercata e le history non ne saprebbero nulla.
            if (ttMove != Move.None && ttScore >= beta)
            {
                if (!ttCapture)
                {
                    var cutContRefs = _contRefsBufs[ply];
                    FillContinuationRefs(ply, cutContRefs);
                    _movePick.ApplyTtCutoffQuietBonus(pos, ply, ttMove, Math.Min(112 * depth, 695), cutContRefs, inCheck);
                }

                // Malus extra alle mosse quiete provate presto dal genitore.
                Move ttCutPrevMove = _currentMoveHistory[ply + StackOffset - 1];
                if (ttCutPrevMove.IsOk && _moveCountHistory[ply + StackOffset - 1] < 5
                    && pos.CapturedPiece() == Piece.None)
                {
                    Square ttCutPrevSq = ttCutPrevMove.ToSq;
                    var ttCutPrevContRefs = _parentContRefsBufs[ply];
                    FillContinuationRefs(ply - 1, ttCutPrevContRefs);
                    _movePick.ApplyTtCutoffPrevPenalty(ttCutPrevContRefs, _inCheckHistory[ply + StackOffset - 1],
                        pos.PieceOn(ttCutPrevSq), ttCutPrevSq, -2210);
                }
            }

            // Rimedio parziale al "graph history interaction problem": con un contatore della
            // regola delle 50 mosse molto alto il valore in TT puo' essere stato calcolato in un
            // contesto in cui la patta era piu' lontana, quindi non si taglia affatto.
            if (pos.Rule50Count < 96)
            {
                if (depth >= 7 && ttMove != Move.None && pos.PseudoLegal(ttMove) && pos.Legal(ttMove)
                    && !Values.IsDecisive(ttScore))
                {
                    // Verifica il taglio giocando davvero la mossa di TT e sondando la posizione
                    // risultante: ci si fida solo se anche da li' il valore conferma il taglio.
                    // "do_move grezzo" come nella fonte (pos.do_move a due argomenti): qui non si
                    // valuta nulla, quindi l'accumulatore NNUE non va spinto.
                    var ttCutSt = _stateInfoPool[ply];
                    pos.DoMove(ttMove, ttCutSt);
                    var nextProbe = _tt.Probe(pos.Key);
                    pos.UndoMove(ttMove);

                    // La fonte confronta il valore GREZZO della entry successiva (nessun
                    // value_from_tt): serve solo il segno rispetto a beta, non un punteggio
                    // riferito a questo ply.
                    if (!Values.IsValid(nextProbe.Data.Value)) return ttScore;
                    if ((ttScore >= beta) == (-nextProbe.Data.Value >= beta)) return ttScore;
                }
                else
                return ttScore;
            }
        }
        // Nessun taglio: se l'UNICO motivo per cui non e' scattato e' che il bound della entry sta
        // dalla parte sbagliata rispetto alla finestra di aspirazione, quella entry non servira'
        // piu' a nessuno — si invecchia invece di lasciarla occupare spazio (search.cpp:915-921).
        else if (!isPvNode && excludedMove == default
            && probe.Data.Depth > depth - (ttScore <= beta ? 1 : 0)
            && Values.IsValid(ttScore)
            && probe.Data.Bound != Bound.Exact
            && (probe.Data.Bound & (ttScore >= beta ? Bound.Upper : Bound.Lower)) != Bound.None
            && depth > 5)
        {
            _tt.Penalize(probe.WriteIndex, 1);
        }


        // Step 7. Sonda le tablebase — search.cpp:922-973. tbBestValueFloor/tbMaxValueCap
        // sostituiscono l'aggiornamento diretto di bestValue/maxValue della fonte: qui "value" (il
        // running best value del ciclo mosse) non esiste ancora a questo punto della funzione
        // (dichiarato allo Step 14, appena prima del ciclo) — i due valori vengono consumati lì
        // (floor come valore iniziale invece di -Infinity) e alla fine della funzione (cap prima
        // del salvataggio in TT), esattamente come bestValue/maxValue nella fonte.
        int? tbBestValueFloor = null;
        int? tbMaxValueCap = null;
        if (ply != 0 && excludedMove == default && _tbConfig.Cardinality != 0)
        {
            int tbPieceCount = Bitboards.PopCount(pos.Pieces());

            if (tbPieceCount <= _tbConfig.Cardinality
                && (tbPieceCount < _tbConfig.Cardinality || depth >= _tbConfig.ProbeDepth)
                && pos.Rule50Count == 0 && !pos.CanCastle(CastlingRights.AnyCastling))
            {
                var wdl = Tablebase.ProbeWdl(pos, out var tbState);

                if (tbState != ProbeState.Fail)
                {
                    _tbHits++;

                    int drawScore = _tbConfig.UseRule50 ? 1 : 0;
                    int tbValue = Values.Tb - ply;
                    int wdlInt = (int)wdl;

                    int tbResultValue = wdlInt < -drawScore ? -tbValue
                        : wdlInt > drawScore ? tbValue
                        : Values.Draw + (2 * wdlInt * drawScore);

                    Bound tbBound = wdlInt < -drawScore ? Bound.Upper
                        : wdlInt > drawScore ? Bound.Lower
                        : Bound.Exact;

                    if (tbBound == Bound.Exact || (tbBound == Bound.Lower ? tbResultValue >= beta : tbResultValue <= alpha))
                    {
                        _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(tbResultValue, ply), ttPv, tbBound,
                            Math.Min(Ply.MaxPly - 1, depth + 6), Move.None, Values.None);
                        return tbResultValue;
                    }

                    if (isPvNode)
                    {
                        if (tbBound == Bound.Lower)
                        {
                            tbBestValueFloor = tbResultValue;
                            alpha = Math.Max(alpha, tbResultValue);
                        }
                        else
                            tbMaxValueCap = tbResultValue;
                    }
                }
            }
        }

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
                return Quiesce(pos, alpha, beta, ply, isPvNode: false); // search.cpp:992

            // Step 9. Futility pruning: nodo figlio — search.cpp:994-1008. La condizione sulla
            // profondità (6 se si "cerca il matto", 19 altrimenti) non va tarata: serve a non
            // troncare la ricerca quando un punteggio già alto suggerisce un matto vicino.
            if (!ttPv && depth < (seekMate ? 6 : 19) && eval >= beta
                && (ttMove == Move.None || ttCapture)
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

            // Step 10. Null move search with verification search — search.cpp:1009-1043.
            // PORTATO FEDELMENTE il 2026-09-07: fino a quel momento qui c'era ancora il segnaposto
            // scritto nel primissimo commit del progetto (0967bda, "nucleo ISPIRATO a search.cpp"),
            // che differiva dalla fonte su ogni punto e faceva moltissimo lavoro inutile:
            //  - provava il null move su OGNI nodo non-PV invece che solo sui cutNode (nei nodi
            //    "all", che per definizione non falliscono alto, il null move non taglia quasi mai:
            //    era lavoro sprecato a ogni nodo);
            //  - non aveva NESSUNA condizione sulla valutazione statica, quindi tentava il null
            //    move anche con la posizione molto sotto beta, dove non può ragionevolmente tagliare;
            //  - usava una riduzione FISSA (R=4) invece di quella dinamica della fonte, che cresce
            //    con la profondità e con quanto la valutazione supera beta;
            //  - non escludeva il caso excludedMove (le ricerche di verifica delle Singular
            //    Extensions), dove la fonte non lo fa mai;
            //  - restituiva "beta" invece di "nullValue", e non aveva la ricerca di verifica.
            // Misurato: era una delle cause principali del divario di ~5x nel numero di nodi
            // rispetto all'oracolo a parità di profondità (vedi docs/porting-master-plan.md).
            if (cutNode && staticEval >= beta - (13 * depth) - (47 * (improving ? 1 : 0)) + 365
                && excludedMove == default && HasNonPawnMaterial(pos, pos.SideToMove)
                && ply >= _nmpMinPly && beta >= -2000)
            {
                // Riduzione dinamica basata sulla profondità e su quanto la valutazione statica
                // supera beta — search.cpp:1016.
                int R = 7 + (depth / 3) + Math.Max((staticEval - beta) / 256, 0);

                var nullSt = _stateInfoPool[ply];
                pos.DoNullMove(nullSt);
                int nullValue = -Negamax(pos, depth - R, ply + 1, -beta, -beta + 1, cutNode: false);
                pos.UndoNullMove();

                // Non restituire matti o punteggi da tablebase non dimostrati.
                if (nullValue >= beta && !Values.IsWin(nullValue))
                {
                    if (_nmpMinPly != 0 || depth < 16) return nullValue;

                    // Ricerca di verifica alle profondità alte, col null move disattivato finché
                    // il ply non supera _nmpMinPly (la ricorsione non è ammessa) — search.cpp:1034.
                    _nmpMinPly = ply + (3 * (depth - R) / 4);
                    int v = Negamax(pos, depth - R, ply, beta - 1, beta, cutNode: false);
                    _nmpMinPly = 0;

                    if (v >= beta) return nullValue;
                }
            }

            // search.cpp:1046 — da qui in poi "improving" vale anche quando la valutazione statica
            // è già sopra beta. Mancava: influenza ProbCut (Step 12), la formula di riduzione LMR
            // e la soglia di potatura delle mosse tardive (Step 15).
            improving |= staticEval >= beta;

            // Step 11. Internal iterative reduction — search.cpp:1048-1052: a profondità
            // sufficiente, riduce la profondità nei nodi PV/Cut senza una mossa in TT (una TT
            // vuota qui è un segnale che questo nodo non è mai stato esplorato a sufficienza) —
            // MAI se stiamo ancora seguendo la riga principale dell'iterazione precedente
            // (followPv), che merita comunque piena profondità anche senza TT hit.
            if (!followPv && !allNode && depth >= 6 && ttMove == Move.None)
                depth--;

            // Step 12. ProbCut — search.cpp:1054-1096: se una cattura (o promozione) "abbastanza
            // buona" (SEE sopra la soglia probCutBeta-staticEval) regge una verifica di
            // quiescenza e, se serve, una ricerca ridotta, il nodo genitore può essere potato: la
            // mossa appena giocata sarebbe comunque troppo costosa per l'avversario.
            int probCutBeta = beta + 241 - (64 * (improving ? 1 : 0));
            if (depth >= 3 && !Values.IsDecisive(beta) && !(Values.IsValid(ttScore) && ttScore < probCutBeta))
            {
                int probCutDepth = depth - (improving ? 5 : 3);

                // MovePicker in modalità ProbCut (il secondo costruttore, movepick.cpp:181-189):
                // emette prima la mossa di TT se è una cattura pseudo-legale (anche senza ancora
                // verificarne la soglia SEE — search.cpp:1069 la controlla comunque con
                // pos.legal(), niente di più), poi le altre catture ordinate per MVV+capture
                // history filtrate da see_ge(mossa, threshold). Riusa gli stessi buffer per-ply
                // del MovePicker principale del ciclo mosse (costruito più sotto, allo Step 14) —
                // sicuro perché non c'è mai sovrapposizione: questo ProbCut finisce prima che
                // quello inizi.
                var probCutMp = new MovePicker(pos, _movePick, ttMove, probCutBeta - staticEval,
                    _mpMoveBufs[ply], _mpValueBufs[ply], _mpGenBufs[ply]);

                Move pcMove;
                while ((pcMove = probCutMp.NextMove()) != Move.None)
                {
                    if (pcMove == excludedMove) continue;
                    if (!pos.Legal(pcMove)) continue;

                    var pcFrame = _accumulatorStack.Push();
                    var pcSt = _stateInfoPool[ply];
                    pos.DoMove(pcMove, pcSt, pos.GivesCheck(pcMove), pcFrame.DirtyThreats, pcFrame.DirtyPiece, pcFrame.DirtyPawnPairs);

                    int pcValue = -Quiesce(pos, -probCutBeta, -probCutBeta + 1, ply + 1, isPvNode: false); // search.cpp:1077

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

        var contRefs = _contRefsBufs[ply];
        FillContinuationRefs(ply, contRefs);
        var mp = new MovePicker(pos, _movePick, ttMove, depth, ply, contRefs,
            _mpMoveBufs[ply], _mpValueBufs[ply], _mpGenBufs[ply]);

        int origAlpha = alpha;
        int value = tbBestValueFloor ?? -Infinity; // Step 1 della fonte, search.cpp:769: bestValue = -VALUE_INFINITE (salvo il floor dello Step 7)
        Move? bestMove = null;
        int moveCount = 0; // search.cpp:1116

        // search.cpp:761-762/1543-1551: mosse quiete/catture provate ma non risultate la
        // migliore, per aggiornare le loro statistiche di ordinamento a fine ciclo (Step 23).
        int searchedSlot = (ply * 2) + (excludedMove == default ? 0 : 1);
        var quietsSearched = _quietsSearchedBufs[searchedSlot];
        var capturesSearched = _capturesSearchedBufs[searchedSlot];
        quietsSearched.Clear();
        capturesSearched.Clear();
        const int SearchedListCapacity = 32; // SEARCHEDLIST_CAPACITY, search.cpp:73

        // Step 14. Generazione a stadi vera (MovePicker, movepick.cpp) invece della lista
        // pre-generata+ordinata: mp.NextMove() emette mosse pseudo-legali una alla volta
        // nell'ordine di merito stimato, search.cpp:1118-1129.
        Move m;
        while ((m = mp.NextMove()) != Move.None)
        {
            if (m == excludedMove) continue;
            if (!pos.Legal(m)) continue;

            // search.cpp:1131-1135 — alla radice, rispetta il gruppo di tbRank corrente
            // (_pvFirst.._pvLast, TB9): una mossa di rango inferiore non viene nemmeno provata
            // finché quelle di rango pari/superiore non sono tutte esaurite. Senza tablebase
            // attiva _pvFirst=0/_pvLast=numero di mosse: questo filtro non esclude mai nulla.
            if (ply == 0 && !RootMoveInRange(m))
                continue;

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
            // ora portato (vedi il gate sul ramo mosse quiete sotto).
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
                // search.cpp:1197 — "else if (!ss->followPV || !PvNode)": una mossa quieta sulla
                // riga principale dell'iterazione precedente, in un nodo PV, non viene mai potata
                // qui (né late move pruning delle quiete sopra la riguarda: quello agisce su
                // mp.SkipQuietMoves, non su questa singola mossa). Prima di followPv questa
                // potatura si applicava SEMPRE alle mosse quiete — piccola ma reale differenza
                // dalla fonte, ora corretta.
                else if (!followPv || !isPvNode)
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
            if (ply != 0 && m == ttMove && excludedMove == default && depth >= 6 + (ttPv ? 1 : 0)
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

            // search.cpp:1305 — nodi cercati da QUESTA mossa, letto solo alla radice per
            // aggiornare l'effort della rootMove corrispondente dopo l'undo.
            long nodeCountBeforeMove = ply == 0 ? _nodes : 0;

            var frame = _accumulatorStack.Push();
            var st = _stateInfoPool[ply];
            pos.DoMove(m, st, givesCheck, frame.DirtyThreats, frame.DirtyPiece, frame.DirtyPawnPairs);

            // Step 18 (continua dopo aver fatto la mossa), search.cpp:1316-1359.
            if (ttPv)
                r -= 3023 + (isPvNode ? 1004 : 0) + (ttScore > alpha ? 885 : 0) // niente is_valid: nella fonte VALUE_NONE==32002 > alpha
                    
                    + (probe.Data.Depth >= depth ? 816 + (cutNode ? 940 : 0) : 0);
            r += 697;
            r -= moveCount * 65;
            r -= Math.Abs(correctionValue) / 26310;
            if (cutNode) r += 4026 + (ttMove == Move.None ? 933 : 0);
            if (ttCapture) r += 1079;

            int childCutoffCnt = _cutoffCntHistory[ply + StackOffset + 1];
            if (childCutoffCnt > 1) r += 264 + (childCutoffCnt > 2 ? 1095 : 0) + (allNode ? 1138 : 0);
            else if (m == ttMove) r -= 2179;

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

                    // search.cpp:1383 — la fonte MUTA newDepth, non usa una variabile separata:
                    // l'aggiustamento resta visibile allo Step 20 sotto, che nei nodi PV rifara'
                    // la ricerca a finestra piena proprio a QUESTA profondita'. Con una locale
                    // (com'era qui fino al 2026-09-07) lo Step 20 ripartiva dalla profondita' non
                    // aggiustata, buttando via il verdetto della ricerca ridotta appena fatta.
                    newDepth += (doDeeperSearch ? 1 : 0) - (doShallowerSearch ? 1 : 0);

                    // search.cpp:1386 — finestra NULLA "-(alpha+1), -alpha", non "-beta": nei
                    // nodi non-PV e' la stessa cosa (beta==alpha+1), ma nei nodi PV questa era una
                    // ricerca a finestra PIENA, ripetuta subito dopo identica dallo Step 20.
                    if (newDepth > d)
                        score = -Negamax(pos, newDepth, ply + 1, -(alpha + 1), -alpha, cutNode: !cutNode);

                    // search.cpp:1389-1390 "Post LMR continuation history updates" — MAI PORTATO
                    // fino al 2026-09-07: la mossa che ha superato alpha in ricerca ridotta viene
                    // premiata sulle continuation history del nodo corrente.
                    _movePick.ApplyPostLmrBonus(contRefs, inCheck, pos.MovedPiece(m), m.ToSq);
                }
            }
            else if (!isPvNode || moveCount > 1)
            {
                int rNoTt = r + (ttMove == Move.None ? 1127 : 0);
                int searchDepth = newDepth - (rNoTt > 5234 ? 1 : 0) - (rNoTt > 5487 && newDepth > 2 ? 1 : 0);
                score = -Negamax(pos, searchDepth, ply + 1, -(alpha + 1), -alpha, cutNode: !cutNode);
            }
            else
            {
                score = -Infinity; // PV, prima mossa: sovrascritto incondizionatamente sotto (Step 20)
            }

            if (isPvNode && (moveCount == 1 || score > alpha))
            {
                // (ss+1)->pv->clear(), search.cpp:1411-1412 — SOLO qui il buffer PV del figlio
                // viene azzerato e poi (sotto, dopo il ritorno) riletto: una mossa che non arriva
                // fin qui non aggiorna mai il proprio bestMove/PV/rootMove (stessa condizione
                // "moveCount==1||score>alpha" del bookkeeping radice e dello Step 22 sotto), quindi
                // non legge mai un buffer lasciato da una mossa precedente.
                _pvBuf[ply + 1].Clear();

                // Step 20 (continua), search.cpp:1414-1420: se stiamo per tuffarci in quiescenza
                // (newDepth<=0 dopo tutte le riduzioni/estensioni sopra) con la STESSA mossa già
                // vista in TT a una profondità utile, un ply in più qui evita di perdere subito la
                // continuazione che la TT aveva già iniziato a esplorare.
                if (m == ttMove
                    && ((Values.IsValid(ttScore) && Values.IsDecisive(ttScore) && probe.Data.Depth > 0)
                        || probe.Data.Depth > 1))
                    newDepth = Math.Max(newDepth, 1);

                score = -Negamax(pos, newDepth, ply + 1, -beta, -alpha, cutNode: false);
            }

            pos.UndoMove(m);
            _accumulatorStack.Pop();

            // Bookkeeping delle rootMoves, search.cpp:1437-1506 — SOLO alla radice, PRIMA
            // dell'aggiornamento generico di bestValue/alpha (Step 22 sotto, search.cpp:1508-1541):
            // aggiorna effort/media mobile della entry corrispondente a questa mossa, poi il suo
            // punteggio/PV se è la prima mossa provata o ha appena superato alpha — le altre
            // restano a -Infinity per il resto di questa iterazione (l'ordinamento stabile in
            // Search_ le lascia comunque al loro posto relativo, search.h:149-151).
            if (ply == 0)
            {
                var rm = RootMove.Find(_rootMoves, m);

                ulong n = (ulong)(_nodes - nodeCountBeforeMove);
                rm.Effort += n;

                const ulong scale = 32, chiNumerator = 3, chiDenominator = 2, minWeight = 12, maxWeight = 24;
                ulong ePrev = Math.Max(1UL, rm.Effort - n);
                ulong w = Math.Clamp((scale * n * chiDenominator) / ((n * chiDenominator) + (chiNumerator * ePrev)), minWeight, maxWeight);
                ulong wMss = Math.Min(w, 16UL);
                long v2 = (long)score * Math.Abs(score);

                rm.AverageScore = rm.AverageScore == -Values.Infinite
                    ? score
                    : (int)((((long)score * (long)w) + ((long)rm.AverageScore * (long)(scale - w))) / (long)scale);

                rm.MeanSquaredScore = rm.MeanSquaredScore == -(long)Values.Infinite * Values.Infinite
                    ? (long)score * Math.Abs(score)
                    : ((v2 * (long)wMss) + (rm.MeanSquaredScore * (long)(scale - wMss))) / (long)scale;

                if (moveCount == 1 || score > alpha)
                {
                    rm.Score = rm.UciScore = score;
                    rm.SelDepth = _selDepth;
                    rm.UnsetInexact();

                    if (score >= beta) { rm.InexactLower = true; rm.UciScore = beta; }
                    else if (score <= alpha) { rm.InexactUpper = true; rm.UciScore = alpha; }

                    rm.Pv.Clear();
                    rm.Pv.Add(m);
                    rm.Pv.AddRange(_pvBuf[ply + 1]);

                    if (moveCount > 1 && _pvIdx == 0) _bestMoveChanges++;
                }
                else
                {
                    rm.Score = -Values.Infinite;
                }
            }

            // Step 22, search.cpp:1508-1541: bestMove si aggiorna SOLO quando la mossa supera
            // davvero alpha — un fail-low puro (nessuna mossa batte alpha) lascia bestMove a null,
            // esattamente come nella fonte.
            //
            // "inc" (search.cpp:1508-1510), portato il 2026-09-07 dopo essere stato dichiarato
            // "raffinamento minore non portato": quando una mossa PAREGGIA il miglior punteggio
            // trovato finora, ogni tanto (un nodo su otto, dal bit del contatore nodi) la si
            // promuove fingendo che superi alpha di un soffio — rompe le parità in modo che il
            // motore non resti sempre incollato alla prima mossa a pari merito.
            int inc = (score == value && ply + 2 >= _rootDepth && (_nodes & 14) == 0
                       && !Values.IsWin(Math.Abs(score) + 1)) ? 1 : 0;

            if (score + inc > value)
            {
                value = score;
                if (score + inc > alpha)
                {
                    bestMove = m;

                    // "Update PV even in fail-high case", search.cpp:1522-1524 — solo nei nodi
                    // PV non-radice (alla radice il PV è già stato aggiornato sopra, su rm.Pv).
                    if (isPvNode && ply != 0)
                    {
                        _pvBuf[ply].Clear();
                        _pvBuf[ply].Add(m);
                        _pvBuf[ply].AddRange(_pvBuf[ply + 1]);
                    }

                    if (score >= beta)
                    {
                        _cutoffCntHistory[ply + StackOffset] += (extension < 2 || isPvNode) ? 1 : 0; // search.cpp:1529
                        break;
                    }

                    // search.cpp:1533-1535 "Reduce other moves if we have found at least one score
                    // improvement" — MAI PORTATO fino al 2026-09-07. Appena una mossa migliora
                    // alpha senza far fallire alto, la fonte ABBASSA DI 3 la profondita' del nodo
                    // per TUTTE le mosse restanti: avendo gia' in mano un miglioramento, le altre
                    // vanno solo confutate, non approfondite. Muta il parametro "depth", quindi si
                    // ripercuote su newDepth, su Reduction() e sulle soglie dello Step 15 di ogni
                    // mossa successiva di questo nodo. E' una potatura molto ampia (agisce fra
                    // profondita' 4 e 11, dove vive la maggior parte dell'albero).
                    if (depth > 3 && depth < 12 && !Values.IsDecisive(score))
                        depth -= 3;

                    alpha = score;
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
            _movePick.UpdateStats(pos, ply, bestMove.Value, quietsSearched, capturesSearched, depth, ttMove, isPvNode, contRefs, inCheck);
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
                // search.cpp:766 — "priorCapture = pos.captured_piece()": e' il PEZZO
                // realmente catturato dalla mossa che ci ha portati qui, non il nostro flag
                // captureStage (che e' vero anche per una promozione a donna su casa vuota, dove
                // non c'e' nessun pezzo catturato e il ramo "else" sotto finirebbe a indicizzare
                // la capture history con PieceType.None).
                bool priorCapture = pos.CapturedPiece() != Piece.None;

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

                    // Buffer SEPARATO: _contRefsBufs[ply-1] appartiene al nodo genitore, che e'
                    // ancora vivo dentro il proprio ciclo mosse e lo sta usando.
                    var parentContRefs = _parentContRefsBufs[ply];
                    FillContinuationRefs(ply - 1, parentContRefs);
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

        // search.cpp:1611-1612: nei nodi PV, uno Step 7 che aveva impostato un tetto (posizione
        // giudicata al più patta/persa dalle tablebase, "maxValue") non deve mai essere superato
        // dal risultato del ciclo mosse.
        if (isPvNode && tbMaxValueCap.HasValue)
            value = Math.Min(value, tbMaxValueCap.Value);

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

        // search.cpp:1621: "!excludedMove && !(rootNode && pvIdx)" — mai scrivere in TT durante la
        // ricerca di verifica delle Singular Extensions (bound calcolato escludendo una mossa,
        // non rappresentativo della posizione vera) né, alla radice, per righe MultiPV oltre la
        // prima (qui pvIdx resta sempre 0, quindi questa seconda condizione non esclude mai nulla
        // finché MultiPV non è portato).
        if (excludedMove == default && !(ply == 0 && _pvIdx != 0))
        {
            var flag = value <= origAlpha ? Bound.Upper : value >= beta ? Bound.Lower : Bound.Exact;
            // search.cpp:1626 — "moveCount != 0 ? depth : std::min(MAX_PLY - 1, depth + 6)": un
            // nodo senza NESSUNA mossa legale (matto/stallo) e' un fatto definitivo, non una
            // stima a profondita' "depth", quindi si memorizza con profondita' maggiorata perche'
            // resista alla sostituzione.
            int savedDepth = moveCount != 0 ? depth : Math.Min(Ply.MaxPly - 1, depth + 6);
            _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(value, ply), ttPv, flag, savedDepth, bestMove ?? Move.None, unadjustedStaticEval);
        }

        return value;
    }

    /// <summary><c>Search::Worker::qsearch</c>, search.cpp:1653-1883 — PORTATA FEDELMENTE il
    /// 2026-09-07. Prima di quella data qui c'era l'abbozzo scritto nel primissimo commit del
    /// progetto: stand pat + catture con SEE non negativa, fail-hard, e NESSUNA consultazione della
    /// transposition table (ne' lettura, ne' taglio, ne' scrittura), nessuna potatura di futility,
    /// nessun limite sul numero di mosse. Dato che la quiescenza produce tipicamente la maggioranza
    /// dei nodi di un motore, non avere la TT qui significava ricalcolare da zero ogni
    /// trasposizione: il buco piu' costoso rimasto rispetto all'oracolo.</summary>
    private int Quiesce(Position pos, int alpha, int beta, int ply, bool isPvNode)
    {
        _nodes++;
        if ((_nodes & 2047) == 0)
        {
            _ct.ThrowIfCancellationRequested();
            double soft = _softDeadlineMs;
            if (soft > 0 && _elapsedStopwatch!.Elapsed.TotalMilliseconds > soft)
                throw new OperationCanceledException();
        }

        // search.cpp:1661-1667 — patta per ripetizione imminente.
        if (alpha < Values.Draw && pos.UpcomingRepetition(ply))
        {
            alpha = ValueDraw();
            if (alpha >= beta) return alpha;
        }

        bool inCheck = pos.Checkers() != 0;
        int moveCount = 0;
        Move bestMove = Move.None;

        if (isPvNode && _selDepth < ply + 1) _selDepth = ply + 1;

        // Step 2. Patta immediata o profondita' massima — search.cpp:1693-1696.
        if (pos.IsDraw(ply) || ply >= Ply.MaxPly)
            return ply >= Ply.MaxPly && !inCheck
                ? Evaluate.StaticEval(pos, _accumulatorStack, Optimism(pos.SideToMove))
                : ValueDraw();

        // Step 3. Consultazione della transposition table — search.cpp:1699-1711.
        var probe = _tt.Probe(pos.Key);
        Move ttMove = probe.Found ? probe.Data.Move : Move.None;
        int ttScore = probe.Found ? ValueFromTt(probe.Data.Value, ply, pos.Rule50Count) : Values.None;
        bool ttPv = probe.Found && probe.Data.IsPv;

        // Taglio anticipato da TT nei nodi non-PV.
        if (!isPvNode && probe.Data.Depth >= Ply.DepthQs && Values.IsValid(ttScore)
            && (probe.Data.Bound & (ttScore >= beta ? Bound.Lower : Bound.Upper)) != Bound.None)
            return ttScore;

        // Step 4. Valutazione statica — search.cpp:1713-1760.
        int unadjustedStaticEval = Values.None;
        int bestValue;
        int futilityBase;

        if (inCheck)
        {
            bestValue = futilityBase = -Infinity;
        }
        else
        {
            int correctionValue = CorrectionValue(pos, ply);

            if (probe.Found)
            {
                unadjustedStaticEval = probe.Data.Eval;
                if (!Values.IsValid(unadjustedStaticEval))
                    unadjustedStaticEval = Evaluate.StaticEval(pos, _accumulatorStack, Optimism(pos.SideToMove));

                bestValue = ToCorrectedStaticEval(unadjustedStaticEval, correctionValue);
                _staticEvalHistory[ply + StackOffset] = bestValue;

                // Il valore di TT puo' essere una stima migliore della valutazione statica.
                if (Values.IsValid(ttScore) && !Values.IsDecisive(ttScore)
                    && (probe.Data.Bound & (ttScore > bestValue ? Bound.Lower : Bound.Upper)) != Bound.None)
                    bestValue = ttScore;
            }
            else
            {
                unadjustedStaticEval = Evaluate.StaticEval(pos, _accumulatorStack, Optimism(pos.SideToMove));
                bestValue = ToCorrectedStaticEval(unadjustedStaticEval, correctionValue);
                _staticEvalHistory[ply + StackOffset] = bestValue;
            }

            // Stand pat: se la valutazione statica e' gia' almeno beta, si torna subito.
            if (bestValue >= beta)
            {
                if (!Values.IsDecisive(bestValue))
                    bestValue = ((441 * bestValue) + (583 * beta)) / 1024;

                if (!probe.Found)
                    _tt.Save(probe.WriteIndex, pos.Key, Values.None, false, Bound.Lower,
                        Ply.DepthUnsearched, Move.None, unadjustedStaticEval);

                return bestValue;
            }

            if (bestValue > alpha) alpha = bestValue;

            futilityBase = _staticEvalHistory[ply + StackOffset] + 306;
        }

        // search.cpp:1763-1765 — contHist di un solo livello (ss-1) e casa di arrivo della mossa
        // precedente, usata dal filtro di futility sotto.
        // La fonte in quiescenza usa UN SOLO livello di continuation history
        // (search.cpp:1763, "contHist[] = {(ss-1)->continuationHistory}"), non i sei del ciclo
        // principale: si costruisce l'array completo e si azzerano i livelli 1..5.
        var contRefs = _contRefsBufs[ply];
        FillContinuationRefs(ply, contRefs);
        for (int i = 1; i < contRefs.Length; i++) contRefs[i] = default;
        Move prevMove = _currentMoveHistory[ply + StackOffset - 1];
        Square prevSq = prevMove != Move.None ? prevMove.ToSq : Square.None;

        var mp = new MovePicker(pos, _movePick, ttMove, Ply.DepthQs, ply, contRefs,
            _mpMoveBufs[ply], _mpValueBufs[ply], _mpGenBufs[ply]);

        // Step 5. Ciclo su tutte le mosse pseudo-legali.
        Move m;
        while ((m = mp.NextMove()) != Move.None)
        {
            if (!pos.Legal(m)) continue;

            bool givesCheck = pos.GivesCheck(m);
            bool capture = pos.CaptureStage(m);

            moveCount++;

            // Step 6. Potatura — search.cpp:1786-1821.
            if (!Values.IsLoss(bestValue))
            {
                if (!givesCheck && m.ToSq != prevSq && !Values.IsLoss(futilityBase)
                    && m.TypeOf != MoveType.Promotion)
                {
                    if (moveCount > 2) continue;

                    int futilityValue = futilityBase + Values.PieceValue[(byte)pos.PieceOn(m.ToSq)];

                    // Valutazione statica + pezzo catturato molto sotto alpha: si pota.
                    if (futilityValue <= alpha)
                    {
                        bestValue = Math.Max(bestValue, futilityValue);
                        continue;
                    }

                    // SEE troppo bassa: si pota.
                    if (!pos.SeeGe(m, alpha - futilityBase))
                    {
                        bestValue = Math.Max(bestValue, Math.Min(alpha, futilityBase));
                        continue;
                    }
                }

                if (!capture) continue;               // salta le mosse quiete
                if (!pos.SeeGe(m, -74)) continue;     // catture con SEE troppo negativa
            }

            // Step 7. Fai e cerca la mossa.
            _currentMoveHistory[ply + StackOffset] = m;
            _movedPieceHistory[ply + StackOffset] = pos.MovedPiece(m);
            _inCheckHistory[ply + StackOffset] = inCheck;
            _captureStageHistory[ply + StackOffset] = capture;

            var qFrame = _accumulatorStack.Push();
            var st = _stateInfoPool[ply];
            pos.DoMove(m, st, givesCheck, qFrame.DirtyThreats, qFrame.DirtyPiece, qFrame.DirtyPawnPairs);
            int score = -Quiesce(pos, -beta, -alpha, ply + 1, isPvNode);
            pos.UndoMove(m);
            _accumulatorStack.Pop();

            // Step 8. Nuova mossa migliore (fail-soft) — search.cpp:1831-1849.
            if (score > bestValue)
            {
                bestValue = score;
                if (score > alpha)
                {
                    bestMove = m;
                    if (score < beta) alpha = score;
                    else break; // fail high
                }
            }
        }

        // Step 9. Matto e stallo — search.cpp:1852-1868. Lo stallo si controlla SOLO nelle
        // condizioni ristrette della fonte: fuori da quelle, "moveCount==0" in quiescenza
        // significa solo che non c'erano catture da provare, non che la posizione sia patta.
        if (moveCount == 0)
        {
            if (inCheck) return -(MateScore - ply);

            Color us = pos.SideToMove;
            if (pos.NonPawnMaterial(us) == 0
                && Types.TypeOf(pos.State.CapturedPiece) >= PieceType.Knight
                && (Bitboards.PawnSinglePushBB(us, pos.Pieces(us, PieceType.Pawn)) & ~pos.Pieces()) == 0)
            {
                var legal = _legalScratchBufs[ply];
                legal.Clear();
                MoveGen.Generate(GenType.Legal, pos, legal);
                if (legal.Count == 0) bestValue = Values.Draw;
            }
        }

        if (!Values.IsDecisive(bestValue) && bestValue > beta)
            bestValue = ((462 * bestValue) + (562 * beta)) / 1024;

        // Step 10. Salva in TT. La valutazione statica si salva com'era PRIMA della correzione.
        _tt.Save(probe.WriteIndex, pos.Key, ValueToTt(bestValue, ply), ttPv,
            bestValue >= beta ? Bound.Lower : Bound.Upper, Ply.DepthQs, bestMove, unadjustedStaticEval);

        return bestValue;
    }


    /// <summary>Costruisce <c>(ss-1)..(ss-6)-&gt;currentMove</c> per la continuation history —
    /// vedi MovePick.cs. Con StackOffset=7 l'indice minimo (ply=0, i=6) è 1, sempre non negativo.</summary>
    /// <summary>Riempie un buffer GIA' esistente invece di allocarne uno nuovo a ogni nodo — nella
    /// fonte e' l'array locale "contHist[]" sullo stack (search.cpp:1148-1151).</summary>
    private void FillContinuationRefs(int ply, ContinuationRef[] refs)
    {
        for (int i = 1; i <= 6; i++)
        {
            int idx = ply + StackOffset - i;
            refs[i - 1] = _currentMoveHistory[idx].IsOk
                ? new ContinuationRef(true, _inCheckHistory[idx], _captureStageHistory[idx], _movedPieceHistory[idx], _currentMoveHistory[idx].ToSq)
                : default;
        }
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

    /// <summary><c>std::count(rootMoves.begin()+pvIdx, rootMoves.begin()+pvLast, move)</c>,
    /// search.cpp:1134 — vero se <paramref name="m"/> è una delle mosse del gruppo
    /// <see cref="_pvFirst"/>.._pvLast corrente (TB9/searchmoves).</summary>
    private bool RootMoveInRange(Move m)
    {
        for (int i = _pvFirst; i < _pvLast; i++)
            if (_rootMoves[i].Matches(m))
                return true;
        return false;
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

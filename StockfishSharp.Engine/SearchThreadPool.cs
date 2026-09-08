// Porting del nucleo di src/thread.h + src/thread.cpp (C1, Lazy SMP) — Stockfish::ThreadPool,
// senza tutta l'infrastruttura NUMA/huge-page/thread nativi C++ (OptionalThreadToNumaNodeBinder,
// idle_loop con condition variable, allocazione allineata) — gestita dal runtime .NET stesso
// (System.Threading.Tasks), non parte dell'algoritmo. Chiamata "SearchThreadPool" invece di
// "ThreadPool" per non collidere con System.Threading.ThreadPool.
//
// I thread della fonte condividono DUE cose: la transposition table (Search::SharedState la passa
// per riferimento a ogni Worker, thread.h:194-204) e le SharedHistories (correction history,
// continuation history, pawn history — history.h:204-257, una copia per nodo NUMA). Restano private
// per thread: mainHistory, lowPlyHistory, captureHistory, continuationCorrectionHistory,
// ttMoveHistory, il MovePicker e l'AccumulatorStack (search.h:349-357).
//
// NOTA STORICA, seconda della serie: fino al 2026-09-08 questo commento diceva che la TT era
// "L'UNICA cosa che i thread condividono davvero" e che le history "restano sempre private per
// thread, esattamente come nella fonte". Era falso, e l'affermazione scritta qui ha fatto da
// coperchio a un pezzo mai portato per settimane — vedi docs/audit-fedelta.md, punto 2-ter.
// La TT stessa non richiede sincronizzazione esplicita: TranspositionTable.cs è già un array di
// struct (non riferimenti), quindi letture/scritture concorrenti su elementi diversi sono
// naturalmente sicure, e su un elemento condiviso possono al più produrre un mismatch di chiave
// scartato al prossimo probe — la stessa tolleranza al "quasi lockless" della fonte (tt.h/cpp),
// mai un crash o una corruzione strutturale.
//
// Fonte reale di parallelismo dei thread "helper" (indice > 0): NON diversificano la profondità
// per indice di thread (quella tecnica di versioni più vecchie di Stockfish non è più presente in
// questa) — l'unica differenza rispetto al main thread è che ignorano il limite di profondità
// richiesto da UCI e continuano fino a MAX_PLY finché il tempo non scade (search.cpp:333-334:
// "!(limits.depth && mainThread && rootDepth>=limits.depth)"), più la naturale non-determinismo
// del timing reale nell'accesso alla TT condivisa (thread diversi la popolano/leggono in ordini
// leggermente diversi). Nessun'altra diversificazione esplicita in questa versione della fonte.
//
// Semplificazione deliberata nella scelta del "miglior thread" (ThreadPool::get_best_thread,
// thread.cpp:357-408): la fonte vota sulla RootMove completa (pv/inexactLower/inexactUpper) di
// ogni thread. RootMove/RootMoves ora ESISTONO con fedeltà (StockfishSharp.Engine/RootMove.cs,
// 2026-09-06) ma restano PRIVATE a ogni Search — GetBestResult qui sotto vota ancora su
// SearchResult.BestMove/ScoreCp/Pv/SelDepth (i campi pubblici esposti da Search_) come proxy di
// rootMoves[0].pv[0]/.score/.pv/.selDepth (la nostra ricerca completa sempre l'ultima iterazione a
// finestra piena, quindi "IsInexact" è sempre falso per costruzione — non serve rappresentarlo).
// La formula di voto stessa (punteggio - minimo + 14, preferenza al mate più corto/lungo quando
// decisivo) è portata fedele, incluse le due guardie sul flag "decisivo" (score != -VALUE_INFINITE
// e !is_inexact, thread.cpp:378-384) e lo spareggio sulla lunghezza della PV.
//
// NOTA STORICA, da non ripetere: fino al 2026-09-08 questa nota affermava che "IsInexact è sempre
// falso per costruzione perché la nostra ricerca completa sempre l'ultima iterazione a finestra
// piena". È vero per il thread PRINCIPALE e FALSO per gli helper, che vengono cancellati a metà
// iterazione — cioè esattamente il caso che il commento della fonte descrive ("Aborted (d1)
// searches may lead to inexact win (or loss) scores"). Quell'assunzione scritta in un commento ha
// nascosto il bug per settimane: un helper interrotto poteva vincere il voto incondizionatamente e
// far giocare la sua mossa.

using StockfishSharp.Engine.Tablebases;

namespace StockfishSharp.Engine;

public sealed class SearchThreadPool
{
    private readonly TranspositionTable _tt = new();
    private readonly List<Search> _searches = [];
    private SharedHistories _sharedHistories = new(1);
    private (bool useRule50, int probeDepth, int probeLimit) _syzygyOptions = (true, 1, 7);

    public int ThreadCount => _searches.Count;

    /// <summary><c>updates.onUpdateFull</c> — la fonte la invoca solo da <c>mainThread</c>
    /// (search.cpp:495), quindi qui la si inoltra al solo thread 0. Sopravvive a
    /// <see cref="SetThreadCount"/>: i Search vengono ricreati, la callback no.</summary>
    public Action<InfoIterazione>? SuAggiornamentoPv
    {
        get => _suAggiornamentoPv;
        set
        {
            _suAggiornamentoPv = value;
            if (_searches.Count > 0) _searches[0].SuAggiornamentoPv = value;
        }
    }

    private Action<InfoIterazione>? _suAggiornamentoPv;

    /// <summary>Ricrea il pool con N thread di ricerca, tutti condividenti la stessa <see
    /// cref="TranspositionTable"/> e le stesse <see cref="SharedHistories"/> — <c>Threads.set</c>,
    /// thread.cpp:208-216 (MovePicker, AccumulatorStack e le history per thread restano private,
    /// ricreate da zero).</summary>
    public void SetThreadCount(int n)
    {
        n = Math.Max(1, n);
        _searches.Clear();

        // thread.cpp:208-216 — le SharedHistories si ricreano insieme ai thread e sono dimensionate
        // sul loro numero (next_power_of_two, fatto dentro il costruttore). Una sola istanza per
        // tutti: e' proprio la condivisione a essere il punto (vedi SharedHistories.cs).
        _sharedHistories = new SharedHistories(n);

        for (int i = 0; i < n; i++)
        {
            var s = new Search(_tt, _searches.Count, _sharedHistories); // threadIdx, search.cpp:173
            s.SetSyzygyOptions(_syzygyOptions.useRule50, _syzygyOptions.probeDepth, _syzygyOptions.probeLimit);
            if (i == 0) s.SuAggiornamentoPv = _suAggiornamentoPv; // solo mainThread, search.cpp:495
            s.SetMoveOverhead(_moveOverheadMs);
            _searches.Add(s);
        }
    }

    /// <summary>Propaga le opzioni UCI Syzygy grezze a tutti i thread del pool — ognuno le
    /// ricombina indipendentemente con <see cref="Tablebase.MaxCardinality"/> e la
    /// posizione corrente via <see cref="Tablebase.RankRootMoves"/> a ogni ricerca
    /// (thread.cpp:323 lo fa una volta sola a livello di pool e lo distribuisce; qui ogni thread
    /// lo ricalcola da sé sulla stessa posizione clonata — stesso risultato deterministico, solo
    /// lavoro ridondante fra thread, nessuna differenza di correttezza).</summary>
    public void SetSyzygyOptions(bool useRule50, int probeDepth, int probeLimit)
    {
        _syzygyOptions = (useRule50, probeDepth, probeLimit);
        foreach (var s in _searches) s.SetSyzygyOptions(useRule50, probeDepth, probeLimit);
    }

    /// <summary><c>options["Move Overhead"]</c> — serve a SyzygyExtendPv, che se ne concede la meta'
    /// come budget (search.cpp:2232-2238). Sopravvive a SetThreadCount come le opzioni Syzygy.</summary>
    public void SetMoveOverhead(long ms)
    {
        _moveOverheadMs = ms;
        foreach (var s in _searches) s.SetMoveOverhead(ms);
    }

    private long _moveOverheadMs = 10;

    /// <summary>Inoltra al thread principale (indice 0) — l'unico che consulta mai questi valori
    /// per la gestione tempo adattiva, vedi <see cref="Search.SetPreviousScores"/> — un
    /// aggiornamento di bestPreviousScore/bestPreviousAverageScore che non viene da una ricerca
    /// vera (es. una mossa di libro giocata dal chiamante: Program.cs).</summary>
    public void SetPreviousScores(int score, int averageScore)
    {
        if (_searches.Count == 0) SetThreadCount(1);
        _searches[0].SetPreviousScores(score, averageScore);
    }

    /// <summary><c>SearchManager::stopOnPonderhit</c> del thread principale (search.h:313) — vedi
    /// <see cref="Search.StopOnPonderhit"/>. Letto dal comando "ponderhit" (Program.cs) per
    /// decidere se fermare la ricerca subito o aspettare il vero tetto massimo.</summary>
    public bool StopOnPonderhit => _searches.Count > 0 && _searches[0].StopOnPonderhit;

    /// <summary>Sonda la transposition table condivisa — usata da <c>RootMove::
    /// extract_ponder_from_tt</c> (search.cpp:2350-2366) per trovare una mossa da suggerire alla
    /// GUI come "ponder" quando la PV aveva una sola mossa.</summary>
    public TTProbeResult ProbeTT(ulong key) => _tt.Probe(key);

    public void Resize(int hashMb) => _tt.Resize(hashMb);

    public void NewGame()
    {
        if (_searches.Count == 0) SetThreadCount(1);

        // Le tabelle condivise si azzerano UNA volta sola qui, non dentro ogni Search.NewGame():
        // nella fonte i thread se ne spartiscono le fette (clear_range, search.cpp:696-697), qui
        // basta un riempimento solo. Prima delle Search, perche' Search.NewGame azzera quelle
        // private e non deve trovarsi le condivise ancora sporche a meta'.
        _sharedHistories.Clear();

        foreach (var s in _searches) s.NewGame();
    }

    /// <summary><c>ThreadPool::start_thinking</c> + attesa sincrona del risultato (il nostro
    /// layer UCI, come per <see cref="Search.Search_"/> a thread singolo, gestisce l'esecuzione in
    /// background con un proprio <c>Task.Run</c> esterno — qui dentro l'attesa è bloccante).
    /// <paramref name="optimumMs"/> (vedi <see cref="Search.Search_"/>) è passato SOLO al thread
    /// principale (indice 0): gli helper, come nella fonte, non consultano mai la gestione tempo
    /// adattiva — vanno sempre fino a <c>Ply.MaxPly</c> e si fermano solo quando il thread
    /// principale (o il chiamante) cancella <paramref name="ct"/>.</summary>
    public SearchResult Search_(Position rootPos, int maxDepth, TimeSpan timeLimit, CancellationToken ct = default, long optimumMs = Search.NoBound,
        Func<bool>? isPondering = null, long maximumMsOverride = Search.NoBound)
    {
        if (_searches.Count == 0) SetThreadCount(1);

        if (_searches.Count == 1)
            return _searches[0].Search_(rootPos, maxDepth, timeLimit, ct, optimumMs: optimumMs,
                isPondering: isPondering, maximumMsOverride: maximumMsOverride);

        // tt.new_search(), search.cpp:204 — chiamato UNA SOLA VOLTA dal "thread principale"
        // (qui: il pool stesso, prima di avviare tutti i worker) e MAI dagli helper (vedi la nota
        // su Search.Search_(callNewSearch)). Chiamarlo per-thread avrebbe fatto incrementare
        // concorrentemente un byte non atomico, corrompendo l'invecchiamento della TT condivisa e
        // inquinandola progressivamente posizione dopo posizione in un bench multi-thread.
        _tt.NewSearch();

        // Ogni thread lavora sulla propria copia della Position radice, ma tutte condividono la
        // STESSA catena StateInfo storica (Previous/PliesFromNull/CapturedPiece) — vedi
        // Position.SetRootState per il perché (thread.cpp:332-346).
        string rootFen = rootPos.Fen();
        bool chess960 = rootPos.IsChess960;
        var positions = new Position[_searches.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = new Position();
            positions[i].Set(rootFen, chess960);
            positions[i].SetRootState(rootPos.State);
        }

        // Gli helper vanno oltre "maxDepth" (fino a MaxPly, search.cpp:333-334) e si fermano SOLO
        // per tempo scaduto — ma se il thread principale finisce PRIMA (perché ha raggiunto
        // "maxDepth" con tempo residuo, es. "go depth N" o un bench a profondità fissa), la fonte
        // li ferma subito impostando "threads.stop=true" (thread.cpp/search.cpp) invece di
        // lasciarli girare fino al budget di tempo — qui un CancellationTokenSource interno,
        // collegato al token esterno, cancellato non appena il thread principale termina, replica
        // lo stesso effetto.
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken stopCt = stopCts.Token;

        // TaskCreationOptions.LongRunning invece di Task.Run: una ricerca è CPU-bound per l'intera
        // durata di "timeLimit" (secondi, non microsecondi) — usare il ThreadPool .NET condiviso
        // (pensato per lavori BREVI) rischia di esaurirne i thread disponibili quando la stessa
        // SearchThreadPool viene richiamata molte volte in sequenza (es. "bench", 51 posizioni): il
        // throttling del pool (~1 nuovo thread/secondo) mette in coda i thread di ricerca
        // successivi dietro quelli precedenti non ancora rilasciati, rallentando progressivamente
        // ogni chiamata — LongRunning chiede invece un vero System.Threading.Thread dedicato.
        // search.cpp:562-566 — SOLO il thread principale legge e azzera bestMoveChanges di OGNI
        // thread del pool (compreso se stesso), non ciascun thread il proprio: se ogni Search
        // azzerasse il proprio contatore per conto suo, il thread principale leggerebbe sempre 0
        // dagli altri (già azzerati) invece del loro vero accumulo — bug reale osservato dal vivo
        // sul bot (2026-09-06): con 8 thread indipendenti la volatilità del solo thread principale
        // (non mediata sugli altri 7) poteva gonfiare "bestMoveInstability" molto più del dovuto,
        // facendo impiegare tempi spropositati su mosse in posizioni genuinamente instabili.
        ulong SumAndResetBestMoveChangesAcrossPool()
        {
            ulong total = 0;
            foreach (var s in _searches) total += s.PeekAndResetBestMoveChanges();
            return total;
        }

        var results = new SearchResult?[_searches.Count];
        var tasks = new Task[_searches.Count];
        for (int i = 0; i < _searches.Count; i++)
        {
            int idx = i;
            if (idx == 0)
            {
                // Il thread principale rispetta il limite di profondità richiesto da UCI.
                tasks[idx] = Task.Factory.StartNew(() =>
                {
                    results[idx] = _searches[idx].Search_(positions[idx], maxDepth, timeLimit, stopCt, callNewSearch: false, optimumMs: optimumMs,
                        crossThreadBestMoveChanges: SumAndResetBestMoveChangesAcrossPool, threadCountForInstability: _searches.Count,
                        isPondering: isPondering, maximumMsOverride: maximumMsOverride);
                    stopCts.Cancel();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            else
            {
                tasks[idx] = Task.Factory.StartNew(
                    () => results[idx] = _searches[idx].Search_(positions[idx], Ply.MaxPly, timeLimit, stopCt, callNewSearch: false),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        Task.WaitAll(tasks);

        var best = GetBestResult(results!);

        // search.cpp:247-248 — "main_manager()->bestPreviousScore = bestThread->rootMoves[0].score"
        // è SEMPRE il vincitore del voto, anche quando è un thread diverso da quello principale:
        // senza questa propagazione, la prossima chiamata a Search_ del thread principale userebbe
        // i PROPRI valori (magari peggiori) invece di quelli della riga davvero scelta.
        _searches[0].SetPreviousScores(best.ScoreCp, best.AverageScore);

        // search.cpp:255 — "if (!uciPvSent || bestThread != this)": se il voto ha scelto un thread
        // diverso dal principale, le righe "info" emesse durante la ricerca (solo dal principale)
        // descrivono un'ALTRA linea, quindi la riga finale va comunque ristampata.
        if (!ReferenceEquals(best, results[0])) best.UciPvSent = false;

        return best;
    }

    /// <summary><c>ThreadPool::get_best_thread</c>, thread.cpp:357-408 — vedi la nota in testa al
    /// file per le sostituzioni (BestMove/ScoreCp/Depth al posto di RootMove.pv/score/pv.size()).</summary>
    private static SearchResult GetBestResult(SearchResult[] results)
    {
        int minScore = results.Min(r => r.ScoreCp);

        var votes = new Dictionary<Move, long>();
        foreach (var r in results)
            if (r.BestMove is { } m)
                votes[m] = votes.GetValueOrDefault(m) + (r.ScoreCp - minScore + 14);

        SearchResult best = results[0];
        for (int i = 1; i < results.Length; i++)
        {
            SearchResult cand = results[i];
            if (cand.BestMove is not { } candMove) continue;
            if (best.BestMove is not { } bestMove) { best = cand; continue; }

            // thread.cpp:378-384 — il flag "decisivo" ha TRE condizioni nella fonte, non una.
            // Le altre due proteggono da un caso che qui si verifica eccome, e che il commento
            // della fonte descrive alla lettera: "Aborted (d1) searches may lead to inexact win
            // (or loss) scores". Gli helper vengono cancellati a meta' iterazione quando il thread
            // principale finisce, quindi la loro rootMoves[0] puo' portare un punteggio da
            // fail-high/fail-low (un BOUND, non un valore esatto) oppure restare a -Infinite se
            // non e' mai stata valutata. Senza le guardie, un simile punteggio conta come
            // "decisivo" e per la regola sotto vince il voto INCONDIZIONATAMENTE: la mossa di un
            // helper interrotto, cercata a profondita' irrisoria, finisce giocata sulla
            // scacchiera.
            //
            // Il commento precedente in testa a questo file sosteneva che IsInexact fosse "sempre
            // falso per costruzione" perche' la nostra ricerca completa sempre l'ultima iterazione
            // a finestra piena. E' vero per il thread principale, FALSO per gli helper.
            bool bestDecisive = best.ScoreCp != -Values.Infinite && Values.IsDecisive(best.ScoreCp) && !best.IsInexact;
            bool candDecisive = cand.ScoreCp != -Values.Infinite && Values.IsDecisive(cand.ScoreCp) && !cand.IsInexact;
            long bestVotes = votes.GetValueOrDefault(bestMove);
            long candVotes = votes.GetValueOrDefault(candMove);

            if (bestDecisive)
            {
                // Assicura di scegliere il matto/conversione più corta.
                if (candDecisive && Math.Abs(cand.ScoreCp) > Math.Abs(best.ScoreCp))
                    best = cand;
            }
            else if (candDecisive
                     || (!Values.IsLoss(cand.ScoreCp)
                         && (candVotes > bestVotes
                             // thread.cpp:392 spareggia sulla LUNGHEZZA DELLA PV, non sulla profondita'.
                             || (candVotes == bestVotes && cand.Pv.Count > best.Pv.Count))))
            {
                best = cand;
            }
        }

        return new SearchResult
        {
            BestMove = best.BestMove,
            Pv = best.Pv,
            SelDepth = best.SelDepth,
            ScoreCp = best.ScoreCp,
            AverageScore = best.AverageScore,
            Depth = best.Depth,
            Nodes = results.Sum(r => r.Nodes), // Threads::nodes_searched, thread.cpp — somma su tutti i thread
            TbHits = results.Sum(r => r.TbHits), // Threads::tb_hits(), thread.cpp — idem
        };
    }
}

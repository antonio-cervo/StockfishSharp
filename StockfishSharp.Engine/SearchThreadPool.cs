// Porting del nucleo di src/thread.h + src/thread.cpp (C1, Lazy SMP) — Stockfish::ThreadPool,
// senza tutta l'infrastruttura NUMA/huge-page/thread nativi C++ (OptionalThreadToNumaNodeBinder,
// idle_loop con condition variable, allocazione allineata) — gestita dal runtime .NET stesso
// (System.Threading.Tasks), non parte dell'algoritmo. Chiamata "SearchThreadPool" invece di
// "ThreadPool" per non collidere con System.Threading.ThreadPool.
//
// L'UNICA cosa che i thread della fonte condividono davvero è la transposition table
// (Search::SharedState la passa per riferimento a ogni Worker, thread.h:194-204) — le history/
// MovePick/AccumulatorStack restano sempre private per thread, esattamente come nella fonte.
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
// ogni thread — una struttura che questo porting non ha ancora (RootMoves complete sono anche il
// prerequisito di MultiPV, Flow A4, non ancora portato). Qui si usa SearchResult.BestMove/ScoreCp
// come proxy di rootMoves[0].pv[0]/.score (la nostra ricerca completa sempre l'ultima iterazione a
// finestra piena, quindi "IsInexact" è sempre falso per costruzione — non serve rappresentarlo) e
// SearchResult.Depth come proxy della lunghezza del PV per lo spareggio finale. La formula di voto
// stessa (punteggio - minimo + 14, preferenza al mate più corto/lungo quando decisivo) è portata
// fedele.

using StockfishSharp.Engine.Tablebases;

namespace StockfishSharp.Engine;

public sealed class SearchThreadPool
{
    private readonly TranspositionTable _tt = new();
    private readonly List<Search> _searches = [];
    private TbConfig _tbConfig;

    public int ThreadCount => _searches.Count;

    /// <summary>Ricrea il pool con N thread di ricerca, tutti condividenti la stessa <see
    /// cref="TranspositionTable"/> — <c>Threads.set</c>, thread.cpp (le history/MovePick/
    /// AccumulatorStack di ciascuno restano private, ricreate da zero).</summary>
    public void SetThreadCount(int n)
    {
        n = Math.Max(1, n);
        _searches.Clear();
        for (int i = 0; i < n; i++)
        {
            var s = new Search(_tt);
            s.SetTbConfig(_tbConfig);
            _searches.Add(s);
        }
    }

    /// <summary>Propaga la configurazione Syzygy (TB10) a tutti i thread del pool — replicata,
    /// non condivisa: ogni <see cref="Search"/> la legge sola-lettura durante la ricerca (stessa
    /// tbConfig per ogni Search::Worker nella fonte, rank_root_moves/thread.cpp).</summary>
    public void SetTbConfig(TbConfig config)
    {
        _tbConfig = config;
        foreach (var s in _searches) s.SetTbConfig(config);
    }

    public void Resize(int hashMb) => _tt.Resize(hashMb);

    public void NewGame()
    {
        if (_searches.Count == 0) SetThreadCount(1);
        foreach (var s in _searches) s.NewGame();
    }

    /// <summary><c>ThreadPool::start_thinking</c> + attesa sincrona del risultato (il nostro
    /// layer UCI, come per <see cref="Search.Search_"/> a thread singolo, gestisce l'esecuzione in
    /// background con un proprio <c>Task.Run</c> esterno — qui dentro l'attesa è bloccante).</summary>
    public SearchResult Search_(Position rootPos, int maxDepth, TimeSpan timeLimit, CancellationToken ct = default)
    {
        if (_searches.Count == 0) SetThreadCount(1);

        if (_searches.Count == 1)
            return _searches[0].Search_(rootPos, maxDepth, timeLimit, ct);

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
                    results[idx] = _searches[idx].Search_(positions[idx], maxDepth, timeLimit, stopCt, callNewSearch: false);
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

        return GetBestResult(results!);
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

            bool bestDecisive = Values.IsDecisive(best.ScoreCp);
            bool candDecisive = Values.IsDecisive(cand.ScoreCp);
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
                         && (candVotes > bestVotes || (candVotes == bestVotes && cand.Depth > best.Depth))))
            {
                best = cand;
            }
        }

        return new SearchResult
        {
            BestMove = best.BestMove,
            ScoreCp = best.ScoreCp,
            Depth = best.Depth,
            Nodes = results.Sum(r => r.Nodes), // Threads::nodes_searched, thread.cpp — somma su tutti i thread
            TbHits = results.Sum(r => r.TbHits), // Threads::tb_hits(), thread.cpp — idem
        };
    }
}

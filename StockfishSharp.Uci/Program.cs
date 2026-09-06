using System.Diagnostics;
using System.Linq;
using StockfishSharp.Engine;
using StockfishSharp.Engine.Tablebases;
using StockfishSharp.Uci;
using File = StockfishSharp.Engine.File;

// Punto di ingresso UCI. Non un porting di src/uci.cpp+ucioption.cpp (~1500 righe insieme, con
// tutta l'infrastruttura di opzioni generiche, benchmark, comandi di debug) — un layer minimo ma
// funzionante che copre i comandi che servono a giocare davvero una partita: uci/isready/
// ucinewgame/position/go/setoption Hash/quit. Il porting fedele di uci.cpp resta un obiettivo
// per la Fase 4 del piano.

Attacks.EnsureInitialized();
Position.Init();

// Percorso della rete di default, non ancora configurabile via UCI (l'opzione "EvalFile" fa
// parte di ucioption.cpp, non ancora portato — Flow A "debito" in docs/porting-master-plan.md).
// Se il file non c'è (~100MB, gitignored — vedi docs/nnue-porting-plan.md per l'URL), si continua
// con il placeholder materiale+PSQT invece di rifiutarsi di avviarsi come fa la fonte reale: qui
// serve poter lavorare anche senza il file scaricato.
const string DefaultNetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";
if (System.IO.File.Exists(DefaultNetworkPath))
{
    Evaluate.NnueNetwork = StockfishSharp.Engine.Nnue.NnueNetwork.Load(DefaultNetworkPath);
    Console.WriteLine($"info string NNUE evaluation using {System.IO.Path.GetFileName(DefaultNetworkPath)}");
}
else
{
    Console.WriteLine("info string NNUE network not found, using placeholder material+PSQT evaluation");
}

// Libro di aperture Polyglot (Flow D1, non un porting — Stockfish non ne ha uno integrato).
// Percorso relativo all'eseguibile, gitignored (file di terze parti), stessa convenzione di
// ACMyChess.Uci. Se il file manca, il motore prosegue normalmente senza libro.
const int BookMaxPlies = 24; // oltre questa profondità non si consulta più il libro
string bookPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Book", "performance.bin");
var book = PolyglotBook.TryLoad(bookPath);
Console.WriteLine(book != null
    ? $"info string opening book loaded from {System.IO.Path.GetFileName(bookPath)}"
    : "info string no opening book found, playing without one");

// UCI_Chess960, engine.cpp:113 (options.add("UCI_Chess960", Option(false))) — usata sia da
// HandlePosition sia dal comando bench (la lista Defaults reale include due posizioni Chess960
// racchiuse fra "setoption name UCI_Chess960 value true/false").
bool isChess960 = false;

var position = new Position();
position.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960);

var search = new SearchThreadPool();
search.SetThreadCount(8);
search.Resize(16);
search.NewGame();
var timeManagement = new TimeManagement();

// Opzioni Syzygy (TB9+TB10, rank_root_moves/Config, engine.cpp) — SyzygyPath vuoto di default
// come la fonte reale (nessuna tablebase caricata: Tablebase.MaxCardinality resta 0 finché
// Tablebase.Init non ne carica almeno una, quindi Tablebase.RankRootMoves disattiva da sé il
// probing — non serve più un caso speciale esplicito qui come prima di TB9). Gli altri tre
// default (Syzygy50MoveRule=true, SyzygyProbeDepth=1, SyzygyProbeLimit=7) sono gli stessi della
// fonte (engine.cpp:117-123).
string syzygyPath = "";
bool syzygy50MoveRule = true;
int syzygyProbeDepth = 1;
int syzygyProbeLimit = 7;

void UpdateSyzygyOptions() => search.SetSyzygyOptions(syzygy50MoveRule, syzygyProbeDepth, syzygyProbeLimit);
UpdateSyzygyOptions();
int maxDepth = 30;

// Una ricerca ("go") gira su un task in background invece che bloccare questo ciclo: un client
// UCI reale (GUI o lichess-bot) può mandare "stop" mentre il motore sta ancora pensando e si
// aspetta un "bestmove" pronto subito dopo — con una chiamata sincrona qui, "stop" non sarebbe mai
// letto finché la ricerca non finisce da sola. Non un porting (Flow A4, uci.cpp non ancora
// portato) — ingegneria pratica sul nostro layer UCI minimo, che già non è fedele.
CancellationTokenSource? searchCts = null;
Task? searchTask = null;

// Lista "Defaults" reale di benchmark.cpp:34-101 (51 posizioni + le tre righe "setoption name
// UCI_Chess960" che le racchiudono) — copiata identica, incluse le mosse incorporate in alcune
// righe ("... moves d4e6") già gestite da HandlePosition. Dichiarata qui, prima del ciclo "while"
// principale che può invocare HandleBench: in top-level statements l'assegnazione di una
// variabile locale deve precedere testualmente il primo punto di ESECUZIONE che la referenzia.
string[] BenchDefaults =
[
    "setoption name UCI_Chess960 value false",
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 10",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11",
    "4rrk1/pp1n3p/3q2pQ/2p1pb2/2PP4/2P3N1/P2B2PP/4RRK1 b - - 7 19",
    "rq3rk1/ppp2ppp/1bnpb3/3N2B1/3NP3/7P/PPPQ1PP1/2KR3R w - - 7 14 moves d4e6",
    "r1bq1r1k/1pp1n1pp/1p1p4/4p2Q/4Pp2/1BNP4/PPP2PPP/3R1RK1 w - - 2 14 moves g2g4",
    "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
    "r1bbk1nr/pp3p1p/2n5/1N4p1/2Np1B2/8/PPP2PPP/2KR1B1R w kq - 0 13",
    "r1bq1rk1/ppp1nppp/4n3/3p3Q/3P4/1BP1B3/PP1N2PP/R4RK1 w - - 1 16",
    "4r1k1/r1q2ppp/ppp2n2/4P3/5Rb1/1N1BQ3/PPP3PP/R5K1 w - - 1 17",
    "2rqkb1r/ppp2p2/2npb1p1/1N1Nn2p/2P1PP2/8/PP2B1PP/R1BQK2R b KQ - 0 11",
    "r1bq1r1k/b1p1npp1/p2p3p/1p6/3PP3/1B2NN2/PP3PPP/R2Q1RK1 w - - 1 16",
    "3r1rk1/p5pp/bpp1pp2/8/q1PP1P2/b3P3/P2NQRPP/1R2B1K1 b - - 6 22",
    "r1q2rk1/2p1bppp/2Pp4/p6b/Q1PNp3/4B3/PP1R1PPP/2K4R w - - 2 18",
    "4k2r/1pb2ppp/1p2p3/1R1p4/3P4/2r1PN2/P4PPP/1R4K1 b - - 3 22",
    "3q2k1/pb3p1p/4pbp1/2r5/PpN2N2/1P2P2P/5PP1/Q2R2K1 b - - 4 26",
    "6k1/6p1/6Pp/ppp5/3pn2P/1P3K2/1PP2P2/3N4 b - - 0 1",
    "3b4/5kp1/1p1p1p1p/pP1PpP1P/P1P1P3/3KN3/8/8 w - - 0 1",
    "2K5/p7/7P/5pR1/8/5k2/r7/8 w - - 0 1 moves g5g6 f3e3 g6g5 e3f3",
    "8/6pk/1p6/8/PP3p1p/5P2/4KP1q/3Q4 w - - 0 1",
    "7k/3p2pp/4q3/8/4Q3/5Kp1/P6b/8 w - - 0 1",
    "8/2p5/8/2kPKp1p/2p4P/2P5/3P4/8 w - - 0 1",
    "8/1p3pp1/7p/5P1P/2k3P1/8/2K2P2/8 w - - 0 1",
    "8/pp2r1k1/2p1p3/3pP2p/1P1P1P1P/P5KR/8/8 w - - 0 1",
    "8/3p4/p1bk3p/Pp6/1Kp1PpPp/2P2P1P/2P5/5B2 b - - 0 1",
    "5k2/7R/4P2p/5K2/p1r2P1p/8/8/8 b - - 0 1",
    "6k1/6p1/P6p/r1N5/5p2/7P/1b3PP1/4R1K1 w - - 0 1",
    "1r3k2/4q3/2Pp3b/3Bp3/2Q2p2/1p1P2P1/1P2KP2/3N4 w - - 0 1",
    "6k1/4pp1p/3p2p1/P1pPb3/R7/1r2P1PP/3B1P2/6K1 w - - 0 1",
    "8/3p3B/5p2/5P2/p7/PP5b/k7/6K1 w - - 0 1",
    "5rk1/q6p/2p3bR/1pPp1rP1/1P1Pp3/P3B1Q1/1K3P2/R7 w - - 93 90",
    "4rrk1/1p1nq3/p7/2p1P1pp/3P2bp/3Q1Bn1/PPPB4/1K2R1NR w - - 40 21",
    "r3k2r/3nnpbp/q2pp1p1/p7/Pp1PPPP1/4BNN1/1P5P/R2Q1RK1 w kq - 0 16",
    "3Qb1k1/1r2ppb1/pN1n2q1/Pp1Pp1Pr/4P2p/4BP2/4B1R1/1R5K b - - 11 40",
    "4k3/3q1r2/1N2r1b1/3ppN2/2nPP3/1B1R2n1/2R1Q3/3K4 w - - 5 1",
    "1r6/1P4bk/3qr1p1/N6p/3pp2P/6R1/3Q1PP1/1R4K1 w - - 1 42",
    "k7/2n1n3/1nbNbn2/2NbRBn1/1nbRQR2/2NBRBN1/3N1N2/7K w - - 0 1",
    "K7/8/8/BNQNQNB1/N5N1/R1Q1q2r/n5n1/bnqnqnbk w - - 0 1",
    "8/8/8/8/5kp1/P7/8/1K1N4 w - - 0 1",
    "8/8/8/5N2/8/p7/8/2NK3k w - - 0 1",
    "8/3k4/8/8/8/4B3/4KB2/2B5 w - - 0 1",
    "8/8/1P6/5pr1/8/4R3/7k/2K5 w - - 0 1",
    "8/2p4P/8/kr6/6R1/8/8/1K6 w - - 0 1",
    "8/8/3P3k/8/1p6/8/1P6/1K3n2 b - - 0 1",
    "8/R7/2q5/8/6k1/8/1P5p/K6R w - - 0 124",
    "6k1/3b3r/1p1p4/p1n2p2/1PPNpP1q/P3Q1p1/1R1RB1P1/5K2 b - - 0 1",
    "r2r1n2/pp2bk2/2p1p2p/3q4/3PN1QP/2P3R1/P4PP1/5RK1 w - - 0 1",
    "8/8/8/8/8/6k1/6p1/6K1 w - -",
    "7k/7P/6K1/8/3B4/8/8/8 b - -",
    "setoption name UCI_Chess960 value true",
    "bbqnnrkr/pppppppp/8/8/8/8/PPPPPPPP/BBQNNRKR w HFhf - 0 1 moves g2g3 d7d5 d2d4 c8h3 c1g5 e8d6 g5e7 f7f6",
    "nqbnrkrb/pppppppp/8/8/8/8/PPPPPPPP/NQBNRKRB w KQkq - 0 1",
    "setoption name UCI_Chess960 value false",
];

void StopSearch()
{
    if (searchTask is { IsCompleted: false })
    {
        searchCts?.Cancel();
        searchTask.Wait();
    }
}

while (Console.ReadLine() is { } line)
{
    var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length == 0) continue;

    switch (tokens[0])
    {
        case "uci":
            Console.WriteLine("id name StockfishSharp (porting in corso)");
            Console.WriteLine("id author Antonio Cervo, porting da Stockfish (GPLv3)");
            Console.WriteLine("option name Hash type spin default 16 min 1 max 4096");
            Console.WriteLine($"option name Threads type spin default 8 min 1 max {Environment.ProcessorCount}");
            Console.WriteLine("option name UCI_Chess960 type check default false");
            Console.WriteLine("option name SyzygyPath type string default <empty>");
            Console.WriteLine("option name SyzygyProbeDepth type spin default 1 min 1 max 100");
            Console.WriteLine("option name Syzygy50MoveRule type check default true");
            Console.WriteLine("option name SyzygyProbeLimit type spin default 7 min 0 max 7");
            Console.WriteLine("uciok");
            break;

        case "isready":
            Console.WriteLine("readyok");
            break;

        case "ucinewgame":
            StopSearch();
            search.NewGame();
            timeManagement.NewGame();
            break;

        case "setoption":
            HandleSetOption(tokens);
            break;

        case "position":
            StopSearch();
            HandlePosition(tokens);
            break;

        case "go":
            StopSearch();
            HandleGo(tokens);
            break;

        case "stop":
            searchCts?.Cancel();
            break;

        case "bench":
            StopSearch();
            HandleBench(tokens);
            break;

        // Comandi non-UCI di debug, uci.cpp:147-183 — "Add custom non-UCI commands, mainly for
        // debugging purposes".
        case "d":
            Console.WriteLine(Visualize(position));
            break;

        case "eval":
            Console.WriteLine($"info string static eval (side to move): {Evaluate.StaticEval(position)}");
            break;

        case "flip":
            StopSearch();
            position.Flip();
            break;

        case "compiler":
            Console.WriteLine(CompilerInfo());
            break;

        case "--help":
        case "help":
        case "--license":
        case "license":
            Console.WriteLine();
            Console.WriteLine("Stockfish is a powerful chess engine for playing and analyzing.");
            Console.WriteLine("It is released as free software licensed under the GNU GPLv3 License.");
            Console.WriteLine("Stockfish is normally used with a graphical user interface (GUI) and implements");
            Console.WriteLine("the Universal Chess Interface (UCI) protocol to communicate with a GUI, an API, etc.");
            Console.WriteLine("For any further information, visit https://github.com/official-stockfish/Stockfish#readme");
            Console.WriteLine("or read the corresponding README.md and Copying.txt files distributed along with this program.");
            break;

        case "quit":
            StopSearch();
            return;
    }
}

void HandleSetOption(string[] toks)
{
    int nameIdx = Array.IndexOf(toks, "name");
    int valueIdx = Array.IndexOf(toks, "value");
    if (nameIdx < 0 || valueIdx < 0 || valueIdx <= nameIdx) return;

    string name = string.Join(' ', toks[(nameIdx + 1)..valueIdx]);
    string value = string.Join(' ', toks[(valueIdx + 1)..]);

    if (string.Equals(name, "Hash", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int mb))
        search.Resize(mb);
    else if (string.Equals(name, "Threads", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int threads))
    {
        // Ricrea il pool (C1, Lazy SMP) — mai durante una ricerca attiva per protocollo, ma
        // StopSearch() per sicurezza in ogni caso.
        StopSearch();
        search.SetThreadCount(threads);
        search.NewGame();
    }
    else if (string.Equals(name, "UCI_Chess960", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool chess960))
        isChess960 = chess960;
    else if (string.Equals(name, "SyzygyPath", StringComparison.OrdinalIgnoreCase))
    {
        syzygyPath = value;
        Tablebase.Init(syzygyPath); // Tablebases::init, chiamato ad ogni cambio di SyzygyPath
        UpdateSyzygyOptions();
    }
    else if (string.Equals(name, "SyzygyProbeDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int probeDepth))
    {
        syzygyProbeDepth = probeDepth;
        UpdateSyzygyOptions();
    }
    else if (string.Equals(name, "Syzygy50MoveRule", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool rule50))
    {
        syzygy50MoveRule = rule50;
        UpdateSyzygyOptions();
    }
    else if (string.Equals(name, "SyzygyProbeLimit", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int probeLimit))
    {
        syzygyProbeLimit = probeLimit;
        UpdateSyzygyOptions();
    }
}

void HandlePosition(string[] toks)
{
    int idx = 1;
    if (idx >= toks.Length) return;

    if (toks[idx] == "startpos")
    {
        position.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960);
        idx++;
    }
    else if (toks[idx] == "fen")
    {
        idx++;
        int fenStart = idx;
        while (idx < toks.Length && toks[idx] != "moves") idx++;
        string fen = string.Join(' ', toks[fenStart..idx]);
        position.Set(fen, isChess960);
    }
    else
    {
        return;
    }

    if (idx < toks.Length && toks[idx] == "moves")
    {
        idx++;
        for (; idx < toks.Length; idx++)
        {
            Move? m = ParseUciMove(toks[idx]);
            if (m == null) continue;
            var st = new StateInfo();
            position.DoMove(m.Value, st);
        }
    }
}

// UCIEngine::to_move, uci.cpp — confronta ogni mossa legale con la stringa in arrivo
// convertendola PRIMA in notazione UCI (MoveToUci), non confrontando le case grezze: per
// l'arrocco la nostra rappresentazione interna è "il re cattura la propria torre" (Move.ToSq =
// casa della torre, es. e8h8 per il nero lato re), mentre una GUI/bot reale manda sempre la
// notazione standard (e8g8) quando non è Chess960 — un confronto diretto sulle case non li fa
// mai combaciare. Bug reale trovato in due partite del bot (2026-09-06): un arrocco nella
// cronologia mosse veniva scartato silenziosamente ("continue" sul null qui sotto), lasciando la
// posizione interna un ply indietro per il resto della partita — sintomo osservato: una mossa
// sensata ma per il lato SBAGLIATO (l'engine rispondeva ancora come se toccasse al lato che
// aveva già arroccato). Stessa tecnica della fonte: MoveToUci già fa la conversione corretta
// (vedi lì), quindi basta confrontare le stringhe invece delle case.
Move? ParseUciMove(string uci)
{
    if (uci.Length < 4) return null;

    var legalMoves = new List<Move>();
    MoveGen.Generate(GenType.Legal, position, legalMoves);

    foreach (var m in legalMoves)
        if (MoveToUci(m) == uci)
            return m;

    return null;
}

void HandleGo(string[] toks)
{
    long? GetLong(string key)
    {
        int i = Array.IndexOf(toks, key);
        return i >= 0 && i + 1 < toks.Length && long.TryParse(toks[i + 1], out long v) ? v : null;
    }

    // "go perft N", uci.cpp:224-225 + Engine::perft — bypassa sia il libro che la ricerca vera.
    var perftDepth = GetLong("perft");
    if (perftDepth.HasValue)
    {
        long nodes = Perft.Run(position, (int)perftDepth.Value);
        Console.WriteLine($"\nNodes searched: {nodes}\n");
        return;
    }

    // Libro di aperture (Flow D1): se la posizione è coperta, risponde subito senza avviare la
    // ricerca vera — stessa logica di ACMyChess.Uci/Program.cs.
    if (position.GamePly < BookMaxPlies)
    {
        Move? bookMove = book?.TryGetMove(position);
        if (bookMove.HasValue)
        {
            Console.WriteLine($"bestmove {MoveToUci(bookMove.Value)}");
            return;
        }
    }

    var movetime = GetLong("movetime");
    var depthArg = GetLong("depth");
    bool infinite = Array.IndexOf(toks, "infinite") >= 0;

    TimeSpan budget;
    // limits.use_time_management(), search.h:182 — vero solo quando la GUI ha fornito wtime/btime
    // reali (ramo "else" sotto): in quel caso "budget" diventa il tetto ASSOLUTO
    // (TimeManagement.MaximumTime, equivalente di tm.maximum()) e optimumMs attiva la gestione
    // tempo adattiva reale dentro Search_ (search.cpp:568-618), che normalmente si ferma MOLTO
    // prima del tetto. Per "infinite"/"movetime"/"go depth" senza orologio, come nella fonte,
    // niente gestione adattiva: optimumMs resta Search.NoBound e budget è l'unico limite, fisso.
    long optimumMs = Search.NoBound;
    if (infinite)
    {
        // Nessun limite di tempo reale: si ferma solo con "stop" o al raggiungimento di maxDepth
        // (il vero "infinite" della fonte non ha nemmeno quel limite, ma un tetto pratico qui
        // evita una ricerca che non termina mai se il client non manda mai "stop").
        budget = TimeSpan.FromHours(1);
    }
    else if (movetime.HasValue)
    {
        budget = TimeSpan.FromMilliseconds(Math.Max(50, movetime.Value - 50));
    }
    else
    {
        Color us = position.SideToMove;
        long? myTime = us == Color.White ? GetLong("wtime") : GetLong("btime");
        long myInc = (us == Color.White ? GetLong("winc") : GetLong("binc")) ?? 0;
        int movesToGo = (int)(GetLong("movestogo") ?? 0);

        if (myTime.HasValue)
        {
            timeManagement.Init(myTime.Value, myInc, movesToGo, position.GamePly);
            budget = TimeSpan.FromMilliseconds(timeManagement.MaximumTime);
            optimumMs = timeManagement.OptimumTime;
        }
        else
        {
            budget = TimeSpan.FromSeconds(10); // "go depth N" senza orologio: budget fisso ragionevole
        }
    }

    int depth = depthArg.HasValue ? (int)Math.Min(depthArg.Value, maxDepth) : maxDepth;

    searchCts = new CancellationTokenSource();
    var ct = searchCts.Token;
    var pos = position; // stesso oggetto Position: il client UCI non deve mandare "position"/"go"
                        // finché non riceve "bestmove" o manda "stop" prima (regola del protocollo).

    searchTask = Task.Run(() =>
    {
        var result = search.Search_(pos, depth, budget, ct, optimumMs: optimumMs);
        Console.WriteLine($"info depth {result.Depth} seldepth {result.SelDepth} score cp {result.ScoreCp} nodes {result.Nodes} tbhits {result.TbHits} pv {FormatPv(result.Pv)}");

        // Matto/stallo: la TT salva Move.None come bestMove (Search.cs, "bestMove ?? Move.None"),
        // quindi result.BestMove.HasValue è vero anche qui — senza questo controllo aggiuntivo
        // MoveToUci(Move.None) stamperebbe "a1a1" (Move.None ha from=to=A1) invece di "0000",
        // una mossa illegale spedita a un client UCI reale. Scoperto testando "bench".
        Console.WriteLine(result.BestMove is { } m && m != Move.None
            ? $"bestmove {MoveToUci(m)}"
            : "bestmove 0000");
    });
}

// Comando "bench" — corrisponde a UCIEngine::bench (uci.cpp:248-312) + Benchmark::setup_bench
// (benchmark.cpp:395-447), con scope ridotto al nostro motore: solo "limitType" depth/movetime
// (non eval/nodes/perft, tecniche non applicabili o non portate qui), solo "fenFile" default (non
// un file esterno o "current"). "threads" ora onorato davvero (C1, Lazy SMP FATTO) invece di
// essere accettato solo per compatibilità di sintassi. Esegue le ricerche in modo SINCRONO (a
// differenza di "go", che gira in background) perché qui serve il risultato di ogni posizione
// prima di passare alla successiva, esattamente come nella fonte.
void HandleBench(string[] toks)
{
    string ttSize = toks.Length > 1 ? toks[1] : "16";
    string threadsArg = toks.Length > 2 ? toks[2] : "1";
    string limit = toks.Length > 3 ? toks[3] : "13";
    string fenFile = toks.Length > 4 ? toks[4] : "default";
    string limitType = toks.Length > 5 ? toks[5] : "depth";

    if (int.TryParse(threadsArg, out int benchThreads) && benchThreads > 0)
        search.SetThreadCount(benchThreads);
    if (fenFile != "default")
    {
        Console.Error.WriteLine($"info string bench: 'fenFile' non supportato ({fenFile}), uso 'default'");
        fenFile = "default";
    }
    if (limitType != "depth" && limitType != "movetime")
    {
        Console.Error.WriteLine($"info string bench: 'limitType' non supportato ({limitType}), uso 'depth'");
        limitType = "depth";
    }

    if (int.TryParse(ttSize, out int mb)) search.Resize(mb);
    search.NewGame();
    timeManagement.NewGame();

    int total = BenchDefaults.Count(f => !f.StartsWith("setoption", StringComparison.Ordinal));
    int cnt = 0;
    long totalNodes = 0;
    var sw = Stopwatch.StartNew();

    foreach (string entry in BenchDefaults)
    {
        if (entry.StartsWith("setoption", StringComparison.Ordinal))
        {
            HandleSetOption(entry.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            continue;
        }

        cnt++;
        Console.Error.WriteLine($"\nPosizione: {cnt}/{total} ({entry})");
        HandlePosition(("position fen " + entry).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        SearchResult result = limitType == "movetime" && long.TryParse(limit, out long ms)
            ? search.Search_(position, maxDepth, TimeSpan.FromMilliseconds(ms), CancellationToken.None)
            : search.Search_(position, int.TryParse(limit, out int d) ? d : 13, TimeSpan.FromHours(1), CancellationToken.None);

        totalNodes += result.Nodes;
        Console.WriteLine($"info depth {result.Depth} seldepth {result.SelDepth} score cp {result.ScoreCp} nodes {result.Nodes} tbhits {result.TbHits} pv {FormatPv(result.Pv)}");
        Console.WriteLine(result.BestMove is { } bm && bm != Move.None ? $"bestmove {MoveToUci(bm)}" : "bestmove 0000");
    }

    sw.Stop();
    long elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);
    Console.Error.WriteLine("\n===========================");
    Console.Error.WriteLine($"Total time (ms) : {elapsedMs}");
    Console.Error.WriteLine($"Nodes searched  : {totalNodes}");
    Console.Error.WriteLine($"Nodes/second    : {1000 * totalNodes / elapsedMs}");
}

// UCIEngine::move, uci.cpp — la rappresentazione interna dell'arrocco è "il re cattura la
// propria torre" (Move.ToSq = casa della torre, stessa convenzione della fonte reale e del
// formato Polyglot), ma una GUI/bot non-Chess960 si aspetta la notazione standard (il re alla
// sua casa finale, es. e1g1 non e1h1). Senza questa conversione ogni arrocco proposto da questo
// motore verrebbe rifiutato come mossa illegale — bug reale trovato in una partita del bot,
// 2026-09-06 (vedi anche la nota gemella in ParseUciMove, stesso problema in direzione opposta).
string MoveToUci(Move m)
{
    Square from = m.FromSq;
    Square to = m.ToSq;

    if (m.TypeOf == MoveType.Castling && !isChess960)
        to = Types.MakeSquare(to > from ? File.G : File.C, Types.RankOf(from));

    string s = SquareToString(from) + SquareToString(to);
    if (m.TypeOf == MoveType.Promotion)
    {
        s += m.PromotionType switch
        {
            PieceType.Queen => "q",
            PieceType.Rook => "r",
            PieceType.Bishop => "b",
            PieceType.Knight => "n",
            _ => "",
        };
    }

    return s;
}

// Formatta la riga "pv" di "info depth ..." — SearchResult.Pv è ora la vera continuazione
// (RootMove.Pv, search.h:165) invece del solo bestmove: una sola mossa quando il porting
// del PV multi-mossa non era ancora fatto è ora il caso raro (fail-high/basso senza tempo per il
// re-search a finestra piena), non più la norma.
string FormatPv(List<Move> pv) => string.Join(' ', pv.Select(MoveToUci));

string SquareToString(Square s) => $"{(char)('a' + (byte)Types.FileOf(s))}{(char)('1' + (byte)Types.RankOf(s))}";

// operator<<(ostream&, const Position&), position.cpp:67-103 — comando "d". Senza la parte
// tablebase (Syzygy WDL/DTZ, Flow C2 non ancora portato).
const string PieceToCharUci = " PNBRQK  pnbrqk";
string Visualize(Position pos)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("\n +---+---+---+---+---+---+---+---+\n");

    for (var r = Rank.Rank8; ; r--)
    {
        for (var f = File.A; f <= File.H; f++)
            sb.Append(" | ").Append(PieceToCharUci[(byte)pos.PieceOn(Types.MakeSquare(f, r))]);

        sb.Append(" | ").Append(1 + (byte)r).Append("\n +---+---+---+---+---+---+---+---+\n");
        if (r == Rank.Rank1) break;
    }

    sb.Append("   a   b   c   d   e   f   g   h\n");
    sb.Append($"\nFen: {pos.Fen()}\nKey: {pos.Key:X16}\nCheckers: ");

    ulong checkers = pos.Checkers();
    while (checkers != 0)
        sb.Append(SquareToString(Bitboards.PopLsb(ref checkers))).Append(' ');

    return sb.ToString();
}

// compiler_info(), misc.cpp — versione minima: qui non c'è un compilatore C++/preprocessore da
// interrogare, solo l'informazione equivalente per la runtime .NET.
string CompilerInfo() =>
    $"\nCompiled by                : .NET SDK {Environment.Version}"
    + $"\nCompiled on                : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"
    + $"\nCompilation architecture   : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"
    + $"\nCompilation settings       : {(Environment.Is64BitProcess ? "64bit" : "32bit")}\n";

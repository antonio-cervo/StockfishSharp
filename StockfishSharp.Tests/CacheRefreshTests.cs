using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Le "Finny Tables" (<see cref="CacheRefresh"/>, AccumulatorCaches della fonte) sono una
/// pura ottimizzazione: il refresh che passa dalla cache DEVE produrre accumulatori bit-identici a
/// quello che riparte dai bias. Se cosi' non fosse, la valutazione cambierebbe in modo silenzioso e
/// dipendente dalla storia — il tipo di guasto piu' difficile da inseguire.
///
/// Il caso che conta davvero e' il RIUSO: la stessa casa del re rivisitata con una disposizione di
/// pezzi diversa da quella memorizzata. Un refresh da zero non puo' sbagliarlo per costruzione, la
/// cache si', se la differenza fra i pezzi e' calcolata male o la voce non viene aggiornata.</summary>
public class CacheRefreshTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public CacheRefreshTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Position Posizione(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    private static void ConfrontaConRicalcoloDaZero(NnueNetwork net, Position pos, CacheRefresh cache, string contesto)
    {
        foreach (var perspective in new[] { Color.White, Color.Black })
        {
            int p = (byte)perspective;

            var daZero = new NnueAccumulator();
            daZero.RefreshPerspective(net, pos, perspective);

            var conCache = new NnueAccumulator();
            conCache.RefreshPerspectiveConCache(net, pos, perspective, cache);

            Assert.True(conCache.Accumulation[p].AsSpan().SequenceEqual(daZero.Accumulation[p]),
                $"{contesto}: accumulation[{perspective}] dalla cache diversa dal ricalcolo da zero.");
            Assert.True(conCache.PsqtAccumulation[p].AsSpan().SequenceEqual(daZero.PsqtAccumulation[p]),
                $"{contesto}: psqtAccumulation[{perspective}] dalla cache diversa dal ricalcolo da zero.");
        }
    }

    [Fact]
    public void LaCacheDaGliStessiAccumulatoriDelRicalcoloDaZero()
    {
        var net = NnueNetwork.Load(NetworkPath);
        var cache = new CacheRefresh();
        cache.Svuota(net);

        // Posizioni scelte per battere la cache dove puo' rompersi: stessa casa del re con pezzi
        // diversi, catture, promozioni, arrocchi (il re CAMBIA casa e poi ci torna), finali spogli.
        string[] fen =
        [
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1",
            "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
            "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/3P1N2/PPP2PPP/RNBQK2R w KQkq - 2 5",
            "r1bq1rk1/pppp1ppp/2n2n2/2b1p3/2B1P3/3P1N2/PPP2PPP/RNBQ1RK1 w - - 6 7",
            "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            "8/8/5k1p/6PP/5K2/8/8/8 b - - 0 90",
            "5QN1/pp1r1ppk/5n2/2p4P/8/2P5/3B1PP1/q1KRR3 w - - 6 30",
            "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
            // ritorno a posizioni gia' viste: la voce in cache ora contiene tutt'altro
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
        ];

        foreach (var f in fen)
            ConfrontaConRicalcoloDaZero(net, Posizione(f), cache, f);
    }

    [Fact]
    public void LaCacheReggeUnaPartitaIntera()
    {
        var net = NnueNetwork.Load(NetworkPath);
        var cache = new CacheRefresh();
        cache.Svuota(net);

        // Cammino DETERMINISTICO su mosse legali generate (seme fisso): tocca arrocchi, catture,
        // promozioni e spostamenti di re senza doverli scrivere a mano e senza rischiare mosse
        // illegali. A ogni semimossa la stessa voce di cache viene riusata con un contenuto diverso.
        var pos = new Position();
        pos.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);
        var rng = new Random(20260910);
        var stati = new List<StateInfo>();

        ConfrontaConRicalcoloDaZero(net, pos, cache, "posizione iniziale");
        for (int ply = 0; ply < 40; ply++)
        {
            var legali = new List<Move>();
            MoveGen.Generate(GenType.Legal, pos, legali);
            if (legali.Count == 0) break;

            var m = legali[rng.Next(legali.Count)];
            var st = new StateInfo();
            stati.Add(st);
            pos.DoMove(m, st);
            ConfrontaConRicalcoloDaZero(net, pos, cache, $"semimossa {ply + 1}");
        }
    }
}

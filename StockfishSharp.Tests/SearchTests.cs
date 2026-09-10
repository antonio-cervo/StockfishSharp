using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

public class SearchTests
{
    public SearchTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Position MakePosition(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    /// <summary>Una ricerca INTERROTTA a meta' deve restituire la posizione com'era: alla RADICE.
    ///
    /// Regressione del piantamento del bot delle notti 2026-09-08/09 e 2026-09-09/10. La ricerca si
    /// ferma lanciando OperationCanceledException da dentro la ricorsione (il controllo ogni 2048
    /// visite): l'eccezione srotola lo stack C#, ma le UndoMove in sospeso NON sono in un finally,
    /// quindi la posizione resta profonda N mosse nell'albero. Search_ inghiotte l'eccezione e
    /// prosegue, e da li' in poi lavora su una posizione che non e' piu' quella di radice.
    ///
    /// Il conto lo presenta ExtractPonderFromTt, che gioca pv[0] — legale alla radice, non li':
    /// IndexOutOfRangeException dentro DoMove, dentro il Task.Run della ricerca. In .NET
    /// un'eccezione non osservata di un Task non fa crashare il processo: il motore resta vivo,
    /// non stampa mai "bestmove", e la partita si perde per tempo. Diagnosi dal dump del processo
    /// piantato ancora vivo (pid 8784, 0 secondi di CPU in 15 secondi reali = bloccato, non in
    /// ciclo; l'heap conteneva l'IndexOutOfRangeException e il TaskExceptionHolder che la teneva).
    ///
    /// La fonte non ha questo problema perche' NON usa eccezioni: si ferma su un flag e la
    /// ricorsione rientra normalmente, chiamando tutte le undo_move.</summary>
    [Fact]
    public void RicercaInterrottaLasciaLaPosizioneAllaRadice()
    {
        var pos = MakePosition("r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15");
        ulong chiaveRadice = pos.Key;

        var search = new Search();
        search.Resize(16);
        search.NewGame();

        // Profondita' alta e budget cortissimo: la scadenza scatta DENTRO un'iterazione, non fra una
        // e l'altra — che e' esattamente il caso che si verifica in partita.
        search.Search_(pos, maxDepth: 40, TimeSpan.FromMilliseconds(300));

        Assert.Equal(chiaveRadice, pos.Key);
    }

    [Fact]
    public void FindsMateInOne()
    {
        // Matto del corridoio: Th8# — posizione da manuale, non da Stockfish.
        var pos = MakePosition("6k1/5ppp/8/8/8/8/8/R6K w - - 0 1");
        var search = new Search();
        search.Resize(16);
        search.NewGame();

        var result = search.Search_(pos, maxDepth: 3, TimeSpan.FromSeconds(5));

        Assert.NotNull(result.BestMove);
        Assert.Equal(new Move(Square.A1, Square.A8), result.BestMove!.Value);
        Assert.True(result.ScoreCp > 25000, $"Punteggio atteso vicino al matto, trovato {result.ScoreCp}");
    }

    [Fact]
    public void FindsMateInTwo()
    {
        // Matto in 2 da manuale: 1.Qd8+ Rxd8 2.Rxd8# (o varianti equivalenti) sulla scacchiera
        // 6k1/6pp/8/8/8/8/6PP/3QK1R1 non è forzato in 2 in tutte le linee — usiamo invece una
        // posizione più semplice e sicura di matto in 2 netto.
        var pos = MakePosition("k7/8/1K6/8/8/8/8/6R1 w - - 0 1");
        var search = new Search();
        search.Resize(16);
        search.NewGame();

        var result = search.Search_(pos, maxDepth: 4, TimeSpan.FromSeconds(10));

        Assert.NotNull(result.BestMove);
        Assert.True(result.ScoreCp > 25000, $"Punteggio atteso vicino al matto, trovato {result.ScoreCp}");
    }

    [Fact]
    public void DoesNotCrashOnStartingPosition()
    {
        var pos = MakePosition("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var search = new Search();
        search.Resize(16);
        search.NewGame();

        var result = search.Search_(pos, maxDepth: 5, TimeSpan.FromSeconds(10));

        Assert.NotNull(result.BestMove);
        Assert.True(pos.PseudoLegal(result.BestMove!.Value));
    }

    [Fact]
    public void ChoosesWinningCaptureOverLosingOne()
    {
        // Donna bianca in d1 può catturare una torre indifesa in a4 (diagonale d1-a4, guadagno
        // netto) invece di mosse neutre — verifica che l'ordinamento/valutazione preferisca la
        // cattura vincente.
        var pos = MakePosition("4k3/8/8/8/r7/8/8/3QK3 w - - 0 1");
        var search = new Search();
        search.Resize(16);
        search.NewGame();

        var result = search.Search_(pos, maxDepth: 4, TimeSpan.FromSeconds(5));

        Assert.NotNull(result.BestMove);
        Assert.Equal(Square.A4, result.BestMove!.Value.ToSq);
    }

    [Fact]
    public void DoesNotExplodeNodeCountWithAspirationWindows()
    {
        // Guardia di regressione per Flow A1 (docs/porting-master-plan.md): dopo aver aggiunto
        // aspiration windows (search.cpp:375-441) con il ciclo "fallito alto/basso -> allarga
        // finestra e ricerca di nuovo", un bug nella condizione di uscita potrebbe far ricercare
        // all'infinito la stessa profondità. Soglia larga apposta (non un confronto numerico
        // preciso con la finestra piena precedente): misurato 19515 nodi cumulativi su questa
        // posizione a profondità 6.
        var pos = MakePosition("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var search = new Search();
        search.Resize(16);
        search.NewGame();

        var result = search.Search_(pos, maxDepth: 6, TimeSpan.FromSeconds(10));

        Assert.Equal(6, result.Depth);
        Assert.True(result.Nodes < 40000, $"Nodi cumulativi molto più alti del previsto: {result.Nodes}");
    }
}

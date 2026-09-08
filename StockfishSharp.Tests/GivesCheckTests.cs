using StockfishSharp.Engine;
using Xunit;

/// <summary>Verifica <see cref="Position.GivesCheck"/> nelle DUE direzioni.
///
/// PERCHE' SERVE, e perche' il perft non basta: <c>DoMove</c> non ricalcola gli scacchi, si FIDA
/// del flag che riceve (<c>_st.CheckersBB = givesCheck ? AttackersTo(reAvversario) &amp; Pieces(us)
/// : 0</c>, Position.cs:1085). Da cio' seguono due cose asimmetriche:
///
/// * un FALSO NEGATIVO (dice "no scacco" quando lo e') lascia <c>CheckersBB = 0</c> nel figlio, che
///   genera allora NON_EVASIONS invece di EVASIONS e conta mosse illegali: il perft se ne accorge,
///   ed e' quindi gia' coperto;
/// * un FALSO POSITIVO e' **invisibile al perft**, perche' il ramo "true" ricalcola comunque gli
///   attaccanti veri e ottiene 0, cioe' lo stesso risultato del ramo "false". Ma in RICERCA il flag
///   e' usato direttamente per decidere: niente potatura di futility sulle mosse che danno scacco
///   (Step 15), estensioni, e la generazione degli scacchi in quiescenza. Un falso positivo cambia
///   quindi l'albero senza rompere nessun conteggio.
///
/// L'oracolo indipendente e' la definizione: si esegue la mossa passando <c>givesCheck: true</c>,
/// cosi' <c>DoMove</c> calcola davvero <c>AttackersTo(reAvversario) &amp; Pieces(us)</c>, e si
/// guarda se l'insieme risultante e' vuoto. E' un percorso completamente diverso da quello di
/// <c>GivesCheck</c>, che invece ragiona per case di scacco precalcolate, blockers, e casi speciali
/// di promozione/en passant/arrocco.</summary>
public class GivesCheckTests
{
    public GivesCheckTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private int _scacchiDiretti, _scacchiScoperti, _scacchiDaPromozione, _scacchiDaEnPassant, _scacchiDaArrocco;

    /// <summary>Verita' indipendente: dopo la mossa, il re del giocatore di turno e' attaccato?</summary>
    private static bool DaScaccoDavvero(Position pos, Move m)
    {
        var st = new StateInfo();
        pos.DoMove(m, st, givesCheck: true); // forza DoMove a calcolare gli attaccanti veri
        bool scacco = pos.Checkers() != 0;
        pos.UndoMove(m);
        return scacco;
    }

    private void Verifica(Position pos, string ctx)
    {
        var mosse = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, mosse);

        foreach (var m in mosse)
        {
            bool detto = pos.GivesCheck(m);
            bool vero = DaScaccoDavvero(pos, m);

            Assert.True(detto == vero,
                $"{ctx}: GivesCheck({m.FromSq}{m.ToSq}{(m.TypeOf == MoveType.Promotion ? m.PromotionType.ToString() : "")}) "
                + $"dice {detto} ma la mossa {(vero ? "DA'" : "non da'")} scacco");

            if (!vero) continue;

            // Classificazione, solo per le guardie di copertura sotto.
            if (m.TypeOf == MoveType.Promotion) _scacchiDaPromozione++;
            else if (m.TypeOf == MoveType.EnPassant) _scacchiDaEnPassant++;
            else if (m.TypeOf == MoveType.Castling) _scacchiDaArrocco++;
            else if ((pos.CheckSquaresOf(Types.TypeOf(pos.PieceOn(m.FromSq))) & Bitboards.SquareBB(m.ToSq)) != 0)
                _scacchiDiretti++;
            else
                _scacchiScoperti++;
        }
    }

    private void Cammina(Position pos, int depth, string fen)
    {
        Verifica(pos, $"FEN={fen} (profondita' residua {depth})");
        if (depth == 0) return;

        var mosse = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, mosse);
        foreach (var m in mosse)
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            Cammina(pos, depth - 1, fen);
            pos.UndoMove(m);
        }
    }

    private static readonly string[] Fens =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        // Kiwipete: arrocco da entrambe le parti, molti scacchi diretti e scoperti
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        // promozioni con e senza cattura: scacco DA promozione (cavallo e donna)
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        // en passant: l'unico caso in cui la presa puo' SCOPRIRE uno scacco su due linee insieme
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
        // arrocco che da' scacco con la TORRE che arriva sulla colonna del re avversario
        "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1",
        // molti pezzi inchiodati/allineati: scacchi scoperti
        "2rqkb1r/ppp2p2/2npb1p1/1N1Nn2p/2P1PP2/8/PP2B1PP/R1BQK2R b KQ - 0 11",
        // SCACCO DA EN PASSANT, costruita apposta: exd6 e.p. toglie il pedone d5 E sposta il
        // pedone da e5, sgombrando la traversa 5 fra la torre a5 e il re nero h5. E' il caso in cui
        // spariscono DUE pezzi dalla stessa linea con una mossa sola, che il ramo en passant di
        // GivesCheck deve trattare a parte (nessun'altra mossa lo fa).
        "8/8/8/R2pP2k/8/8/8/K7 w - d6 0 1",
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void GivesCheckCoincideConLaDefinizione(int indice)
    {
        var pos = new Position();
        pos.Set(Fens[indice], isChess960: false);
        Cammina(pos, indice == 1 || indice == 2 ? 2 : 3, Fens[indice]);
    }

    /// <summary>Guardie di COPERTURA: senza, un test come questo passa anche quando un ramo
    /// difficile non viene mai percorso — errore gia' commesso il 2026-09-08 sul test dei tipi di
    /// generazione, dove una mutazione passava indenne perche' nessuna posizione arrivava mai sotto
    /// scacco.</summary>
    [Fact]
    public void TuttiITipiDiScaccoSonoDavveroPercorsi()
    {
        foreach (string fen in Fens)
        {
            var pos = new Position();
            pos.Set(fen, isChess960: false);
            Cammina(pos, 2, fen);
        }

        Assert.True(_scacchiDiretti > 0, "nessuno scacco diretto percorso");
        Assert.True(_scacchiScoperti > 0, "nessuno scacco SCOPERTO percorso (il ramo blockersForKing)");
        Assert.True(_scacchiDaPromozione > 0, "nessuno scacco da PROMOZIONE percorso");
        Assert.True(_scacchiDaEnPassant > 0, "nessuno scacco da EN PASSANT percorso (il ramo con due linee da ricontrollare)");
        Assert.True(_scacchiDaArrocco > 0, "nessuno scacco da ARROCCO percorso (il ramo con la torre che arriva su rto)");
    }
}

using StockfishSharp.Engine;
using Xunit;

/// <summary>Copre i tipi di generazione che il perft NON tocca.
///
/// PERCHE' SERVE: il perft, unica verifica di MoveGen finora, chiama sempre e solo
/// <c>GenType.Legal</c>. Ma il MovePicker — cioe' TUTTA la ricerca — non usa mai Legal: usa
/// <c>Captures</c>, <c>Quiets</c> ed <c>Evasions</c> separatamente (MovePicker.cs:316,356,397).
/// Quei tre percorsi erano quindi completamente scoperti: un errore li' non avrebbe fatto fallire
/// nessun test, e si sarebbe manifestato solo come "il motore gioca peggio", il sintomo piu'
/// difficile da diagnosticare che ci sia.
///
/// Le proprieta' verificate sono DICHIARATIVE, ricavate dalla semantica della fonte
/// (movegen.cpp:86-102 per <c>make_promotions</c>, 207-238 per la scelta del <c>target</c>), non
/// dalla nostra implementazione — quindi sono un oracolo indipendente, non una tautologia:
///
/// * <c>target</c> vale <c>pieces(~Us)</c> per CAPTURES ed <c>~pieces()</c> per QUIETS: due insiemi
///   disgiunti la cui unione e' <c>~pieces(Us)</c>, cioe' esattamente il target di NON_EVASIONS.
/// * Le promozioni sono l'unico punto in cui i due non si dividono per casa d'arrivo:
///   <c>make_promozioni</c> da' la promozione a DONNA a CAPTURES anche quando la spinta e' quieta, e
///   le sottopromozioni a QUIETS quando la spinta e' quieta, a CAPTURES quando e' una cattura.
///   La somma resta comunque esattamente le quattro promozioni di NON_EVASIONS.
/// * L'en passant sta in CAPTURES e NON_EVASIONS ma non in QUIETS; l'arrocco in QUIETS e
///   NON_EVASIONS ma non in CAPTURES.
///
/// Da cui: <b>CAPTURES e QUIETS sono disgiunti e la loro unione e' esattamente NON_EVASIONS</b>.
/// Una sola riga sbagliata in uno dei tre percorsi rompe questa uguaglianza.</summary>
public class GenTypeCoverageTests
{
    public GenTypeCoverageTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static List<Move> Gen(GenType t, Position pos)
    {
        var l = new List<Move>();
        MoveGen.Generate(t, pos, l);
        return l;
    }

    private static string Descrivi(IEnumerable<Move> ms) =>
        string.Join(" ", ms.Select(m => $"{m.FromSq}{m.ToSq}{(m.TypeOf == MoveType.Promotion ? m.PromotionType.ToString() : "")}")
                           .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>La proprieta' centrale, su ogni posizione raggiungibile a profondita' 3 dalle FEN
    /// date: cammino esaustivo, non campionamento.</summary>
    // Guardie di COPERTURA: un test che non raggiunge mai il ramo difficile passa anche quando quel
    // ramo e' rotto. Verificato con una mutazione (EVASIONS che genera solo mosse di re): senza
    // queste guardie il test passava lo stesso, perche' nessuna delle posizioni di partenza
    // raggiungeva un nodo sotto scacco.
    private int _nodiSottoScacco, _nodiConPromozioni, _nodiConEnPassant, _nodiConArrocco, _nodiDoppioScacco;

    private void Verifica(Position pos, string ctx)
    {
        var legal = Gen(GenType.Legal, pos);
        if (pos.Checkers() != 0) _nodiSottoScacco++;
        if (Bitboards.MoreThanOne(pos.Checkers())) _nodiDoppioScacco++;
        if (legal.Any(m => m.TypeOf == MoveType.Promotion)) _nodiConPromozioni++;
        if (legal.Any(m => m.TypeOf == MoveType.EnPassant)) _nodiConEnPassant++;
        if (legal.Any(m => m.TypeOf == MoveType.Castling)) _nodiConArrocco++;

        if (pos.Checkers() == 0)
        {
            var caps = Gen(GenType.Captures, pos);
            var quiets = Gen(GenType.Quiets, pos);
            var non = Gen(GenType.NonEvasions, pos);

            // Disgiunti: nessuna mossa puo' essere sia cattura sia quieta.
            var comuni = caps.Intersect(quiets).ToList();
            Assert.True(comuni.Count == 0,
                $"{ctx}: CAPTURES e QUIETS hanno {comuni.Count} mosse in comune ({Descrivi(comuni)})");

            // Unione esatta, come MULTIINSIEME (un duplicato da una parte sola e' un errore vero).
            Assert.True(caps.Count + quiets.Count == non.Count,
                $"{ctx}: |CAPTURES|+|QUIETS| = {caps.Count}+{quiets.Count} = {caps.Count + quiets.Count} != |NON_EVASIONS| = {non.Count}");
            Assert.True(Descrivi(caps.Concat(quiets)) == Descrivi(non),
                $"{ctx}: CAPTURES+QUIETS != NON_EVASIONS\n  unione: {Descrivi(caps.Concat(quiets))}\n  attese: {Descrivi(non)}");

            // Ogni mossa quieta arriva su una casa VUOTA, perche' il suo target e' ~pieces().
            // Unica eccezione l'ARROCCO, che nella rappresentazione interna (come nella fonte) e'
            // "il re cattura la propria torre": la sua casa d'arrivo e' quella della torre, quindi
            // occupata per definizione. Non e' un'eccezione alla regola del target — l'arrocco e'
            // aggiunto a parte, dopo (movegen.cpp:235-238), non passa da target.
            foreach (var m in quiets)
                Assert.True(m.TypeOf == MoveType.Castling || pos.PieceOn(m.ToSq) == Piece.None,
                    $"{ctx}: mossa quieta {m.FromSq}{m.ToSq} arriva su una casa occupata");

            // Le mosse pseudo-legali filtrate per legalita' devono dare esattamente le legali.
            Assert.True(Descrivi(non.Where(pos.Legal)) == Descrivi(legal),
                $"{ctx}: NON_EVASIONS filtrate per legalita' != LEGAL");
        }
        else
        {
            var evasions = Gen(GenType.Evasions, pos);

            // ATTENZIONE, errore gia' commesso una volta qui: confrontare EVASIONS con
            // GenType.Legal e' una TAUTOLOGIA, perche' sotto scacco Legal e' COSTRUITO su Evasions
            // (MoveGen.cs:52). Una mutazione che toglieva a EVASIONS tutte le mosse non-di-re
            // passava indenne. Serve un oracolo davvero indipendente.
            //
            // E non puo' essere "NON_EVASIONS filtrate con pos.Legal": Legal e' fedele alla fonte e
            // NON verifica che la mossa risolva lo scacco (per un pezzo non inchiodato e non re
            // risponde sempre true, dando per scontata la generazione EVASIONS a monte).
            //
            // L'oracolo indipendente e' la DEFINIZIONE di legalita': esegui la mossa, guarda se il
            // tuo re resta attaccato, disfala. Usa solo generazione di attacchi e DoMove/UndoMove,
            // gia' validati per conto loro dal perft.
            var attese = Gen(GenType.NonEvasions, pos)
                // L'arrocco va escluso: NON_EVASIONS lo genera comunque, ma sotto scacco non e' mai
                // legale (il re parte da una casa attaccata) e la forza bruta qui sotto, che guarda
                // solo la casa d'arrivo, non se ne accorgerebbe. Nella fonte il caso non si pone:
                // sotto scacco si passa sempre da EVASIONS, che l'arrocco non lo genera.
                .Where(m => m.TypeOf != MoveType.Castling)
                .Where(m => LegaleAForzaBruta(pos, m))
                .ToList();
            var ottenute = evasions.Where(m => LegaleAForzaBruta(pos, m)).ToList();

            Assert.True(Descrivi(ottenute) == Descrivi(attese),
                $"{ctx}: le EVASIONI legali non coincidono con le mosse legali calcolate a forza bruta"
                + $"\n  da EVASIONS:   {Descrivi(ottenute)}"
                + $"\n  a forza bruta: {Descrivi(attese)}");
        }
    }

    /// <summary>La definizione di mossa legale, senza passare da <see cref="Position.Legal"/>:
    /// esegui, guarda se il re del giocatore di turno resta attaccato, disfa.</summary>
    private static bool LegaleAForzaBruta(Position pos, Move m)
    {
        Color us = pos.SideToMove;
        var st = new StateInfo();
        pos.DoMove(m, st);
        ulong re = pos.Pieces(us, PieceType.King);
        bool ok = re != 0 && (pos.AttackersTo(Bitboards.Lsb(re)) & pos.Pieces(Types.Opposite(us))) == 0;
        pos.UndoMove(m);
        return ok;
    }

    private void Cammina(Position pos, int depth, string fen)
    {
        Verifica(pos, $"FEN={fen} (profondita' residua {depth})");
        if (depth == 0) return;

        foreach (var m in Gen(GenType.Legal, pos))
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            Cammina(pos, depth - 1, fen);
            pos.UndoMove(m);
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    // Kiwipete: arrocco da entrambe le parti, molte catture
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)]
    // promozioni con e senza cattura: il caso in cui CAPTURES e QUIETS si dividono le 4 promozioni
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 2)]
    // presa en passant disponibile (sta in CAPTURES, non in QUIETS)
    [InlineData("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3", 3)]
    // posizione con scacchi frequenti: esercita EVASIONS, incluso il doppio scacco
    [InlineData("2rqkb1r/ppp2p2/2npb1p1/1N1Nn2p/2P1PP2/8/PP2B1PP/R1BQK2R b KQ - 0 11", 3)]
    // GIA' sotto scacco alla radice: e' l'unico modo di garantire che il ramo EVASIONS venga
    // percorso davvero (le posizioni sopra, camminate in profondita', non ci arrivavano mai)
    [InlineData("rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3", 3)]
    // DOPPIO SCACCO (cavallo f6 + torre e1): il ramo in cui si generano SOLO mosse di re
    [InlineData("4k3/8/5N2/8/8/8/8/4R1K1 b - - 0 1", 2)]
    public void CapturesEQuietsPartizionanoNonEvasions(string fen, int depth)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        Cammina(pos, depth, fen);
    }

    /// <summary>Le guardie di copertura vanno verificate una volta su TUTTE le posizioni insieme:
    /// singolarmente nessuna le soddisfa tutte, ed e' giusto cosi'.</summary>
    [Fact]
    public void TuttiIRamiDifficiliSonoDavveroPercorsi()
    {
        string[] fens =
        [
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
            "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
            "2rqkb1r/ppp2p2/2npb1p1/1N1Nn2p/2P1PP2/8/PP2B1PP/R1BQK2R b KQ - 0 11",
            "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3",
            "4k3/8/5N2/8/8/8/8/4R1K1 b - - 0 1",
        ];

        foreach (string fen in fens)
        {
            var pos = new Position();
            pos.Set(fen, isChess960: false);
            Cammina(pos, 2, fen);
        }

        Assert.True(_nodiSottoScacco > 0, "nessun nodo sotto scacco: il ramo EVASIONS non e' stato percorso");
        Assert.True(_nodiDoppioScacco > 0, "nessun doppio scacco: il ramo 'solo mosse di re' non e' stato percorso");
        Assert.True(_nodiConPromozioni > 0, "nessuna promozione: il caso in cui CAPTURES e QUIETS si dividono le 4 promozioni non e' stato percorso");
        Assert.True(_nodiConEnPassant > 0, "nessun en passant: il caso 'sta in CAPTURES ma non in QUIETS' non e' stato percorso");
        Assert.True(_nodiConArrocco > 0, "nessun arrocco: il caso 'sta in QUIETS ma non in CAPTURES' non e' stato percorso");
    }
}

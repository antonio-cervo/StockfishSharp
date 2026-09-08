using System.Collections.Generic;
using System.Linq;
using StockfishSharp.Engine;
using Xunit;

/// <summary>Guardiano sull'ORDINE di <c>GenType.Legal</c>, non solo sul suo insieme.
///
/// PERCHE' SERVE, e perche' non e' pedanteria. Il 2026-09-08 il filtro di legalita' di
/// <c>GenerateLegal</c> scorreva la lista all'INDIETRO mentre la fonte (movegen.cpp:281-287) la
/// scorre in AVANTI. La rimozione e' O(1) e sostituisce l'elemento illegale con l'ULTIMO della
/// lista, quindi la direzione del ciclo cambia QUALE mossa finisce in quale posizione: stesso
/// insieme, ordine diverso. Nessun test se ne accorgeva — il perft confronta i CONTEGGI e
/// GenTypeCoverageTests confronta gli INSIEMI.
///
/// E l'ordine conta davvero, in due punti:
/// * <c>rootMoves</c> e' costruita in quest'ordine (thread.cpp:309-321), quindi <c>rootMoves[0]</c>
///   e' la prima mossa legale generata e alla prima iterazione — TT vuota — diventa il ttMove
///   della radice (search.cpp:820), che ordina l'intera ricerca;
/// * <c>PartialInsertionSort</c> di MovePicker e' stabile: a pari punteggio decide l'ordine di
///   generazione.
///
/// L'ORACOLO di questo test non e' la nostra implementazione: e' la trascrizione DIRETTA
/// dell'algoritmo della fonte applicata alle mosse pseudo-legali, scritta qui sotto in modo
/// deliberatamente ingenuo (nessuna rimozione furba, si ricostruisce cio' che la fonte otterrebbe
/// passo passo). Se qualcuno "ottimizzasse" di nuovo il ciclo cambiandone la direzione, o passasse
/// a una rimozione che preserva l'ordine (piu' naturale in C#, e sbagliata), questo test fallisce.
///
/// Le FEN scelte hanno tutte almeno una mossa pseudo-legale ILLEGALE — pezzo inchiodato, mossa di
/// re verso una casa attaccata, en passant che scopre scacco — perche' senza rimozioni ogni
/// implementazione da' lo stesso ordine e il test non proverebbe nulla. La prima e' proprio la
/// posizione su cui il difetto e' stato trovato.
/// </summary>
public class LegalMoveOrderTests
{
    public LegalMoveOrderTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Position Da(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    public static TheoryData<string> Posizioni =>
    [
        // Pedone f4 inchiodato dalla torre in d4 + mossa di re verso g3/h3 attaccate: e' la
        // posizione dove la divergenza e' stata scoperta (la fonte apre con h4g5, noi aprivamo
        // con h4g4).
        "8/2p5/3p4/KP5r/3R1p1k/8/4P1P1/8 b - - 1 11",
        // Kiwipete: molte mosse, alfiere e cavallo inchiodati, arrocchi.
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        // En passant che scoprirebbe scacco sulla traversa (il caso raro del terzo ramo del filtro).
        "8/8/8/8/k1p4R/8/3P4/3K4 w - - 0 1",
        "8/8/8/2k5/2pP4/8/B7/4K3 b - d3 0 3",
        // Re sotto scacco: il filtro si applica al ramo EVASIONS, con un target diverso.
        "rnb1kbnr/pp1ppppp/2p5/q7/8/3P4/PPP1PPPP/RNBQKBNR w KQkq - 2 3",
        // Doppio inchiodamento e re in un angolo affollato.
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
    ];

    [Theory]
    [MemberData(nameof(Posizioni))]
    public void OrdineDiLegalCoincideConLAlgoritmoDellaFonte(string fen)
    {
        var pos = Da(fen);

        var nostre = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, nostre);

        var attese = FiltroDellaFonte(pos);

        Assert.Equal(
            attese.Select(Descrivi).ToArray(),
            nostre.Select(Descrivi).ToArray());
    }

    /// <summary>La posizione su cui il difetto e' stato trovato, con l'ordine ATTESO scritto a mano
    /// (verificato contro "go perft 1" dell'oracolo compilato). E' il caso di regressione esplicito:
    /// vale anche se un giorno <see cref="FiltroDellaFonte"/> venisse toccato per sbaglio.</summary>
    [Fact]
    public void OrdineAttesoLetteraleSullaPosizioneDelDifetto()
    {
        var pos = Da("8/2p5/3p4/KP5r/3R1p1k/8/4P1P1/8 b - - 1 11");
        var nostre = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, nostre);

        Assert.Equal(
            ["h4g5", "d6d5", "c7c6", "c7c5", "h5b5", "h5c5", "h5d5", "h5e5",
             "h5f5", "h5g5", "h5h6", "h5h7", "h5h8", "h4g3", "h4g4"],
            nostre.Select(Descrivi).ToArray());
    }

    /// <summary><c>generate&lt;LEGAL&gt;</c>, movegen.cpp:271-289, trascritto passo passo: ciclo IN
    /// AVANTI, e su una mossa illegale si sostituisce con l'ULTIMA e si RIESAMINA la stessa
    /// posizione (non si avanza).</summary>
    private static List<Move> FiltroDellaFonte(Position pos)
    {
        Color us = pos.SideToMove;
        ulong pinned = pos.BlockersForKing(us) & pos.Pieces(us);
        Square ksq = pos.SquareOf(PieceType.King, us);

        var lista = new List<Move>();
        MoveGen.Generate(pos.Checkers() != 0 ? GenType.Evasions : GenType.NonEvasions, pos, lista);

        int cur = 0;
        int end = lista.Count;
        while (cur != end)
        {
            Move m = lista[cur];
            bool daVerificare = (pinned & Bitboards.SquareBB(m.FromSq)) != 0
                                || m.FromSq == ksq
                                || m.TypeOf == MoveType.EnPassant;

            if (daVerificare && !pos.Legal(m))
                lista[cur] = lista[--end];
            else
                ++cur;
        }

        lista.RemoveRange(end, lista.Count - end);
        return lista;
    }

    private static string Descrivi(Move m)
    {
        string s = m.FromSq.ToString().ToLowerInvariant() + m.ToSq.ToString().ToLowerInvariant();
        return m.TypeOf == MoveType.Promotion
            ? s + char.ToLowerInvariant(m.PromotionType.ToString()[0])
            : s;
    }
}

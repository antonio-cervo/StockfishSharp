using StockfishSharp.Engine;
using Xunit;

/// <summary>Verifica che le chiavi Zobrist mantenute INCREMENTALMENTE da DoMove/UndoMove coincidano
/// sempre con quelle ricalcolate da zero sulla stessa posizione.
///
/// PERCHE' SERVE: il perft, che e' la nostra verifica principale di Position/MoveGen, valida la
/// scacchiera e la generazione delle mosse ma **non tocca nessuna di queste chiavi** — conta solo
/// nodi. Una deriva resterebbe quindi del tutto invisibile ai test esistenti, e non e' innocua:
///   - <c>Key</c> indicizza la transposition table (una deriva = entry lette per la posizione
///     sbagliata, cioe' valutazioni e mosse di TT arbitrarie);
///   - <c>PawnKey</c> indicizza la pawn history e la pawn correction history;
///   - <c>MinorPieceKey</c> e <c>NonPawnKey</c> indicizzano le altre tre correction history, che
///     CORREGGONO la valutazione statica di ogni nodo (<c>to_corrected_static_eval</c>);
///   - <c>MaterialKey</c> serve al riconoscimento del materiale.
/// Una deriva su una qualunque di queste produce un motore che sbaglia in modo diffuso e silenzioso.
///
/// Il confronto e' contro <c>Position.Set(fen)</c>, che ricalcola tutto da zero: e' un oracolo
/// indipendente dal percorso incrementale, lo stesso principio gia' usato per l'accumulatore NNUE.
/// Si verifica anche il ripristino dopo <c>UndoMove</c>, che e' l'altra meta' del percorso.</summary>
public class IncrementalKeysTests
{
    public IncrementalKeysTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static void AssertKeysMatch(Position pos, string ctx)
    {
        var scratch = new Position();
        scratch.Set(pos.Fen(), pos.IsChess960);

        Assert.True(pos.Key == scratch.Key, $"{ctx}: Key incrementale {pos.Key:X16} != da zero {scratch.Key:X16}");
        Assert.True(pos.PawnKey == scratch.PawnKey, $"{ctx}: PawnKey {pos.PawnKey:X16} != {scratch.PawnKey:X16}");
        Assert.True(pos.MinorPieceKey == scratch.MinorPieceKey, $"{ctx}: MinorPieceKey diverge");
        Assert.True(pos.NonPawnKey(Color.White) == scratch.NonPawnKey(Color.White), $"{ctx}: NonPawnKey(bianco) diverge");
        Assert.True(pos.NonPawnKey(Color.Black) == scratch.NonPawnKey(Color.Black), $"{ctx}: NonPawnKey(nero) diverge");
        Assert.True(pos.MaterialKey == scratch.MaterialKey, $"{ctx}: MaterialKey diverge");
        Assert.True(pos.MaterialKeyIsOk(), $"{ctx}: MaterialKeyIsOk() falso");
    }

    /// <summary>Ricorsione su TUTTE le mosse legali fino a una profondita' data: copre arrocco,
    /// presa en passant, promozioni e catture in modo esaustivo invece che casuale.</summary>
    private static void Walk(Position pos, int depth, string fen)
    {
        if (depth == 0) return;
        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        foreach (var m in moves)
        {
            ulong keyPrima = pos.Key;
            var st = new StateInfo();
            pos.DoMove(m, st);
            AssertKeysMatch(pos, $"FEN={fen} dopo {m.FromSq}{m.ToSq} (profondita' residua {depth})");
            Walk(pos, depth - 1, fen);
            pos.UndoMove(m);

            Assert.True(pos.Key == keyPrima,
                $"FEN={fen}: dopo UndoMove di {m.FromSq}{m.ToSq} la Key non e' tornata al valore precedente");
            AssertKeysMatch(pos, $"FEN={fen} dopo UndoMove di {m.FromSq}{m.ToSq}");
        }
    }

    [Theory]
    // posizione iniziale
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    // Kiwipete: arrocco da entrambe le parti, molte catture
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)]
    // promozioni (anche con cattura) e arrocco
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 2)]
    // presa en passant disponibile
    [InlineData("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3", 3)]
    // finale di soli pedoni: molte spinte doppie, quindi molte case di en passant
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11", 3)]
    public void ChiaviIncrementaliCoincidonoColRicalcoloDaZero(string fen, int depth)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        AssertKeysMatch(pos, $"FEN={fen} (radice)");
        Walk(pos, depth, fen);
    }

    /// <summary>Il null move e' l'altro percorso che tocca le chiavi (cambia il tratto e azzera la
    /// casa di en passant) e non e' coperto dal perft, che non lo genera mai.</summary>
    [Theory]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3")]
    public void NullMoveMantieneLeChiaviCoerenti(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        ulong keyPrima = pos.Key;

        var st = new StateInfo();
        pos.DoNullMove(st);
        AssertKeysMatch(pos, $"FEN={fen} dopo null move");
        pos.UndoNullMove();

        Assert.True(pos.Key == keyPrima, $"FEN={fen}: la Key non e' tornata al valore precedente dopo UndoNullMove");
        AssertKeysMatch(pos, $"FEN={fen} dopo UndoNullMove");
    }
}

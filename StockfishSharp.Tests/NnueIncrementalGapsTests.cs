using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica dell'aggiornamento incrementale dell'accumulatore NNUE nel caso che la
/// ricerca REALE produce ma che <see cref="NnueIncrementalTests"/> non copriva: <b>catene con
/// buchi</b>.
///
/// Quel test chiama <c>Evaluate</c> a ogni nodo, quindi l'accumulatore e' sempre gia' aggiornato e
/// il recupero deve risalire al massimo di un frame. Nella ricerca vera non e' cosi': un nodo sotto
/// scacco non valuta affatto, e un taglio da transposition table esce prima dello Step 5 — quindi
/// si accumulano piu' ply senza valutazione e il successivo <c>Evaluate</c> deve risalire indietro
/// fino all'ultimo accumulatore utilizzabile e riapplicare in avanti TUTTI i delta intermedi
/// (<c>FindLastUsableAccumulator</c> + <c>ForwardUpdateIncremental</c>). E' esattamente il percorso
/// dove un errore resterebbe silenzioso: nessun crash, solo valutazioni sbagliate.
///
/// Qui si giocano partite casuali lunghe, valutando solo ogni tanto (con buchi di lunghezza
/// variabile), e a ogni valutazione si confronta bit-per-bit contro il ricalcolo da zero.</summary>
public class NnueIncrementalGapsTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public NnueIncrementalGapsTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static void AssertMatches(NnueAccumulator incremental, NnueAccumulator scratch, string ctx)
    {
        foreach (var c in new[] { Color.White, Color.Black })
        {
            int p = (byte)c;
            Assert.True(incremental.Accumulation[p].AsSpan().SequenceEqual(scratch.Accumulation[p]),
                $"{ctx}: accumulation[{c}] diverge dal ricalcolo da zero.");
            Assert.True(incremental.PsqtAccumulation[p].AsSpan().SequenceEqual(scratch.PsqtAccumulation[p]),
                $"{ctx}: psqtAccumulation[{c}] diverge dal ricalcolo da zero.");
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11")]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")]
    public void IncrementalMatchesScratchWithGapsInEvaluation(string fen)
    {
        var net = NnueNetwork.Load(NetworkPath);
        var moves = new List<Move>();

        // Semi fissi: il test deve essere riproducibile, non "a volte" verde.
        foreach (int seed in new[] { 1, 7, 12345, 987654 })
        {
            var rng = new Random(seed);
            int evaluated = 0, maxGap = 0, kingMoves = 0;

            // Alcune posizioni di partenza finiscono presto (matto/stallo/patta): si rigioca
            // finche' non si e' coperto abbastanza, invece di accontentarsi di una partita corta.
            for (int game = 0; game < 40 && evaluated < 40; game++)
            {
                var pos = new Position();
                pos.Set(fen, isChess960: false);

                var stack = new AccumulatorStack();
                stack.Reset();
                stack.Evaluate(pos, net);

                int gap = 0;
                for (int ply = 0; ply < 140; ply++)
                {
                    moves.Clear();
                    MoveGen.Generate(GenType.Legal, pos, moves);
                    if (moves.Count == 0) break;

                    var m = moves[rng.Next(moves.Count)];
                    if (Types.TypeOf(pos.MovedPiece(m)) == PieceType.King) kingMoves++;

                    var frame = stack.Push();
                    var st = new StateInfo();
                    pos.DoMove(m, st, pos.GivesCheck(m), frame.DirtyThreats, frame.DirtyPiece, frame.DirtyPawnPairs);

                    // Il punto del test: si valuta solo UNA VOLTA SU QUATTRO, quindi la catena
                    // accumula buchi di lunghezza variabile prima di ogni recupero.
                    gap++;
                    if (rng.Next(4) == 0)
                    {
                        stack.Evaluate(pos, net);
                        AssertMatches(stack.Latest, NnueAccumulator.ComputeFromScratch(net, pos),
                            $"FEN={fen} seme={seed} partita={game} ply={ply} buco={gap}");
                        evaluated++;
                        maxGap = Math.Max(maxGap, gap);
                        gap = 0;
                    }
                }

                // Valutazione finale: chiude anche l'ultimo buco, spesso il piu' lungo perche'
                // nessuna valutazione casuale lo ha interrotto.
                if (gap > 0)
                {
                    stack.Evaluate(pos, net);
                    AssertMatches(stack.Latest, NnueAccumulator.ComputeFromScratch(net, pos),
                        $"FEN={fen} seme={seed} partita={game} finale, buco={gap}");
                    evaluated++;
                    maxGap = Math.Max(maxGap, gap);
                }
            }

            Assert.True(evaluated >= 40, $"seme={seed}: troppe poche valutazioni ({evaluated}), il test non starebbe verificando granche'.");
            Assert.True(maxGap >= 3, $"seme={seed}: buco massimo {maxGap}, troppo corto per esercitare il recupero su piu' frame.");
            Assert.True(kingMoves >= 1, $"seme={seed}: nessuna mossa di re, il refresh non e' stato esercitato.");
        }
    }
}

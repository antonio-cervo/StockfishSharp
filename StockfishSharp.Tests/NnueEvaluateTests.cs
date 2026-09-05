using System.Globalization;
using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

public class NnueEvaluateTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public NnueEvaluateTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static string FormatSigned(int cp)
    {
        double pawns = 0.01 * cp;
        return pawns.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);
    }

    [Theory]
    // FEN, "Final evaluation" atteso dall'oracolo (comando "eval", stessa rete), lato bianco.
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "+0.00")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", "-2.48")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", "+0.48")]
    public void FinalEvaluationMatchesOracle(string fen, string expected)
    {
        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set(fen, isChess960: false);

        int v = NnueEvaluate.Evaluate(net, pos, optimism: 0);
        int whiteSideValue = pos.SideToMove == Color.White ? v : -v;
        int cp = WinRateModel.ToCentipawns(whiteSideValue, pos);

        Assert.Equal(expected, FormatSigned(cp));
    }
}

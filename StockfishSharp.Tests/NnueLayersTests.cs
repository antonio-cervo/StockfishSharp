using System.Globalization;
using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

public class NnueLayersTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public NnueLayersTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static string FormatCpAlignedDot(int value, Position pos)
    {
        double pawns = Math.Abs(0.01 * WinRateModel.ToCentipawns(value, pos));
        char sign = value < 0 ? '-' : value > 0 ? '+' : ' ';
        return $"{sign}{pawns.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    [Fact]
    public void PositionalMatchesOracleForKiwipeteLikePosition()
    {
        // Colonna "Positional (Layers)" letta dalla stessa tabella dell'oracolo usata in
        // NnueAccumulatorTests (stessa FEN, stessa rete nn-1a298aa575a0.nnue).
        string[] expected =
        [
            "-0.70", "-1.22", "-0.98", "-1.28", "-1.37", "-1.39", "-1.36", "-1.49",
        ];

        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", isChess960: false);

        var acc = NnueAccumulator.ComputeFromScratch(net, pos);
        byte[] transformed = NnueLayers.TransformBothPerspectives(acc, pos.SideToMove);

        for (int bucket = 0; bucket < NnueArchitecture.LayerStacks; bucket++)
        {
            int raw = NnueLayers.Propagate(net.LayerStacks[bucket], transformed);
            int value = raw / NnueCommon.OutputScale;
            string formatted = FormatCpAlignedDot(value, pos);
            Assert.Equal(expected[bucket], formatted);
        }
    }
}

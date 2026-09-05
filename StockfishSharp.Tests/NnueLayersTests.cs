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

    [Fact]
    public void Avx512TransformPerspectiveMatchesScalarBitExactOnRandomAccumulations()
    {
        Assert.True(NnueLayers.UsingAvx512, "Attesa CPU con AVX512BW+F su questa macchina di sviluppo/CI.");

        var rng = new Random(11111);
        for (int trial = 0; trial < 50; trial++)
        {
            var accumulation = new short[NnueArchitecture.L1];
            for (int j = 0; j < accumulation.Length; j++)
                accumulation[j] = (short)rng.Next(short.MinValue, short.MaxValue + 1);

            byte[] scalar = NnueLayers.TransformPerspectiveScalar(accumulation);
            byte[] avx512 = NnueLayers.TransformPerspectiveAvx512(accumulation);

            Assert.Equal(scalar, avx512);
        }
    }

    [Theory]
    [InlineData(NnueArchitecture.L1, 32)]
    [InlineData(64, 32)]
    [InlineData(128, 1)]
    public void Avx512AffineTransformMatchesScalarBitExactOnRandomInputs(int inputDim, int outputDim)
    {
        Assert.True(NnueLayers.UsingAvx512, "Attesa CPU con AVX512BW+F su questa macchina di sviluppo/CI.");

        var rng = new Random(22222 ^ (inputDim << 8) ^ outputDim);
        for (int trial = 0; trial < 20; trial++)
        {
            var input = new byte[inputDim];
            for (int i = 0; i < input.Length; i++) input[i] = (byte)rng.Next(0, 128);

            var weights = new sbyte[outputDim * inputDim];
            for (int i = 0; i < weights.Length; i++) weights[i] = (sbyte)rng.Next(sbyte.MinValue, sbyte.MaxValue + 1);

            var biases = new int[outputDim];
            for (int i = 0; i < biases.Length; i++) biases[i] = rng.Next(-1000, 1000);

            int[] scalar = NnueLayers.AffineTransformScalar(input, weights, biases, inputDim, outputDim);
            int[] avx512 = NnueLayers.AffineTransformAvx512(input, weights, biases, inputDim, outputDim);

            Assert.Equal(scalar, avx512);
        }
    }
}

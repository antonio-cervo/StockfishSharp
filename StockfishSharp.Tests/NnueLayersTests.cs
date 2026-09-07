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

    /// <summary>Il percorso a input SPARSO di fc_0 (<c>AffineTransformSparseInput</c>, quello che
    /// la fonte usa davvero su hardware SIMD) deve dare lo stesso identico risultato del percorso
    /// scalare, che elabora tutti gli input senza saltare gli zeri. Verifica insieme le due parti
    /// delicate: la permutazione dei pesi al caricamento
    /// (<c>NnueLayerStack.BuildFc0ScrambledWeights</c>) e il prodotto scalare u8xi8
    /// <c>maddubs</c>+<c>madd</c>. L'input è generato con la stessa sparsità dei dati veri (~77%
    /// di zeri, misurata su posizioni reali) più due casi limite: tutto zero e nessuno zero.</summary>
    [Fact]
    public void Fc0SparseMatchesScalarBitExactOnRandomInputs()
    {
        Assert.True(NnueLayers.UsingAvx512, "Attesa CPU con AVX512BW+F su questa macchina di sviluppo/CI.");

        const int inputDim = NnueArchitecture.L1;
        const int outputDim = NnueArchitecture.L2;
        var rng = new Random(33333);

        for (int trial = 0; trial < 30; trial++)
        {
            var input = new byte[inputDim];
            // trial 0: tutto zero; trial 1: nessuno zero; gli altri: ~77% di zeri come nei dati veri.
            for (int i = 0; i < input.Length; i++)
                input[i] = trial switch
                {
                    0 => (byte)0,
                    1 => (byte)rng.Next(1, 128),
                    _ => rng.Next(100) < 77 ? (byte)0 : (byte)rng.Next(1, 128),
                };

            var weights = new sbyte[outputDim * inputDim];
            for (int i = 0; i < weights.Length; i++) weights[i] = (sbyte)rng.Next(sbyte.MinValue, sbyte.MaxValue + 1);

            var biases = new int[outputDim];
            for (int i = 0; i < biases.Length; i++) biases[i] = rng.Next(-1000, 1000);

            // Stessa permutazione applicata al caricamento della rete (NnueNetwork.cs).
            var scrambled = new sbyte[outputDim * inputDim];
            for (int j = 0; j < outputDim; j++)
                for (int i = 0; i < inputDim; i++)
                    scrambled[(i / 4 * (outputDim * 4)) + (j * 4) + (i % 4)] = weights[(j * inputDim) + i];

            int[] scalar = NnueLayers.AffineTransformScalar(input, weights, biases, inputDim, outputDim);

            var sparse = new int[outputDim];
            NnueLayers.AffineTransformFc0SparseAvx512(input, scrambled, biases, sparse);

            Assert.Equal(scalar, sparse);
        }
    }
}

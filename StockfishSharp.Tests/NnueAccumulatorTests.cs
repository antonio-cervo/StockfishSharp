using System.Globalization;
using StockfishSharp.Engine;
using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

public class NnueAccumulatorTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    public NnueAccumulatorTests()
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
    public void MaterialPsqtMatchesOracleForKiwipeteLikePosition()
    {
        // Valori "Material (PSQT)" letti dalla tabella stampata dall'eseguibile ufficiale
        // Stockfish 19 (comando "eval") sulla stessa FEN, con la stessa rete nn-1a298aa575a0.nnue.
        string[] expected =
        [
            "+1.71", "-0.06", "-0.23", "-0.42", "-0.46", "-0.48", "-0.53", "-0.49",
        ];

        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", isChess960: false);

        var acc = NnueAccumulator.ComputeFromScratch(net, pos);

        for (int bucket = 0; bucket < NnueArchitecture.LayerStacks; bucket++)
        {
            int value = acc.MaterialPsqt(pos.SideToMove, bucket);
            string formatted = FormatCpAlignedDot(value, pos);
            Assert.Equal(expected[bucket], formatted);
        }
    }

    [Fact]
    public void MaterialPsqtIsZeroForSymmetricStartingPosition()
    {
        var net = NnueNetwork.Load(NetworkPath);
        var pos = new Position();
        pos.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);

        var acc = NnueAccumulator.ComputeFromScratch(net, pos);

        for (int bucket = 0; bucket < NnueArchitecture.LayerStacks; bucket++)
        {
            Assert.Equal(0, acc.MaterialPsqt(pos.SideToMove, bucket));
        }
    }
}

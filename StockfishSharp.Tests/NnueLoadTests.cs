using StockfishSharp.Engine.Nnue;
using Xunit;

namespace StockfishSharp.Tests;

public class NnueLoadTests
{
    private const string NetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";

    [Fact]
    public void LoadsToExactEndOfFile()
    {
        var net = NnueNetwork.Load(NetworkPath);
        Assert.NotNull(net);
        Assert.Equal(NnueArchitecture.LayerStacks, net.LayerStacks.Length);
    }
}

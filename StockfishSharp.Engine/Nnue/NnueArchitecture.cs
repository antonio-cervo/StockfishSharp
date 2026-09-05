// Corrisponde a src/nnue/nnue_architecture.h della fonte upstream (dimensioni e struttura della
// rete Stockfish 19: tre insiemi di feature combinati). Vedi ../Types.cs per la nota generale sul
// porting.

namespace StockfishSharp.Engine.Nnue;

public static class NnueArchitecture
{
    public const int L1 = 1024; // dimensioni per prospettiva dell'accumulatore (HalfDimensions)
    public const int L2 = 32;
    public const int L3 = 32;

    public const int PsqtBuckets = 8;
    public const int LayerStacks = 8;

    // Dimensioni dei tre insiemi di feature — nnue/features/*.h.
    public const int PsqDimensions = 22528; // HalfKAv2_hm: 64 case * (11*64) / 2
    public const int ThreatDimensions = 59808; // FullThreats
    public const int PairDimensions = 4560; // PP_3Wide: 96*95/2
    public const int ThreatAndPpDimensions = ThreatDimensions + PairDimensions; // 64368

    public const int HalfDimensions = L1;
}

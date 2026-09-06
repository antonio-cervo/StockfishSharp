// src/types.h:347-350 — "used by NNUE" per la feature Pp3Wide (coppie di pedoni): la bitboard dei
// pedoni di ciascun colore prima e dopo la mossa, da cui si ricavano le coppie di pedoni adiacenti
// cambiate senza doverle enumerare tutte da zero.

namespace StockfishSharp.Engine;

public sealed class DirtyPawnPairs
{
    public readonly ulong[] Before = new ulong[Colors.Nb];
    public readonly ulong[] After = new ulong[Colors.Nb];
}

// Porzione di src/types.h:308-345 — "Keep track of what threats change on the board (used by
// NNUE)". La fonte impacchetta i 4 campi + il flag "add" in 32 bit (u32 data, con offset di bit
// dedicati) per poterli manipolare con le istruzioni SIMD AVX-512 di write_multiple_dirties
// (position.cpp, dietro #ifdef USE_AVX512ICL, non portate — stesso trattamento di ogni altro
// codice SIMD-specifico in questo porting). Qui, senza quel vincolo, una struct con campi diretti
// è più leggibile e altrettanto fedele nel CONTENUTO (stessi 5 valori, stesso significato).
//
// DirtyThreatList della fonte (ValueList<DirtyThreat,96>, dimensionata per il caso peggiore di una
// singola mossa non-castling) diventa qui semplicemente List<DirtyThreat>.

namespace StockfishSharp.Engine;

public readonly struct DirtyThreat(Piece pc, Piece threatenedPc, Square pcSq, Square threatenedSq, bool add)
{
    public readonly Piece Pc = pc;
    public readonly Piece ThreatenedPc = threatenedPc;
    public readonly Square PcSq = pcSq;
    public readonly Square ThreatenedSq = threatenedSq;
    public readonly bool Add = add;
}

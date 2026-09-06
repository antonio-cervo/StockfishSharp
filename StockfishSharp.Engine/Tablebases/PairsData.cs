// Porting di "struct PairsData" (tbprobe.cpp:367-390) e dei numeretti letti dal file mappato
// (number<T,LE>, tbprobe.cpp:160-172). Deviazione dichiarata in docs/syzygy-porting-plan.md:
// niente puntatori grezzi in una regione mmap'd — tutti i campi che nella fonte sono puntatori
// diventano un offset (int) dentro il byte[] dell'intero file, letto per intero in RAM.

namespace StockfishSharp.Engine.Tablebases;

/// <summary>Lettura di interi da un <c>byte[]</c> a un dato offset, nell'endianness richiesta dal
/// chiamante — equivalente di <c>number&lt;T,LE&gt;(addr)</c>, tbprobe.cpp:160-172 (qui l'host è
/// sempre little-endian in pratica, ma si tiene la stessa distinzione esplicita della fonte per
/// fedeltà, invece di assumerlo implicitamente).</summary>
public static class TbBinary
{
    public static ushort ReadU16Le(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    public static uint ReadU32Le(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    /// <summary>Big-endian a 32 bit letto a partire da un puntatore <c>u32*</c> — usato solo in
    /// <c>decompress_pairs</c> per il refill del buffer a 64 bit.</summary>
    public static uint ReadU32Be(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>Big-endian a 64 bit — il primo caricamento in <c>decompress_pairs</c>
    /// (<c>number&lt;u64,BigEndian&gt;(ptr)</c>), letto come due u32 BE consecutivi.</summary>
    public static ulong ReadU64Be(byte[] d, int o) => ((ulong)ReadU32Be(d, o) << 32) | ReadU32Be(d, o + 4);
}

/// <summary><c>struct LR</c>, tbprobe.cpp:201-218 — qui pre-scomposto in due array
/// <c>Left</c>/<c>Right</c> invece di 3 byte impacchettati letti al volo, così
/// <c>decompress_pairs</c> indicizza direttamente senza ricalcolare i bit ogni volta (stesso
/// risultato, calcolato una volta sola quando <see cref="TbTable.SetSizes"/> popola il buffer
/// invece che ad ogni lookup).</summary>
public sealed class BTree
{
    public readonly ushort[] Left;
    public readonly ushort[] Right;

    public BTree(int count)
    {
        Left = new ushort[count];
        Right = new ushort[count];
        // LR{{0x00, 0xF0, 0xFF}} di default, tbprobe.cpp:1214 — Left=0, Right=0xFFF (sentinella
        // foglia in set_symlen).
        Array.Fill(Right, (ushort)0xFFF);
    }

    /// <summary>Decodifica una entry LR grezza (3 byte) come <c>LR::get&lt;Side&gt;</c>,
    /// tbprobe.cpp:210-215, e la scrive in posizione <paramref name="sym"/>.</summary>
    public void SetFromRaw(int sym, byte b0, byte b1, byte b2)
    {
        Left[sym] = (ushort)(((b1 & 0xF) << 8) | b0);
        Right[sym] = (ushort)((b2 << 4) | (b1 >> 4));
    }
}

/// <summary><c>struct PairsData</c>, tbprobe.cpp:367-390. Puntatori grezzi della fonte -> offset
/// interi in <see cref="RawData"/> (il file .rtbw/.rtbz intero, condiviso da tutte le PairsData
/// della stessa <see cref="TbTable"/>).</summary>
public sealed class PairsData
{
    public byte Flags;
    public byte MaxSymLen;
    public byte MinSymLen;
    public uint BlocksNum;
    public long SizeofBlock;
    public long Span;

    /// <summary>File condiviso (l'intero .rtbw/.rtbz), popolato da <see cref="TbTable.Mapped"/>.</summary>
    public byte[] RawData = [];

    public int LowestSymOffset; // Sym* lowestSym
    public int BlockLengthOffset; // u16* blockLength
    public uint BlockLengthSize;
    public int SparseIndexOffset; // SparseEntry* sparseIndex (6 byte: u32 block LE + u16 offset LE)
    public long SparseIndexSize;
    public int DataOffset; // u8* data — inizio dei dati Huffman compressi
    public int DataEndOffset; // u8* dataEnd

    public ulong[] Base64 = [];
    public byte[] SymLen = []; // std::vector<u8> symlen, dimensionato SymCount
    public BTree Btree = new(0);

    public readonly Piece[] Pieces = new Piece[TbConstants.TbPieces];
    public readonly ulong[] GroupIdx = new ulong[TbConstants.TbPieces + 1];
    public readonly int[] GroupLen = new int[TbConstants.TbPieces + 1];
    public readonly ushort[] MapIdx = new ushort[4];
}

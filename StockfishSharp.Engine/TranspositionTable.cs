// Corrisponde a src/tt.h + src/tt.cpp della fonte upstream. Vedi Types.cs per la nota generale
// sul porting.
//
// Semplificazioni deliberate: nessuna gestione NUMA/huge-page (aligned_large_pages_alloc,
// clear() multi-thread per nodo NUMA) — allocazione .NET semplice, il clear gira su un solo
// thread finché non arriverà il vero threading Lazy SMP (fase rifinitura). Niente RelaxedAtomic
// (i campi della fonte sono pensati per accessi concorrenti senza sincronizzazione, "va bene
// anche se raro un dato incoerente" — non ancora rilevante a thread singolo). L'algoritmo
// (cluster da 3 entry, bound/pv/generazione impacchettati in un byte, strategia di rimpiazzo,
// invecchiamento) è portato fedelmente.

namespace StockfishSharp.Engine;

/// <summary>Copia locale dei dati di una entry — <c>TTData</c>, tt.h:47-65.</summary>
public readonly struct TTData(Move move, int value, int eval, int depth, Bound bound, bool isPv)
{
    public readonly Move Move = move;
    public readonly int Value = value;
    public readonly int Eval = eval;
    public readonly int Depth = depth;
    public readonly Bound Bound = bound;
    public readonly bool IsPv = isPv;
}

/// <summary>Risultato di una probe: se l'entry era già occupata, i suoi dati, e l'indice a cui
/// scrivere un eventuale aggiornamento — sostituisce la coppia TTWriter/puntatore della fonte
/// (tt.h:71-80): niente puntatori grezzi, si richiama <see cref="TranspositionTable.Save"/> con
/// lo stesso indice invece di passare un oggetto scrivibile a parte.</summary>
public readonly struct TTProbeResult(bool found, TTData data, int writeIndex)
{
    public readonly bool Found = found;
    public readonly TTData Data = data;
    public readonly int WriteIndex = writeIndex;
}

/// <summary>Una entry della transposition table, 10 byte utili come nella fonte (tt.cpp:37-54) —
/// chiave troncata a 16 bit, profondità offset da <see cref="Ply.DepthNone"/> per usare 0 come
/// "non occupata", pv/bound/generazione impacchettati in un solo byte.</summary>
internal struct TTEntry
{
    public ushort Key16;
    public byte Depth8;
    public byte GenBound8;
    public ushort Move16;
    public short Value16;
    public short Eval16;

    private const byte GenerationBits = 5;
    private const byte GenerationMask = (1 << GenerationBits) - 1;
    private const byte BoundShift = GenerationBits;
    private const byte BoundMask = 0b11 << BoundShift;
    private const byte PvShift = BoundShift + 2;
    private const byte PvMask = 1 << PvShift;

    public readonly bool IsOccupied => Depth8 != 0;

    public readonly TTData Read() => new(
        new Move(Move16),
        Value16,
        Eval16,
        Ply.DepthNone + Depth8,
        (Bound)((GenBound8 & BoundMask) >> BoundShift),
        (GenBound8 & PvMask) != 0);

    public readonly byte RelativeAge(byte currGeneration) => (byte)((currGeneration - GenBound8) & GenerationMask);

    /// <summary>Inserisce nuovi dati, possibilmente sovrascrivendo una posizione diversa —
    /// <c>TTEntry::save</c>, tt.cpp:101-135.</summary>
    public void Save(ulong k, int v, bool pv, Bound b, int d, Move m, int ev, byte currGeneration)
    {
        if (m != Move.None || (ushort)k != Key16)
            Move16 = m.Raw;

        if (b == Bound.Exact || (ushort)k != Key16 || d - Ply.DepthNone + (pv ? 2 : 0) > Depth8 - 4
            || RelativeAge(currGeneration) != 0)
        {
            Key16 = (ushort)k;
            Depth8 = (byte)(d - Ply.DepthNone);
            GenBound8 = (byte)(currGeneration | ((byte)b << BoundShift) | ((pv ? 1 : 0) << PvShift));
            Value16 = (short)v;
            Eval16 = (short)ev;
        }
        // Invecchiamento secondario: importante per la ricerca elementare del matto — se l'entry è
        // abbastanza profonda e ha un punteggio decisivo (vicino a matto) ma non viene sovrascritta
        // sopra, la si "invecchia" leggermente riducendo la profondità memorizzata.
        else if (Depth8 + Ply.DepthNone >= 5 && (Bound)((GenBound8 & BoundMask) >> BoundShift) != Bound.Exact)
        {
            short v16 = Value16;
            if (Math.Abs((int)v16) < Values.Infinite && Values.IsDecisive(v16))
                Depth8 = (byte)Math.Max(Depth8 - 1, 0);
        }
    }
}

public sealed class TranspositionTable
{
    private const int ClusterSize = 3;

    private TTEntry[] _entries = [];
    private int _clusterCount;
    private byte _generation;

    public void Resize(int mbSize)
    {
        _clusterCount = Math.Max(1, mbSize * 1024 * 1024 / (ClusterSize * 10));
        _entries = new TTEntry[(long)_clusterCount * ClusterSize];
        _generation = 0;
    }

    public void Clear() => Array.Clear(_entries);

    public void NewSearch() => _generation++;

    public byte Generation => _generation;

    /// <summary>Indice del primo entry del cluster per questa chiave — <c>first_entry</c>,
    /// tt.cpp:293-296. <c>mul_hi64</c> (i 64 bit alti di key*clusterCount) distribuisce la chiave
    /// uniformemente sui cluster senza bisogno che clusterCount sia una potenza di due.</summary>
    private int FirstEntryIndex(ulong key) => (int)(MulHi64(key, (ulong)_clusterCount) * ClusterSize);

    private static ulong MulHi64(ulong a, ulong b) => (ulong)(((UInt128)a * b) >> 64);

    /// <summary>Cerca la posizione nella tabella — <c>TranspositionTable::probe</c>,
    /// tt.cpp:270-290.</summary>
    public TTProbeResult Probe(ulong key)
    {
        int baseIdx = FirstEntryIndex(key);
        ushort key16 = (ushort)key;

        for (int i = 0; i < ClusterSize; i++)
            if (_entries[baseIdx + i].Key16 == key16)
                return new TTProbeResult(_entries[baseIdx + i].IsOccupied, _entries[baseIdx + i].Read(), baseIdx + i);

        int replace = baseIdx;
        for (int i = 1; i < ClusterSize; i++)
        {
            int idx = baseIdx + i;
            if (_entries[replace].Depth8 - 8 * _entries[replace].RelativeAge(_generation)
                > _entries[idx].Depth8 - 8 * _entries[idx].RelativeAge(_generation))
                replace = idx;
        }

        return new TTProbeResult(false, new TTData(Move.None, Values.None, Values.None, Ply.DepthNone, Bound.None, false), replace);
    }

    public void Save(int writeIndex, ulong key, int value, bool pv, Bound bound, int depth, Move move, int eval) =>
        _entries[writeIndex].Save(key, value, pv, bound, depth, move, eval, _generation);

    /// <summary>Frazione (permille) di entry occupate — <c>hashfull</c>, tt.cpp:242-250. Solo per
    /// diagnostica UCI ("info hashfull").</summary>
    public int HashFull()
    {
        int cnt = 0;
        int sampled = Math.Min(1000, _clusterCount);
        for (int i = 0; i < sampled; i++)
            for (int j = 0; j < ClusterSize; j++)
                if (_entries[(i * ClusterSize) + j].IsOccupied) cnt++;

        return sampled == 0 ? 0 : cnt * 1000 / (sampled * ClusterSize);
    }
}

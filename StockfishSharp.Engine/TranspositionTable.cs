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
    internal const byte GenerationMask = (1 << GenerationBits) - 1;
    private const byte BoundShift = GenerationBits;
    private const byte BoundMask = 0b11 << BoundShift;
    private const byte PvShift = BoundShift + 2;
    private const byte PvMask = 1 << PvShift;

    public readonly bool IsOccupied => Depth8 != 0;

    /// <summary><c>TTWriter::penalize</c>, tt.cpp:156-159 — opera direttamente su
    /// <c>depth8</c> (il campo grezzo, non la profondita' logica).</summary>
    public void Penalize(int penalty) => Depth8 = (byte)Math.Max(Depth8 - penalty, 0);

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

/// <summary>Un cluster: 3 entry da 10 byte piu' 2 byte di padding, per un totale di 32 byte esatti
/// — <c>struct Cluster</c>, tt.cpp:170-175, dove la fonte mette un <c>static_assert(sizeof(Cluster)
/// == 32, "Suboptimal Cluster size")</c>. Quei 2 byte NON sono spreco: portano il cluster a mezza
/// cache line, cosi' le tre entry sondate insieme non scavalcano mai due linee di cache — e la
/// probe della TT e' l'accesso casuale piu' frequente del motore.
///
/// Fino al 2026-09-07 questo porting non aveva il cluster affatto: allocava un <c>TTEntry[]</c>
/// piatto e calcolava <c>clusterCount = mbSize*1MB / (3*10)</c>, cioe' diviso 30 invece di 32. A
/// parita' di "Hash" dichiarato allocava quindi il 6,67% di entry IN PIU' della fonte (con Hash 16:
/// 559.240 cluster contro 524.288), il che rendeva ogni confronto di nodi contro l'oracolo
/// leggermente a nostro favore.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 32)]
internal struct Cluster
{
    public TTEntry E0;
    public TTEntry E1;
    public TTEntry E2;
}

public sealed class TranspositionTable
{
    private const int ClusterSize = 3;

    private Cluster[] _clusters = [];
    private int _clusterCount;
    private byte _generation;

    /// <summary>Accesso per riferimento alla i-esima entry di un cluster. Le tre entry sono
    /// contigue in memoria (layout sequenziale, 10 byte l'una), quindi l'aritmetica di riferimento
    /// e' valida — equivale a <c>&amp;cluster.entry[i]</c> della fonte.</summary>
    private ref TTEntry EntryAt(int flatIndex)
    {
        ref Cluster c = ref _clusters[flatIndex / ClusterSize];
        return ref System.Runtime.CompilerServices.Unsafe.Add(ref c.E0, flatIndex % ClusterSize);
    }

    public void Resize(int mbSize)
    {
        // tt.cpp:184 — "clusterCount = mbSize * 1024 * 1024 / sizeof(Cluster)", sizeof(Cluster)==32.
        _clusterCount = Math.Max(1, mbSize * 1024 * 1024 / 32);
        _clusters = new Cluster[_clusterCount];
        _generation = 0;
    }

    /// <summary><c>TranspositionTable::clear</c>, tt.cpp:187-221 — la fonte azzera anche
    /// <c>generation8</c>, non solo la tabella. Ometterlo lasciava un contatore che continuava a
    /// crescere attraverso <c>ucinewgame</c>/"Clear Hash", cioe' attraverso i confini di partita.</summary>
    public void Clear()
    {
        _generation = 0;
        Array.Clear(_clusters);
    }

    /// <summary><c>TranspositionTable::new_search</c>, tt.cpp:238-242. Il mascheramento NON e'
    /// pignoleria: <c>genBound8</c> impacca generazione (5 bit), bound (2 bit) e pv (1 bit) nello
    /// stesso byte, e <c>save</c> li unisce con un OR. Senza la maschera, dalla 32esima ricerca in
    /// poi i bit alti della generazione traboccano nei campi bound e pv, che vengono riletti
    /// sbagliati: un limite UPPER puo' tornare come EXACT, cioe' un taglio che non andava fatto.
    /// La fonte ha su questo un assert esplicito in <c>save</c>
    /// (<c>assert(curr_generation &lt;= GENERATION_MASK); // TT::new_search() plays nice</c>).</summary>
    public void NewSearch()
    {
        _generation++;
        _generation &= TTEntry.GenerationMask; // tt.cpp:241
    }

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
        {
            ref TTEntry e = ref EntryAt(baseIdx + i);
            if (e.Key16 == key16)
                return new TTProbeResult(e.IsOccupied, e.Read(), baseIdx + i);
        }

        int replace = baseIdx;
        for (int i = 1; i < ClusterSize; i++)
        {
            int idx = baseIdx + i;
            ref TTEntry r = ref EntryAt(replace);
            ref TTEntry c = ref EntryAt(idx);
            if (r.Depth8 - 8 * r.RelativeAge(_generation) > c.Depth8 - 8 * c.RelativeAge(_generation))
                replace = idx;
        }

        return new TTProbeResult(false, new TTData(Move.None, Values.None, Values.None, Ply.DepthNone, Bound.None, false), replace);
    }

    public void Save(int writeIndex, ulong key, int value, bool pv, Bound bound, int depth, Move move, int eval) =>
        EntryAt(writeIndex).Save(key, value, pv, bound, depth, move, eval, _generation);

    /// <summary>Marca una entry come inutile decrementandone la profondita' memorizzata —
    /// <c>TTWriter::penalize</c>, tt.cpp:155-159. Il <c>Math.Max(..., 0)</c> della fonte protegge
    /// da underflow dovuti a letture concorrenti: 0 significa "non occupata".</summary>
    public void Penalize(int writeIndex, int penalty) =>
        EntryAt(writeIndex).Penalize(penalty);

    /// <summary>Frazione (permille) di entry occupate — <c>hashfull</c>, tt.cpp:242-250. Solo per
    /// diagnostica UCI ("info hashfull").</summary>
    public int HashFull()
    {
        int cnt = 0;
        int sampled = Math.Min(1000, _clusterCount);
        for (int i = 0; i < sampled; i++)
            for (int j = 0; j < ClusterSize; j++)
                if (EntryAt((i * ClusterSize) + j).IsOccupied) cnt++;

        return sampled == 0 ? 0 : cnt * 1000 / (sampled * ClusterSize);
    }
}

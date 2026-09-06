// Porting di "class TBTables" (tbprobe.cpp:491-603) — registro hash (Robin Hood) delle tabelle
// caricate, popolato da Tablebase.Init().

namespace StockfishSharp.Engine.Tablebases;

public sealed class TbTables
{
    private const int Size = 1 << 12; // 4K table, indexed by key's 12 lsb
    private const int Overflow = 1;

    private struct Entry
    {
        public ulong Key;
        public TbTableWdl? Wdl;
        public TbTableDtz? Dtz;
    }

    private Entry[] _hashTable = new Entry[Size + Overflow];
    private readonly List<TbTableWdl> _wdlTable = [];
    private readonly List<TbTableDtz> _dtzTable = [];
    private int _foundDtzFiles;
    private int _foundWdlFiles;

    /// <summary><c>TBTables::insert</c>, tbprobe.cpp:514-540 (Robin Hood hashing).</summary>
    private void Insert(ulong key, TbTableWdl wdl, TbTableDtz dtz)
    {
        uint homeBucket = (uint)key & (Size - 1);
        Entry entry = new() { Key = key, Wdl = wdl, Dtz = dtz };

        for (uint bucket = homeBucket; bucket < Size + Overflow - 1; bucket++)
        {
            ulong otherKey = _hashTable[bucket].Key;
            if (otherKey == key || _hashTable[bucket].Wdl is null)
            {
                _hashTable[bucket] = entry;
                return;
            }

            uint otherHomeBucket = (uint)otherKey & (Size - 1);
            if (otherHomeBucket > homeBucket)
            {
                (entry, _hashTable[bucket]) = (_hashTable[bucket], entry);
                key = otherKey;
                homeBucket = otherHomeBucket;
            }
        }
        throw new InvalidOperationException("TB hash table size too low!");
    }

    public TbTableWdl? GetWdl(ulong key)
    {
        for (uint i = (uint)key & (Size - 1); ; i++)
            if (_hashTable[i].Key == key || _hashTable[i].Wdl is null)
                return _hashTable[i].Wdl;
    }

    public TbTableDtz? GetDtz(ulong key)
    {
        for (uint i = (uint)key & (Size - 1); ; i++)
            if (_hashTable[i].Key == key || _hashTable[i].Dtz is null)
                return _hashTable[i].Dtz;
    }

    public void Clear()
    {
        _hashTable = new Entry[Size + Overflow];
        _wdlTable.Clear();
        _dtzTable.Clear();
        _foundDtzFiles = 0;
        _foundWdlFiles = 0;
    }

    public void Info()
    {
        Console.Error.WriteLine(
            $"info string Found {_foundWdlFiles} WDL and {_foundDtzFiles} DTZ tablebase files (up to {Tablebase.MaxCardinality}-man).");
    }

    /// <summary><c>TBTables::add</c>, tbprobe.cpp:572-603.</summary>
    public void Add(PieceType[] pieces)
    {
        var codeBuilder = new System.Text.StringBuilder();
        foreach (var pt in pieces)
            codeBuilder.Append(TbConstants.PieceToChar[(byte)pt]);

        string raw = codeBuilder.ToString();
        int kIdx = raw.IndexOf('K', 1);
        string code = raw[..kIdx] + "v" + raw[kIdx..]; // KRK -> KRvK

        if (TbFile.Find(code + ".rtbz") is not null)
            _foundDtzFiles++;

        string? wdlPath = TbFile.Find(code + ".rtbw");
        if (wdlPath is null)
            return;

        _foundWdlFiles++;
        Tablebase.MaxCardinality = Math.Max(pieces.Length, Tablebase.MaxCardinality);

        var wdl = new TbTableWdl(code);
        var dtz = new TbTableDtz(wdl);
        _wdlTable.Add(wdl);
        _dtzTable.Add(dtz);

        Insert(wdl.Key, wdl, dtz);
        Insert(wdl.Key2, wdl, dtz);
    }
}

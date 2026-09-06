// Porting di "template<TBType Type> struct TBTable" (tbprobe.cpp:396-486) e delle funzioni che lo
// popolano al primo accesso: set/set_groups/set_symlen/set_sizes/set_dtz_map (tbprobe.cpp:1033-
// 1381) + mapped (tbprobe.cpp:1387-1426). Deviazione dichiarata in docs/syzygy-porting-plan.md:
// la specializzazione di template C++ (Ret/Sides/check_dtz_stm/map_score diversi per WDL e DTZ)
// diventa qui dispatch virtuale fra due classi concrete invece che generics — stesso effetto,
// idioma C#.

namespace StockfishSharp.Engine.Tablebases;

public abstract class TbTable
{
    private const byte SplitBit = 1;
    private const byte HasPawnsBit = 2;
    private const byte White = 0, Grey = 1, Black = 2;

    /// <summary><c>std::atomic_bool ready</c> — qui senza atomicità esplicita: il lock in
    /// <see cref="Mapped"/> è sufficiente perché tutte le letture di <see cref="Ready"/> avvengono
    /// sotto lo stesso lock o dopo una scrittura già completata (nessuna doppia lettura lock-free
    /// come nella fonte, che ottimizza per il caso comune "già pronto" — qui non necessario dato
    /// che .NET non espone lo stesso costo di un mutex per un semplice probe).</summary>
    public volatile bool Ready;
    public byte[]? RawData;

    public ulong Key;
    public ulong Key2;
    public int PieceCount;
    public bool HasPawns;
    public bool HasUniquePieces;
    public readonly byte[] PawnCount = new byte[2]; // [Lead color / other color]

    public abstract int Sides { get; }

    private PairsData[][] _items = [];

    protected void AllocateItems()
    {
        int files = HasPawns ? 4 : 1;
        _items = new PairsData[Sides][];
        for (int i = 0; i < Sides; i++)
        {
            _items[i] = new PairsData[files];
            for (int f = 0; f < files; f++)
                _items[i][f] = new PairsData();
        }
    }

    /// <summary><c>PairsData* get(int stm, int f)</c>, tbprobe.cpp:414.</summary>
    public PairsData Get(int stm, int f) => _items[stm % Sides][HasPawns ? f : 0];

    /// <summary><c>check_dtz_stm</c>, overload WDL (tbprobe.cpp:746) — sempre vero.</summary>
    public virtual bool CheckDtzStm(int stm, File f) => true;

    /// <summary><c>map_score</c>, overload WDL (tbprobe.cpp:758) — <c>value - 2</c>.</summary>
    public virtual int MapScore(File f, int value, WdlScore wdl) => value - 2;

    /// <summary><c>set_dtz_map</c>, overload WDL (tbprobe.cpp:1237) — no-op.</summary>
    internal virtual bool SetDtzMap(byte[] file, ref int data, File maxFile, int end) => true;

    // --- fits(), tbprobe.cpp:222-229 ---
    protected static bool Fits(int p, long count, long stride, int end)
    {
        if (p > end) return false;
        long room = end - p;
        return count == 0 || stride <= room / count;
    }

    /// <summary><c>Position::set(const string& code, Color c, StateInfo* si)</c>, position.cpp:
    /// 542-558 — costruisce il FEN materiale "8/weak/8/8/8/8/strong/8 w - - 0 10" usato solo per
    /// calcolare le chiavi materiali delle due orientazioni di colore di un codice come "KRvK".</summary>
    protected static string BuildMaterialFen(string code, Color c)
    {
        int kIdx = code.IndexOf('K', 1);
        int vIdx = code.IndexOf('v');
        int strongEnd = Math.Min(vIdx < 0 ? kIdx : vIdx, kIdx);

        string[] sides = [code[kIdx..], code[..strongEnd]]; // [0]=weak, [1]=strong
        sides[(byte)c] = sides[(byte)c].ToLowerInvariant();

        return $"8/{sides[0]}{8 - sides[0].Length}/8/8/8/8/{sides[1]}{8 - sides[1].Length}/8 w - - 0 10";
    }

    /// <summary><c>Position::fen()</c>-style build usata solo per il probe: ricostruisce il nome
    /// file (es. "KRvK.rtbw") dai pezzi realmente presenti sulla scacchiera — <c>mapped()</c>,
    /// tbprobe.cpp:1404-1413.</summary>
    protected static string PiecesFileNameCode(Position pos)
    {
        var w = new System.Text.StringBuilder();
        var b = new System.Text.StringBuilder();
        for (PieceType pt = PieceType.King; pt >= PieceType.Pawn; pt--)
        {
            w.Append(TbConstants.PieceToChar[(byte)pt], Bitboards.PopCount(pos.Pieces(Color.White, pt)));
            b.Append(TbConstants.PieceToChar[(byte)pt], Bitboards.PopCount(pos.Pieces(Color.Black, pt)));
        }
        return w.ToString() + "v" + b;
    }

    // --- set_groups, tbprobe.cpp:1033-1084 ---
    protected static void SetGroups(TbTable e, PairsData d, int[] order, File f)
    {
        int n = 0;
        int firstLen = e.HasPawns ? 0 : e.HasUniquePieces ? 3 : 2;
        d.GroupLen[n] = 1;
        for (int i = 1; i < e.PieceCount; i++)
        {
            firstLen--;
            if (firstLen > 0 || d.Pieces[i] == d.Pieces[i - 1])
                d.GroupLen[n]++;
            else
                d.GroupLen[++n] = 1;
        }
        d.GroupLen[++n] = 0;

        bool pp = e.HasPawns && e.PawnCount[1] != 0;
        int next = pp ? 2 : 1;
        int freeSquares = 64 - d.GroupLen[0] - (pp ? d.GroupLen[1] : 0);
        ulong idx = 1;

        for (int k = 0; next < n || k == order[0] || k == order[1]; k++)
        {
            if (k == order[0])
            {
                d.GroupIdx[0] = idx;
                idx *= (ulong)(e.HasPawns ? TbConstants.LeadPawnsSize[d.GroupLen[0], (byte)f]
                    : e.HasUniquePieces ? 31332 : 462);
            }
            else if (k == order[1])
            {
                d.GroupIdx[1] = idx;
                idx *= (ulong)TbConstants.Binomial[d.GroupLen[1], 48 - d.GroupLen[0]];
            }
            else
            {
                d.GroupIdx[next] = idx;
                idx *= (ulong)TbConstants.Binomial[d.GroupLen[next], freeSquares];
                freeSquares -= d.GroupLen[next];
                next++;
            }
        }
        d.GroupIdx[n] = idx;
    }

    // --- set_symlen, tbprobe.cpp:1095-1123 ---
    private static byte SetSymlen(PairsData d, ushort s, byte[] colour, ref bool cyclic)
    {
        colour[s] = Grey;
        ushort sr = d.Btree.Right[s];
        if (sr == 0xFFF) { colour[s] = Black; return 0; }

        ushort sl = d.Btree.Left[s];
        if (colour[sl] == Grey || colour[sr] == Grey)
        {
            cyclic = true;
            colour[s] = Black;
            return 0;
        }
        if (colour[sl] == White) d.SymLen[sl] = SetSymlen(d, sl, colour, ref cyclic);
        if (colour[sr] == White) d.SymLen[sr] = SetSymlen(d, sr, colour, ref cyclic);
        colour[s] = Black;
        return (byte)(d.SymLen[sl] + d.SymLen[sr] + 1);
    }

    // --- set_sizes, tbprobe.cpp:1125-1235 ---
    protected static bool SetSizes(PairsData d, byte[] file, ref int data, int end)
    {
        if (!Fits(data, 1, 1, end)) return false;
        d.Flags = file[data]; data++;

        if ((d.Flags & (byte)TbFlag.SingleValue) != 0)
        {
            d.BlocksNum = d.BlockLengthSize = 0;
            d.Span = d.SparseIndexSize = 0;
            if (!Fits(data, 1, 1, end)) return false;
            d.MinSymLen = file[data]; data++;
            return true;
        }

        int zeroIdx = Array.IndexOf(d.GroupLen, 0, 0, 7);
        ulong tbSize = d.GroupIdx[zeroIdx];

        if (!Fits(data, 9, 1, end)) return false;
        if (file[data] >= 64 || file[data + 1] >= 64) return false;

        d.SizeofBlock = 1L << file[data]; data++;
        d.Span = 1L << file[data]; data++;
        d.SparseIndexSize = (long)((tbSize + (ulong)d.Span - 1) / (ulong)d.Span);
        byte padding = file[data]; data++;
        d.BlocksNum = TbBinary.ReadU32Le(file, data); data += 4;
        d.BlockLengthSize = d.BlocksNum + padding;
        d.MaxSymLen = file[data]; data++;
        d.MinSymLen = file[data]; data++;

        if (d.MinSymLen == 0 || d.MinSymLen > d.MaxSymLen || d.MaxSymLen >= 64) return false;

        d.LowestSymOffset = data;
        int base64Size = d.MaxSymLen - d.MinSymLen + 1;
        d.Base64 = new ulong[base64Size];
        if (!Fits(data, base64Size, 2, end)) return false;

        for (int i = base64Size - 2; i >= 0; i--)
        {
            ushort lowI = TbBinary.ReadU16Le(file, d.LowestSymOffset + i * 2);
            ushort lowI1 = TbBinary.ReadU16Le(file, d.LowestSymOffset + (i + 1) * 2);
            d.Base64[i] = (d.Base64[i + 1] + lowI - lowI1) / 2;
            if (d.Base64[i] * 2 < d.Base64[i + 1]) return false;
        }
        for (int i = 0; i < base64Size; i++)
            d.Base64[i] <<= 64 - i - d.MinSymLen;

        data += base64Size * 2;

        if (!Fits(data, 2, 1, end)) return false;
        int symlenSize = TbBinary.ReadU16Le(file, data);
        data += 2;
        if (!Fits(data, symlenSize, 3, end)) return false;

        d.Btree = new BTree(TbConstants.SymCount);
        for (int sym = 0; sym < symlenSize; sym++)
            d.Btree.SetFromRaw(sym, file[data + sym * 3], file[data + sym * 3 + 1], file[data + sym * 3 + 2]);

        d.SymLen = new byte[TbConstants.SymCount];
        byte[] colour = new byte[TbConstants.SymCount]; // 0 == White di default
        bool cyclic = false;
        for (int sym = 0; sym < symlenSize; sym++)
            if (colour[sym] == White)
                d.SymLen[sym] = SetSymlen(d, (ushort)sym, colour, ref cyclic);
        if (cyclic) return false;

        data += symlenSize * 3 + (symlenSize & 1);
        return true;
    }

    /// <summary><c>set&lt;T&gt;</c>, tbprobe.cpp:1289-1381 — popola tutte le PairsData della
    /// tabella dai dati appena letti dal file. Chiamato una sola volta, al primo accesso
    /// (<see cref="Mapped"/>).</summary>
    private bool Populate(byte[] file, int end)
    {
        int data = 4; // Skip Magics's header, tbprobe.cpp:348 — vedi la nota in TbFile.ReadTable
        if (!Fits(data, 1, 1, end)) return false;

        bool hasPawnsFlag = (file[data] & HasPawnsBit) != 0;
        bool splitFlag = (file[data] & SplitBit) != 0;
        if (HasPawns != hasPawnsFlag || (Key != Key2) != splitFlag) return false;
        data++;

        int activeSides = Sides == 2 && Key != Key2 ? 2 : 1;
        File maxFile = HasPawns ? File.D : File.A;
        bool pp = HasPawns && PawnCount[1] != 0;

        for (File f = File.A; f <= maxFile; f++)
        {
            if (!Fits(data, (1 + (pp ? 1 : 0)) + PieceCount, 1, end)) return false;

            int[][] order = [
                [file[data] & 0xF, pp ? file[data + 1] & 0xF : 0xF],
                [file[data] >> 4, pp ? file[data + 1] >> 4 : 0xF],
            ];
            data += 1 + (pp ? 1 : 0);

            for (int k = 0; k < PieceCount; k++, data++)
                for (int i = 0; i < activeSides; i++)
                    Get(i, (int)f).Pieces[k] = (Piece)(i != 0 ? file[data] >> 4 : file[data] & 0xF);

            for (int i = 0; i < activeSides; i++)
                SetGroups(this, Get(i, (int)f), order[i], f);
        }

        data += data & 1; // word alignment

        for (File f = File.A; f <= maxFile; f++)
            for (int i = 0; i < activeSides; i++)
                if (!SetSizes(Get(i, (int)f), file, ref data, end)) return false;

        if (!SetDtzMap(file, ref data, maxFile, end)) return false;

        for (File f = File.A; f <= maxFile; f++)
            for (int i = 0; i < activeSides; i++)
            {
                var d = Get(i, (int)f);
                d.RawData = file;
                d.SparseIndexOffset = data;
                if (!Fits(data, d.SparseIndexSize, 6, end)) return false;
                data += (int)(d.SparseIndexSize * 6);
            }

        for (File f = File.A; f <= maxFile; f++)
            for (int i = 0; i < activeSides; i++)
            {
                var d = Get(i, (int)f);
                d.BlockLengthOffset = data;
                if (!Fits(data, d.BlockLengthSize, 2, end)) return false;
                data += (int)(d.BlockLengthSize * 2);
            }

        for (File f = File.A; f <= maxFile; f++)
            for (int i = 0; i < activeSides; i++)
            {
                data = (data + 0x3F) & ~0x3F; // allineamento a 64 byte
                var d = Get(i, (int)f);
                d.DataOffset = data;
                if (!Fits(data, d.BlocksNum, d.SizeofBlock, end)) return false;
                data += (int)(d.BlocksNum * d.SizeofBlock);
                d.DataEndOffset = data;
            }

        return true;
    }

    /// <summary><c>mapped&lt;Type&gt;</c>, tbprobe.cpp:1387-1426 — senza il vero mmap (vedi nota
    /// in cima al file) e senza il mutex esplicito: <see cref="Ready"/> è scritto una sola volta,
    /// sotto <c>lock</c>, e la doppia verifica prima/dopo il lock resta comunque corretta in .NET
    /// (una scrittura a <c>volatile bool</c> è visibile subito agli altri thread).</summary>
    private readonly object _mapLock = new();

    public byte[]? Mapped(Position pos, bool isWdl)
    {
        if (Ready) return RawData;

        lock (_mapLock)
        {
            if (Ready) return RawData;

            string code = PiecesFileNameCode(pos);
            string fname = (Key == pos.MaterialKey ? code : SwapSides(code)) + (isWdl ? ".rtbw" : ".rtbz");

            string? path = TbFile.Find(fname);
            byte[]? data = path is null ? null : TbFile.ReadTable(path, isWdl);

            if (data is not null)
            {
                AllocateItems();
                if (!Populate(data, data.Length))
                    throw new TbFileCorruptedException($"Corrupted table in file {fname}");
            }

            RawData = data;
            Ready = true;
            return RawData;
        }
    }

    private static string SwapSides(string code)
    {
        int v = code.IndexOf('v');
        return code[(v + 1)..] + "v" + code[..v];
    }
}

/// <summary><c>TBTable&lt;WDL&gt;</c>, tbprobe.cpp:396-472 — <c>Sides=2</c>.</summary>
public sealed class TbTableWdl : TbTable
{
    public override int Sides => 2;

    /// <summary><c>TBTable&lt;WDL&gt;::TBTable(const std::string& code)</c>, tbprobe.cpp:428-472.</summary>
    public TbTableWdl(string code)
    {
        var pos = new Position();
        pos.Set(BuildMaterialFen(code, Color.White), false);

        Key = pos.MaterialKey;
        PieceCount = Bitboards.PopCount(pos.Pieces());
        HasPawns = pos.Pieces(PieceType.Pawn) != 0;

        HasUniquePieces = false;
        foreach (Color c in new[] { Color.White, Color.Black })
            for (PieceType pt = PieceType.Pawn; pt < PieceType.King; pt++)
                if (Bitboards.PopCount(pos.Pieces(c, pt)) == 1)
                    HasUniquePieces = true;

        bool useWhiteAsLead = pos.Pieces(Color.Black, PieceType.Pawn) == 0
            || (pos.Pieces(Color.White, PieceType.Pawn) != 0
                && Bitboards.PopCount(pos.Pieces(Color.White, PieceType.Pawn)) >= Bitboards.PopCount(pos.Pieces(Color.Black, PieceType.Pawn)));

        PawnCount[0] = (byte)Bitboards.PopCount(pos.Pieces(useWhiteAsLead ? Color.White : Color.Black, PieceType.Pawn));
        PawnCount[1] = (byte)Bitboards.PopCount(pos.Pieces(useWhiteAsLead ? Color.Black : Color.White, PieceType.Pawn));

        pos.Set(BuildMaterialFen(code, Color.Black), false);
        Key2 = pos.MaterialKey;
    }
}

/// <summary><c>TBTable&lt;DTZ&gt;</c>, tbprobe.cpp:396-427,474-486 — <c>Sides=1</c>.</summary>
public sealed class TbTableDtz : TbTable
{
    public override int Sides => 1;

    /// <summary><c>e.map</c>, tbprobe.cpp:403 — offset in <see cref="TbTable.RawData"/> invece di
    /// un puntatore grezzo (vedi nota di deviazione in cima al file).</summary>
    public int MapOffset;

    /// <summary><c>TBTable&lt;DTZ&gt;::TBTable(const TBTable&lt;WDL&gt;& wdl)</c>, tbprobe.cpp:475-486.</summary>
    public TbTableDtz(TbTableWdl wdl)
    {
        Key = wdl.Key;
        Key2 = wdl.Key2;
        PieceCount = wdl.PieceCount;
        HasPawns = wdl.HasPawns;
        HasUniquePieces = wdl.HasUniquePieces;
        PawnCount[0] = wdl.PawnCount[0];
        PawnCount[1] = wdl.PawnCount[1];
    }

    /// <summary><c>check_dtz_stm</c>, overload DTZ (tbprobe.cpp:748-752).</summary>
    public override bool CheckDtzStm(int stm, File f)
    {
        byte flags = (byte)Get(stm, (int)f).Flags;
        bool flagStm = (flags & (byte)TbFlag.Stm) != 0;
        return flagStm == (stm != 0) || (Key == Key2 && !HasPawns);
    }

    /// <summary><c>map_score</c>, overload DTZ (tbprobe.cpp:760-784).</summary>
    public override int MapScore(File f, int value, WdlScore wdl)
    {
        int[] wdlMap = [1, 3, 0, 2, 0];

        byte flags = Get(0, (int)f).Flags;
        ushort[] idx = Get(0, (int)f).MapIdx;
        byte[] data = RawData!;

        if ((flags & (byte)TbFlag.Mapped) != 0)
        {
            int mapIdx = idx[wdlMap[(int)wdl + 2]] + value;
            value = (flags & (byte)TbFlag.Wide) != 0
                ? TbBinary.ReadU16Le(data, MapOffset + mapIdx * 2)
                : data[MapOffset + mapIdx];
        }

        if ((wdl == WdlScore.Win && (flags & (byte)TbFlag.WinPlies) == 0)
            || (wdl == WdlScore.Loss && (flags & (byte)TbFlag.LossPlies) == 0)
            || wdl == WdlScore.CursedWin || wdl == WdlScore.BlessedLoss)
            value *= 2;

        return value + 1;
    }

    /// <summary><c>set_dtz_map</c>, overload DTZ (tbprobe.cpp:1239-1285).</summary>
    internal override bool SetDtzMap(byte[] file, ref int data, File maxFile, int end)
    {
        MapOffset = data;
        for (File f = File.A; f <= maxFile; f++)
        {
            byte flags = Get(0, (int)f).Flags;
            if ((flags & (byte)TbFlag.Mapped) == 0) continue;

            if ((flags & (byte)TbFlag.Wide) != 0)
            {
                data += data & 1; // allineamento a parola
                for (int i = 0; i < 4; i++)
                {
                    if (!Fits(data, 2, 1, end)) return false;
                    Get(0, (int)f).MapIdx[i] = (ushort)(((data - MapOffset) / 2) + 1);
                    long step = 2L * TbBinary.ReadU16Le(file, data) + 2;
                    if (!Fits(data, step, 1, end)) return false;
                    data += (int)step;
                }
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!Fits(data, 1, 1, end)) return false;
                    Get(0, (int)f).MapIdx[i] = (ushort)(data - MapOffset + 1);
                    long step = file[data] + 1;
                    if (!Fits(data, step, 1, end)) return false;
                    data += (int)step;
                }
            }
        }
        data += data & 1;
        return true;
    }
}

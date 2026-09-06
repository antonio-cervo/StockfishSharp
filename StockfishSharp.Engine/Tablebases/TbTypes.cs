// Porting di syzygy/tbprobe.h + le parti di tbprobe.cpp che non sono funzioni (tipi, costanti,
// tabelle combinatorie) — Stockfish::Tablebases. Vedi docs/syzygy-porting-plan.md per le fasi
// (TB1-TB10) e le due deviazioni dichiarate (niente mmap reale, template C++ -> classi C#).

namespace StockfishSharp.Engine.Tablebases;

/// <summary><c>WDLScore</c>, tbprobe.h:48-54.</summary>
public enum WdlScore
{
    Loss = -2,
    BlessedLoss = -1,
    Draw = 0,
    CursedWin = 1,
    Win = 2,
}

/// <summary><c>ProbeState</c>, tbprobe.h:57-62.</summary>
public enum ProbeState
{
    Fail = 0,
    Ok = 1,
    ChangeStm = -1,
    ZeroingBestMove = 2,
}

/// <summary><c>Tablebases::Config</c>, tbprobe.h:41-46 — usato da <see cref="Search"/> come
/// equivalente di <c>Search::Worker::tbConfig</c> (search.cpp:922-973, Step 7).</summary>
public struct TbConfig
{
    public int Cardinality;
    public bool RootInTb;
    public bool UseRule50;
    public int ProbeDepth;
}

/// <summary>Sostituto minimo di <c>Search::RootMove</c> (search.h) per <see
/// cref="Tablebase.RootProbe"/>/<see cref="Tablebase.RootProbeWdl"/>/<see
/// cref="Tablebase.RankRootMoves"/> (TB9) — la struttura vera ha un intero PV
/// (<c>std::vector&lt;Move&gt; pv</c>) più campi per MultiPV/aspiration windows (Flow A4, non
/// ancora portato); qui basta la sola prima mossa (<c>m.pv[0]</c> nella fonte) più i due campi
/// che TB9 effettivamente legge/scrive.</summary>
public sealed class TbRootMove(Move move)
{
    public readonly Move Move = move;
    public int TbRank;
    public int TbScore;
}

/// <summary><c>TBFlag</c>, tbprobe.cpp:119-126.</summary>
[Flags]
public enum TbFlag : byte
{
    None = 0,
    Stm = 1,
    Mapped = 2,
    WinPlies = 4,
    LossPlies = 8,
    Wide = 16,
    SingleValue = 128,
}

public static class TbConstants
{
    public const int TbPieces = 7; // Max number of supported pieces
    public const int MaxDtz = 1 << 18;
    public const int SymCount = 4096;

    public const string PieceToChar = " PNBRQK  pnbrqk";

    // WDL_to_value[], tbprobe.cpp:146-147 — indicizzato da wdl+2.
    public static readonly int[] WdlToValue =
    [
        -Values.Mate + Ply.MaxPly + 1, Values.Draw - 2, Values.Draw, Values.Draw + 2,
        Values.Mate - Ply.MaxPly - 1,
    ];

    public static int OffA1H8(Square sq) => (byte)Types.RankOf(sq) - (byte)Types.FileOf(sq);

    /// <summary><c>dtz_before_zeroing</c>, tbprobe.cpp:177-183.</summary>
    public static int DtzBeforeZeroing(WdlScore wdl) => wdl switch
    {
        WdlScore.Win => 1,
        WdlScore.CursedWin => 101,
        WdlScore.BlessedLoss => -101,
        WdlScore.Loss => -1,
        _ => 0,
    };

    public static int SignOf(int val) => Math.Sign(val);

    // --- Tabelle combinatorie, costruite una volta da Tablebase.Init() (tbprobe.cpp:1545-1637) ---

    public static readonly int[] MapPawns = new int[Squares.Nb];
    public static readonly int[] MapB1H1H7 = new int[Squares.Nb];
    public static readonly int[] MapA1D1D4 = new int[Squares.Nb];
    public static readonly int[,] MapKK = new int[10, Squares.Nb]; // [MapA1D1D4][SQUARE_NB]

    public static readonly int[,] Binomial = new int[6, Squares.Nb]; // [k][n]
    public static readonly int[,] LeadPawnIdx = new int[6, Squares.Nb]; // [leadPawnsCnt][SQUARE_NB]
    public static readonly int[,] LeadPawnsSize = new int[6, 4]; // [leadPawnsCnt][FILE_A..FILE_D]

    private static bool PawnsComp(Square i, Square j) => MapPawns[(byte)i] < MapPawns[(byte)j];

    /// <summary>Ordina un array di case per <see cref="MapPawns"/> crescente, come
    /// <c>pawns_comp</c> usato con <c>std::max_element</c>/<c>std::stable_sort</c>.</summary>
    public static int MaxPawnsCompIndex(Square[] squares, int start, int count)
    {
        int best = start;
        for (int i = start + 1; i < start + count; i++)
            if (MapPawns[(byte)squares[i]] > MapPawns[(byte)squares[best]])
                best = i;
        return best;
    }

    public static void StableSortByMapPawns(Square[] squares, int start, int count)
    {
        Array.Sort(squares, start, count, Comparer<Square>.Create((a, b) => MapPawns[(byte)a].CompareTo(MapPawns[(byte)b])));
    }

    /// <summary><c>Tablebases::init</c>, tbprobe.cpp:1545-1637 — solo la parte di costruzione
    /// delle tabelle combinatorie (la scansione directory/aggiunta tabelle è in
    /// <see cref="Tablebase.Init"/>).</summary>
    public static void BuildCombinatorialTables()
    {
        int code = 0;
        for (Square s = Square.A1; s <= Square.H8; s++)
            if (OffA1H8(s) < 0)
                MapB1H1H7[(byte)s] = code++;

        List<Square> diagonal = [];
        code = 0;
        for (Square s = Square.A1; s <= Square.D4; s++)
        {
            if (OffA1H8(s) < 0 && Types.FileOf(s) <= File.D)
                MapA1D1D4[(byte)s] = code++;
            else if (OffA1H8(s) == 0 && Types.FileOf(s) <= File.D)
                diagonal.Add(s);
        }
        foreach (Square s in diagonal)
            MapA1D1D4[(byte)s] = code++;

        List<(int idx, Square s2)> bothOnDiagonal = [];
        code = 0;
        for (int idx = 0; idx < 10; idx++)
            for (Square s1 = Square.A1; s1 <= Square.D4; s1++)
            {
                if (MapA1D1D4[(byte)s1] != idx || (idx == 0 && s1 != Square.B1))
                    continue;

                for (Square s2 = Square.A1; s2 <= Square.H8; s2++)
                {
                    ulong kingAttacksOrSelf = Attacks.AttacksBb(PieceType.King, s1) | Bitboards.SquareBB(s1);
                    if ((kingAttacksOrSelf & Bitboards.SquareBB(s2)) != 0)
                        continue; // Illegal position
                    if (OffA1H8(s1) == 0 && OffA1H8(s2) > 0)
                        continue; // First on diagonal, second above
                    if (OffA1H8(s1) == 0 && OffA1H8(s2) == 0)
                        bothOnDiagonal.Add((idx, s2));
                    else
                        MapKK[idx, (byte)s2] = code++;
                }
            }

        foreach (var (idx, s2) in bothOnDiagonal)
            MapKK[idx, (byte)s2] = code++;

        Binomial[0, 0] = 1;
        for (int n = 1; n < 64; n++)
            for (int k = 0; k < 6 && k <= n; k++)
                Binomial[k, n] = (k > 0 ? Binomial[k - 1, n - 1] : 0) + (k < n ? Binomial[k, n - 1] : 0);

        int availableSquares = 47;
        for (int leadPawnsCnt = 1; leadPawnsCnt <= 5; leadPawnsCnt++)
            for (File f = File.A; f <= File.D; f++)
            {
                int idx2 = 0;
                for (Rank r = Rank.Rank2; r <= Rank.Rank7; r++)
                {
                    Square sq = Types.MakeSquare(f, r);

                    if (leadPawnsCnt == 1)
                    {
                        MapPawns[(byte)sq] = availableSquares--;
                        MapPawns[(byte)Types.FlipFile(sq)] = availableSquares--;
                    }
                    LeadPawnIdx[leadPawnsCnt, (byte)sq] = idx2;
                    idx2 += Binomial[leadPawnsCnt - 1, MapPawns[(byte)sq]];
                }
                LeadPawnsSize[leadPawnsCnt, (byte)f] = idx2;
            }
    }
}

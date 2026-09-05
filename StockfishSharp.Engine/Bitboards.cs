using System.Numerics;
using System.Text;

// Corrisponde a src/bitboard.h + src/bitboard.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.

namespace StockfishSharp.Engine;

/// <summary>Costanti e funzioni sui bitboard — bitboard.h. In C# usiamo <see cref="ulong"/>
/// direttamente al posto dell'alias <c>Bitboard</c> della fonte (stesso motivo di Value/Depth in
/// Types.cs). Le funzioni di popcount/lsb/msb usano <c>System.Numerics.BitOperations</c> — .NET
/// le compila già sulle istruzioni hardware POPCNT/TZCNT/LZCNT quando disponibili, stesso ruolo
/// dei branch <c>_mm_popcnt_u64</c>/<c>__builtin_ctzll</c> della fonte (bitboard.h:175-266), senza
/// bisogno di replicare la selezione a tempo di compilazione.</summary>
public static class Bitboards
{
    public const ulong FileABB = 0x0101010101010101UL;
    public const ulong FileBBB = FileABB << 1;
    public const ulong FileCBB = FileABB << 2;
    public const ulong FileDBB = FileABB << 3;
    public const ulong FileEBB = FileABB << 4;
    public const ulong FileFBB = FileABB << 5;
    public const ulong FileGBB = FileABB << 6;
    public const ulong FileHBB = FileABB << 7;

    public const ulong Rank1BB = 0xFF;
    public const ulong Rank2BB = Rank1BB << (8 * 1);
    public const ulong Rank3BB = Rank1BB << (8 * 2);
    public const ulong Rank4BB = Rank1BB << (8 * 3);
    public const ulong Rank5BB = Rank1BB << (8 * 4);
    public const ulong Rank6BB = Rank1BB << (8 * 5);
    public const ulong Rank7BB = Rank1BB << (8 * 6);
    public const ulong Rank8BB = Rank1BB << (8 * 7);

    public static ulong SquareBB(Square s) => 1UL << (byte)s;

    public static bool MoreThanOne(ulong b) => (b & (b - 1)) != 0;

    public static ulong RankBB(Rank r) => Rank1BB << (8 * (byte)r);

    public static ulong RankBB(Square s) => RankBB(Types.RankOf(s));

    public static ulong FileBB(File f) => FileABB << (byte)f;

    public static ulong FileBB(Square s) => FileBB(Types.FileOf(s));

    /// <summary>Sposta un bitboard di uno o due passi in una direzione — <c>shift</c>,
    /// bitboard.h:103-115. Le maschere ~FileH/~FileA evitano che uno spostamento EST/OVEST
    /// "avvolga" da un lato all'altro della scacchiera.</summary>
    public static ulong Shift(ulong b, Direction dir) => dir switch
    {
        Direction.North => b << 8,
        Direction.South => b >> 8,
        (Direction)((int)Direction.North * 2) => b << 16,
        (Direction)((int)Direction.South * 2) => b >> 16,
        Direction.East => (b & ~FileHBB) << 1,
        Direction.West => (b & ~FileABB) >> 1,
        Direction.NorthEast => (b & ~FileHBB) << 9,
        Direction.NorthWest => (b & ~FileABB) << 7,
        Direction.SouthEast => (b & ~FileHBB) >> 7,
        Direction.SouthWest => (b & ~FileABB) >> 9,
        _ => 0,
    };

    /// <summary>Case attaccate da pedoni del colore dato, a partire dalle case nel bitboard —
    /// <c>pawn_attacks_bb&lt;C&gt;</c>, bitboard.h:120-124.</summary>
    public static ulong PawnAttacksBB(Color c, ulong b) => c == Color.White
        ? Shift(b, Direction.NorthWest) | Shift(b, Direction.NorthEast)
        : Shift(b, Direction.SouthWest) | Shift(b, Direction.SouthEast);

    public static ulong PawnSinglePushBB(Color c, ulong b) => Shift(b, Types.PawnPush(c));

    private static readonly ulong[] PawnPairTable = BuildPawnPairTable();

    private static ulong[] BuildPawnPairTable()
    {
        var result = new ulong[Squares.Nb];
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            ulong file = FileBB(s);
            ulong files = file | Shift(file, Direction.East) | Shift(file, Direction.West);
            result[(byte)s] = files & ~(Rank1BB | Rank8BB) & ~SquareBB(s);
        }

        return result;
    }

    /// <summary>Case che possono ospitare un pedone che formi una "coppia di pedoni" con un pedone
    /// su s — bitboard.h:130-144. Geometria indipendente dal colore.</summary>
    public static ulong PawnPairBB(Square s) => PawnPairTable[(byte)s];

    public static int EdgeDistance(File f) => Math.Min((byte)f, (byte)(File.H - (byte)f));

    public static int PopCount(ulong b) => BitOperations.PopCount(b);

    /// <summary>Casa meno significativa di un bitboard non nullo — <c>lsb</c>, bitboard.h:200-231.</summary>
    public static Square Lsb(ulong b) => (Square)BitOperations.TrailingZeroCount(b);

    /// <summary>Casa più significativa di un bitboard non nullo — <c>msb</c>, bitboard.h:234-266.</summary>
    public static Square Msb(ulong b) => (Square)(63 - BitOperations.LeadingZeroCount(b));

    public static ulong LeastSignificantSquareBB(ulong b) => b & (0UL - b);

    /// <summary>Trova e cancella il bit meno significativo — <c>pop_lsb</c>, bitboard.h:276-281.</summary>
    public static Square PopLsb(ref ulong b)
    {
        Square s = Lsb(b);
        b &= b - 1;
        return s;
    }

    /// <summary>Rappresentazione ASCII di un bitboard — <c>Bitboards::pretty</c>, bitboard.cpp:25-42.
    /// Solo per debug/diagnostica, non sul percorso caldo.</summary>
    public static string Pretty(ulong b)
    {
        var sb = new StringBuilder();
        sb.Append("+---+---+---+---+---+---+---+---+\n");
        for (var r = Rank.Rank8; ; r--)
        {
            for (var f = File.A; f <= File.H; f++)
                sb.Append((b & SquareBB(Types.MakeSquare(f, r))) != 0 ? "| X " : "|   ");
            sb.Append("| ").Append(1 + (byte)r).Append('\n');
            sb.Append("+---+---+---+---+---+---+---+---+\n");
            if (r == Rank.Rank1) break;
        }

        sb.Append("  a   b   c   d   e   f   g   h\n");
        return sb.ToString();
    }
}

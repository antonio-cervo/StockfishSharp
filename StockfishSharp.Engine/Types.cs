// Porting reale di Stockfish (https://github.com/official-stockfish/Stockfish, GPLv3) in C#.
// Questo file corrisponde a src/types.h della fonte upstream (commit edb0d9d, 2026-09-05).
// Tradotto in C# idiomatico (enum, struct, costanti) mantenendo nomi/struttura/algoritmi
// dell'originale — porting fedele, non un adattamento come nel motore di ACMyChess.

namespace StockfishSharp.Engine;

public enum Color : byte
{
    White,
    Black,
}

/// <summary>Numero di colori — <c>COLOR_NB</c> nella fonte, tenuto come costante separata (non
/// dentro l'enum) perché usato solo per dimensionare array, mai come valore di Color valido.</summary>
public static class Colors
{
    public const int Nb = 2;
}

/// <summary>Diritti di arrocco come bitmask — <c>CastlingRights</c>, types.h:124-138.</summary>
[Flags]
public enum CastlingRights : byte
{
    None = 0,
    WhiteOo = 1,
    WhiteOoo = WhiteOo << 1,
    BlackOo = WhiteOo << 2,
    BlackOoo = WhiteOo << 3,

    KingSide = WhiteOo | BlackOo,
    QueenSide = WhiteOoo | BlackOoo,
    WhiteCastling = WhiteOo | WhiteOoo,
    BlackCastling = BlackOo | BlackOoo,
    AnyCastling = WhiteCastling | BlackCastling,
}

/// <summary><c>Bound</c>, types.h:140-145 — flag della transposition table.</summary>
public enum Bound : byte
{
    None = 0,
    Upper = 1,
    Lower = 2,
    Exact = Upper | Lower,
}

/// <summary>Tipo di pezzo — <c>PieceType</c>, types.h:206-211. <see cref="AllPieces"/> == 0 ==
/// <see cref="None"/> come nella fonte (usato per indicizzare occupancy "di qualunque pezzo").</summary>
public enum PieceType : byte
{
    None = 0,
    Pawn,
    Knight,
    Bishop,
    Rook,
    Queen,
    King,
    AllPieces = None,
}

public static class PieceTypes
{
    public const int Nb = 8;
}

/// <summary>Pezzo (colore+tipo) — <c>Piece</c>, types.h:213-218. Layout identico alla fonte:
/// i pezzi bianchi occupano 1-6, i neri 9-14 (bit 3 = colore), così <see cref="Types.TypeOf"/>/
/// <see cref="Types.ColorOf"/> restano semplici mascheramenti a bit.</summary>
public enum Piece : byte
{
    None = 0,
    WPawn = PieceType.Pawn,
    WKnight,
    WBishop,
    WRook,
    WQueen,
    WKing,
    BPawn = PieceType.Pawn + 8,
    BKnight,
    BBishop,
    BRook,
    BQueen,
    BKing,
}

public static class Pieces
{
    public const int Nb = 16;
}

/// <summary><c>Value</c> nella fonte è un semplice alias di <c>int</c> (per distinguere
/// concettualmente un punteggio di ricerca da un intero qualunque) — C# non ha un modo pulito di
/// alias-are un tipo primitivo attraverso più file, quindi qui resta <c>int</c> diretto ovunque,
/// stesso approccio già usato in ACMyChess per lo stesso motivo.</summary>
public static class Values
{
    public const int Zero = 0;
    public const int Draw = 0;
    public const int None = 32002;
    public const int Infinite = 32001;

    public const int Mate = 32000;
    public const int MateInMaxPly = Mate - Ply.MaxPly;
    public const int MatedInMaxPly = -MateInMaxPly;

    public const int Tb = MateInMaxPly - 1;
    public const int TbWinInMaxPly = Tb - Ply.MaxPly;
    public const int TbLossInMaxPly = -TbWinInMaxPly;

    public const int Pawn = 208;
    public const int Knight = 781;
    public const int Bishop = 825;
    public const int Rook = 1276;
    public const int Queen = 2538;

    /// <summary><c>PieceValue[PIECE_NB]</c>, types.h:221-223 — indicizzato per <see cref="Piece"/>.</summary>
    public static readonly int[] PieceValue =
    [
        Zero, Pawn, Knight, Bishop, Rook, Queen, Zero, Zero,
        Zero, Pawn, Knight, Bishop, Rook, Queen, Zero, Zero,
    ];

    public static bool IsValid(int value) => value != None;

    public static bool IsWin(int value) => value >= TbWinInMaxPly;

    public static bool IsLoss(int value) => value <= TbLossInMaxPly;

    public static bool IsDecisive(int value) => IsWin(value) || IsLoss(value);

    public static bool IsMate(int value) => value >= MateInMaxPly;

    public static bool IsMated(int value) => value <= MatedInMaxPly;

    public static bool IsMateOrMated(int value) => IsMate(value) || IsMated(value);

    public static int MateIn(int ply) => Mate - ply;

    public static int MatedIn(int ply) => -Mate + ply;
}

/// <summary>Costanti di profondità/ply — types.h:115-116,225-241. <c>Depth</c> è anch'esso un
/// alias di <c>int</c> nella fonte, stesso discorso di <see cref="Values"/>.</summary>
public static class Ply
{
    public const int MaxMoves = 256;
    public const int MaxPly = 246;

    public const int DepthQs = 0;
    public const int DepthUnsearched = -2;
    public const int DepthNone = -3;
}

/// <summary><c>Square</c>, types.h:244-257.</summary>
public enum Square : byte
{
    A1, B1, C1, D1, E1, F1, G1, H1,
    A2, B2, C2, D2, E2, F2, G2, H2,
    A3, B3, C3, D3, E3, F3, G3, H3,
    A4, B4, C4, D4, E4, F4, G4, H4,
    A5, B5, C5, D5, E5, F5, G5, H5,
    A6, B6, C6, D6, E6, F6, G6, H6,
    A7, B7, C7, D7, E7, F7, G7, H7,
    A8, B8, C8, D8, E8, F8, G8, H8,
    None = 64,
}

public static class Squares
{
    public const int Nb = 64;
}

/// <summary><c>Direction</c>, types.h:260-270 — spostamento espresso come delta di indice
/// quadrato (coerente con la numerazione 0=a1..63=h8 sopra).</summary>
public enum Direction : sbyte
{
    North = 8,
    East = 1,
    South = -North,
    West = -East,

    NorthEast = North + East,
    SouthEast = South + East,
    SouthWest = South + West,
    NorthWest = North + West,
}

/// <summary><c>File</c>, types.h:272-282.</summary>
public enum File : byte
{
    A, B, C, D, E, F, G, H,
}

public static class Files
{
    public const int Nb = 8;
}

/// <summary><c>Rank</c>, types.h:284-294.</summary>
public enum Rank : byte
{
    Rank1, Rank2, Rank3, Rank4, Rank5, Rank6, Rank7, Rank8,
}

public static class Ranks
{
    public const int Nb = 8;
}

/// <summary>Funzioni libere di types.h tradotte come metodi statici — mantenute qui invece che
/// come operatori sparsi sui singoli enum per restare vicine all'organizzazione della fonte
/// (types.h le mette tutte insieme subito dopo le enum, righe 358-421).</summary>
public static class Types
{
    public static Color Opposite(Color c) => c == Color.White ? Color.Black : Color.White;

    /// <summary>Riflette a1&lt;-&gt;a8 — <c>flip_rank</c>, types.h:382.</summary>
    public static Square FlipRank(Square s) => (Square)((byte)s ^ (byte)Square.A8);

    /// <summary>Riflette a1&lt;-&gt;h1 — <c>flip_file</c>, types.h:385.</summary>
    public static Square FlipFile(Square s) => (Square)((byte)s ^ (byte)Square.H1);

    /// <summary>Cambia colore al pezzo (bianco&lt;-&gt;nero, stesso tipo) — <c>operator~(Piece)</c>,
    /// types.h:388.</summary>
    public static Piece OppositeColor(Piece pc) => (Piece)((byte)pc ^ 8);

    public static CastlingRights CastlingFor(Color c, CastlingRights cr) =>
        (c == Color.White ? CastlingRights.WhiteCastling : CastlingRights.BlackCastling) & cr;

    public static Square MakeSquare(File f, Rank r) => (Square)(((byte)r << 3) + (byte)f);

    public static Piece MakePiece(Color c, PieceType pt) => (Piece)(((byte)c << 3) + (byte)pt);

    public static PieceType TypeOf(Piece pc) => (PieceType)((byte)pc & 7);

    public static Color ColorOf(Piece pc) => (Color)((byte)pc >> 3);

    public static bool IsOk(Square s) => s <= Square.H8;

    public static File FileOf(Square s) => (File)((byte)s & 7);

    public static Rank RankOf(Square s) => (Rank)((byte)s >> 3);

    /// <summary>Specchia il quadrato per il lato nero (usato per tabelle PSQT/pedoni
    /// simmetriche) — <c>relative_square</c>, types.h:411.</summary>
    public static Square RelativeSquare(Color c, Square s) => (Square)((byte)s ^ ((byte)c * 56));

    public static Rank RelativeRank(Color c, Rank r) => (Rank)((byte)r ^ ((byte)c * 7));

    public static Rank RelativeRank(Color c, Square s) => RelativeRank(c, RankOf(s));

    public static Direction PawnPush(Color c) => c == Color.White ? Direction.North : Direction.South;

    public static Square AddDirection(Square s, Direction d) => (Square)((byte)s + (sbyte)d);

    public static Square SubDirection(Square s, Direction d) => (Square)((byte)s - (sbyte)d);

    /// <summary>Generatore pseudocasuale congruenziale usato per costruire chiavi Zobrist e (nella
    /// generazione dei magic bitboard) semi di ricerca — <c>make_key</c>, types.h:421.</summary>
    public static ulong MakeKey(ulong seed) => (seed * 6364136223846793005UL) + 1442695040888963407UL;
}

/// <summary>Tipo speciale di mossa codificato nei bit 14-15 — <c>MoveType</c>, types.h:424-429.</summary>
public enum MoveType : ushort
{
    Normal = 0,
    Promotion = 1 << 14,
    EnPassant = 2 << 14,
    Castling = 3 << 14,
}

/// <summary>Mossa compattata in 16 bit — <c>Move</c>, types.h:443-492. Layout identico alla fonte:
/// bit 0-5 casa di arrivo, bit 6-11 casa di partenza, bit 12-13 tipo di promozione (Cavallo=0 ..
/// Donna=3), bit 14-15 flag speciale. <see cref="None"/> e <see cref="Null"/> condividono lo
/// stesso trucco della fonte (casa di partenza == casa di arrivo, mai vero per una mossa reale).</summary>
public readonly struct Move : IEquatable<Move>
{
    private readonly ushort _data;

    public Move(ushort data) => _data = data;

    public Move(Square from, Square to) => _data = (ushort)(((byte)from << 6) + (byte)to);

    public static Move Make(MoveType type, Square from, Square to, PieceType promotion = PieceType.Knight) =>
        new((ushort)((ushort)type + (((byte)promotion - (byte)PieceType.Knight) << 12) + ((byte)from << 6) + (byte)to));

    public Square FromSq => (Square)((_data >> 6) & 0x3F);

    public Square ToSq => (Square)(_data & 0x3F);

    public MoveType TypeOf => (MoveType)(_data & (3 << 14));

    public PieceType PromotionType => (PieceType)(((_data >> 12) & 3) + (byte)PieceType.Knight);

    public bool IsOk => _data != None._data && _data != Null._data;

    public static Move Null { get; } = new(65);

    public static Move None { get; } = new(0);

    public ushort Raw => _data;

    public bool Equals(Move other) => _data == other._data;

    public override bool Equals(object? obj) => obj is Move m && Equals(m);

    public override int GetHashCode() => _data;

    public static bool operator ==(Move a, Move b) => a.Equals(b);

    public static bool operator !=(Move a, Move b) => !a.Equals(b);

    public static explicit operator bool(Move m) => m._data != 0;
}

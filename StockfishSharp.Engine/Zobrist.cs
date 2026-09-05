// Corrisponde alla parte Zobrist di src/position.cpp (righe 48-55, 120-163: Position::init()).
// Vedi Types.cs per la nota generale sul porting.

namespace StockfishSharp.Engine;

/// <summary>Chiavi Zobrist per il calcolo incrementale dell'hash di posizione — namespace
/// <c>Zobrist</c> in position.cpp. Non porta ancora le "cuckoo table" per la rilevazione veloce
/// di ripetizione (position.cpp:106-162): servono solo a <c>is_draw</c>/<c>upcoming_repetition</c>,
/// non a perft/do_move/undo_move — rimandate a quando servirà la rilevazione di patta in
/// ricerca.</summary>
public static class Zobrist
{
    // [Piece.Nb, Square.Nb] — indicizzato per pezzo e casa, come la fonte.
    public static readonly ulong[,] Psq = new ulong[PieceSlots.Nb, Squares.Nb];
    public static readonly ulong[] EnPassant = new ulong[Files.Nb];

    // Indicizzato direttamente dal valore bitmask di CastlingRights (0-15), come CASTLING_RIGHT_NB
    // nella fonte — non un array per bit singolo.
    public static readonly ulong[] Castling = new ulong[16];

    public static ulong Side;
    public static ulong NoPawns;

    private static bool _initialized;
    private static readonly object InitLock = new();

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            Init();
            _initialized = true;
        }
    }

    private static void Init()
    {
        var rng = new XorShift64StarRng(1070372);

        foreach (var pc in AllPieces.Values)
            for (var s = Square.A1; s <= Square.H8; s++)
                Psq[(byte)pc, (byte)s] = rng.Next();

        // I pedoni su queste case promuoverebbero — la fonte li azzera perché un pedone bianco
        // non esiste mai su rank8 (o nero su rank1): mantiene l'invarianza "Zobrist::psq[pc][to]
        // è zero" usata in do_move per la casa di arrivo di una promozione.
        for (var f = File.A; f <= File.H; f++)
        {
            Psq[(byte)Piece.WPawn, (byte)Types.MakeSquare(f, Rank.Rank8)] = 0;
            Psq[(byte)Piece.BPawn, (byte)Types.MakeSquare(f, Rank.Rank1)] = 0;
        }

        for (var f = File.A; f <= File.H; f++)
            EnPassant[(byte)f] = rng.Next();

        for (int cr = 0; cr <= (int)CastlingRights.AnyCastling; cr++)
            Castling[cr] = rng.Next();

        Side = rng.Next();
        NoPawns = rng.Next();
    }
}

/// <summary>I 12 pezzi reali (esclude <see cref="Piece.None"/>) nell'ordine della fonte
/// (position.cpp:61-62, array anonimo <c>Pieces[]</c>) — usato per iterare durante
/// l'inizializzazione Zobrist e il calcolo della chiave materiale.</summary>
public static class AllPieces
{
    public static readonly Piece[] Values =
    [
        Piece.WPawn, Piece.WKnight, Piece.WBishop, Piece.WRook, Piece.WQueen, Piece.WKing,
        Piece.BPawn, Piece.BKnight, Piece.BBishop, Piece.BRook, Piece.BQueen, Piece.BKing,
    ];
}

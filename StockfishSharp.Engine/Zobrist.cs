// Corrisponde alla parte Zobrist di src/position.cpp (righe 48-55, 120-163: Position::init()).
// Vedi Types.cs per la nota generale sul porting.

namespace StockfishSharp.Engine;

/// <summary>Chiavi Zobrist per il calcolo incrementale dell'hash di posizione — namespace
/// <c>Zobrist</c> in position.cpp. Include ora anche le "cuckoo table" (l'algoritmo di Marcel van
/// Kervinck, position.cpp:106-162) per <c>Position.UpcomingRepetition</c>.</summary>
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

    // Cuckoo table, position.cpp:111-162: due funzioni hash (H1/H2) e due array paralleli
    // (chiave, mossa) per rilevare in O(1) se una SINGOLA mossa reversibile trasforma una
    // posizione in un'altra — usato da Position.UpcomingRepetition per stabilire se una mossa
    // imminente porterebbe a una ripetizione, senza dover generare/provare ogni mossa.
    public static readonly ulong[] Cuckoo = new ulong[8192];
    public static readonly Move[] CuckooMove = new Move[8192];

    public static int H1(ulong h) => (int)(h & 0x1fff);
    public static int H2(ulong h) => (int)((h >> 16) & 0x1fff);

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

        // Tabelle cuckoo, position.cpp:140-162: per ogni pezzo e ogni coppia di case (s1<s2) tale
        // che il pezzo (non pedone) possa muoversi fra le due su scacchiera vuota (quindi mossa
        // REVERSIBILE — la stessa mossa rigiocata torna alla posizione di partenza), inserisce la
        // chiave XOR delle due case + il cambio di turno nella tabella, con la tecnica dei
        // "cuculi" (se lo slot è occupato, sposta l'occupante nel suo slot alternativo H1/H2, a
        // catena, finché non si libera uno slot vuoto).
        Array.Clear(Cuckoo);
        Array.Fill(CuckooMove, Move.None);
        Attacks.EnsureInitialized();

        foreach (var pc in AllPieces.Values)
        {
            if (Types.TypeOf(pc) == PieceType.Pawn) continue;

            for (var s1 = Square.A1; s1 <= Square.H8; s1++)
            {
                for (var s2 = (Square)((byte)s1 + 1); s2 <= Square.H8; s2++)
                {
                    if ((Attacks.AttacksBb(Types.TypeOf(pc), s1) & Bitboards.SquareBB(s2)) == 0) continue;

                    var move = new Move(s1, s2);
                    ulong key = Psq[(byte)pc, (byte)s1] ^ Psq[(byte)pc, (byte)s2] ^ Side;
                    int i = H1(key);

                    while (true)
                    {
                        (Cuckoo[i], key) = (key, Cuckoo[i]);
                        (CuckooMove[i], move) = (move, CuckooMove[i]);
                        if (move == Move.None) break; // slot vuoto raggiunto
                        i = i == H1(key) ? H2(key) : H1(key); // sposta la "vittima" nell'alternativa
                    }
                }
            }
        }
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

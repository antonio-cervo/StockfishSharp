// Corrisponde a src/attacks.h + src/attacks.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// La fonte reale sceglie tra tre implementazioni delle sliding attacks a seconda della CPU
// (hyperbola quintessence su ARM, la variante AVX2 "dual hyperbola quintessence" su x86 moderni,
// o i classici "fancy magic bitboard" precalcolati altrove) — qui portiamo SOLO la terza, quella
// classica indipendente dalla piattaforma: stesso algoritmo usato da decenni di motori scacchistici,
// niente intrinsechi SIMD da replicare, e più facile da verificare riga per riga. Portiamo anche
// solo il ramo a 64 bit (Is64Bit è sempre vero su .NET moderno): il ramo a 32 bit della fonte
// (attacks.cpp:156-159, index() a due metà) non ha equivalente qui.

namespace StockfishSharp.Engine;

/// <summary>Dati magic bitboard per una singola casa — <c>Magic</c>, attacks.h:145-164 (ramo
/// "fancy magic" classico). A differenza della fonte (una tabella <c>attacks[]</c> condivisa fra
/// tutte le case di uno stesso tipo di pezzo, con offset), qui ogni casa ha il proprio array: più
/// semplice in C#, stesso risultato — il layout di memoria condiviso della fonte è
/// un'ottimizzazione di cache, non parte dell'algoritmo.</summary>
public sealed class Magic
{
    public ulong Mask;
    public ulong[] Attacks = [];
    public ulong Number;
    public int Shift;

    public uint Index(ulong occupied) => (uint)(((occupied & Mask) * Number) >> Shift);

    public ulong AttacksBb(ulong occupied) => Attacks[Index(occupied)];
}

public static class Attacks
{
    // magics[square][pieceType - Bishop] — Bishop=0, Rook=1, stessa indicizzazione della fonte.
    private static readonly Magic[,] Magics = new Magic[Squares.Nb, 2];

    private static readonly ulong[,] PseudoAttacksTable = new ulong[PieceTypes.Nb, Squares.Nb];
    private static readonly ulong[,] PawnPushOrAttacksTable = new ulong[Colors.Nb, Squares.Nb];

    private static readonly ulong[,] LineBB = new ulong[Squares.Nb, Squares.Nb];
    private static readonly ulong[,] BetweenBB = new ulong[Squares.Nb, Squares.Nb];
    private static readonly ulong[,] RayPassBB = new ulong[Squares.Nb, Squares.Nb];

    private static bool _initialized;

    /// <summary>Genera magic bitboard e tabelle derivate — chiamato una sola volta, come
    /// <c>Attacks::init()</c> nella fonte (invocato all'avvio del programma, main.cpp). Qui è
    /// idempotente e thread-safe tramite lock, cosi' non serve un punto di ingresso esplicito
    /// separato: la prima chiamata a una qualunque funzione di questa classe lo attiva.</summary>
    private static readonly object InitLock = new();

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            InitMagics(PieceType.Rook);
            InitMagics(PieceType.Bishop);
            InitPseudoAttacks();
            InitLineBetweenRayPass();
            _initialized = true;
        }
    }

    /// <summary>Bitboard del quadrato di arrivo per un passo dato dal quadrato s, o vuoto se il
    /// passo esce dalla scacchiera — <c>safe_destination</c>, attacks.h:191-195. Il controllo sulla
    /// distanza in colonna (&lt;=2) è quello che scarta un "avvolgimento" orizzontale silenzioso
    /// (es. h1 + un passo di cavaliere che uscirebbe a sinistra ricomparendo a destra).</summary>
    private static ulong SafeDestination(Square s, int step)
    {
        int to = (byte)s + step;
        if (to is < 0 or > 63) return 0;
        var toSq = (Square)to;
        int fileDiff = Math.Abs((byte)Types.FileOf(s) - (byte)Types.FileOf(toSq));
        return fileDiff <= 2 ? Bitboards.SquareBB(toSq) : 0UL;
    }

    private static readonly Direction[] RookDirections = [Direction.North, Direction.South, Direction.East, Direction.West];
    private static readonly Direction[] BishopDirections = [Direction.NorthEast, Direction.SouthEast, Direction.SouthWest, Direction.NorthWest];

    /// <summary>Attacchi di torre/alfiere su scacchiera con occupazione data, fermandosi al primo
    /// pezzo incontrato in ogni direzione (incluso) — <c>sliding_attack</c>, attacks.h:197-211.</summary>
    private static ulong SlidingAttack(PieceType pt, Square sq, ulong occupied)
    {
        ulong attacks = 0;
        foreach (var d in pt == PieceType.Rook ? RookDirections : BishopDirections)
        {
            Square s = sq;
            while (true)
            {
                ulong dest = SafeDestination(s, (sbyte)d);
                if (dest == 0) break;
                attacks |= dest;
                if ((occupied & dest) != 0) break;
                s = Types.AddDirection(s, d);
            }
        }

        return attacks;
    }

    private static readonly int[] KnightSteps = [-17, -15, -10, -6, 6, 10, 15, 17];
    private static readonly int[] KingSteps = [-9, -8, -7, -1, 1, 7, 8, 9];

    private static ulong KnightAttack(Square sq)
    {
        ulong b = 0;
        foreach (int step in KnightSteps) b |= SafeDestination(sq, step);
        return b;
    }

    private static ulong KingAttack(Square sq)
    {
        ulong b = 0;
        foreach (int step in KingSteps) b |= SafeDestination(sq, step);
        return b;
    }

    /// <summary>Genera i magic bitboard per torre o alfiere su tutte le case — <c>init_magics</c>,
    /// attacks.cpp:102-154. Stesso identico algoritmo della fonte: semi PRNG fissi per riga (così
    /// il risultato è deterministico e riproducibile, non serve rigenerarlo a ogni avvio) e ricerca
    /// per tentativi di un magic number che non produca collisioni sull'occupazione rilevante.</summary>
    private static void InitMagics(PieceType pt)
    {
        // Stessi semi della fonte (attacks.cpp:104-105), solo la riga Is64Bit=true (indice 1).
        int[] seeds = pt == PieceType.Rook
            ? [728, 10316, 55013, 32803, 12281, 15100, 16645, 255]
            : [8977, 44560, 54343, 38998, 5731, 95205, 104912, 17020];

        // La fonte usa i semi indicizzati [Is64Bit][rank] con due righe (rook, bishop) diverse —
        // qui separate per chiarezza invece che in un'unica tabella 2D come attacks.cpp:104-105.

        var occupancy = new ulong[4096];
        var reference = new ulong[4096];
        var epoch = new int[4096];
        int cnt = 0;

        for (var s = Square.A1; s <= Square.H8; s++)
        {
            ulong edges = ((Bitboards.Rank1BB | Bitboards.Rank8BB) & ~Bitboards.RankBB(s))
                        | ((Bitboards.FileABB | Bitboards.FileHBB) & ~Bitboards.FileBB(s));

            var m = new Magic();
            ulong attacks = SlidingAttack(pt, s, 0);
            m.Mask = attacks & ~edges;
            m.Shift = 64 - Bitboards.PopCount(m.Mask);

            int size = 0;
            ulong b = 0;
            do
            {
                occupancy[size] = b;
                reference[size] = SlidingAttack(pt, s, b);
                size++;
                b = (b - m.Mask) & m.Mask;
            } while (b != 0);

            m.Attacks = new ulong[size];

            var rng = new XorShift64StarRng((ulong)seeds[(byte)Types.RankOf(s)]);

            for (int i = 0; i < size;)
            {
                // sparse_rand: AND di tre numeri casuali, per ottenere magic number con pochi bit
                // a 1 (statisticamente più efficaci) — PRNG::sparse_rand in misc.h, non ancora
                // portato a parte: qui inlineato perché usato solo qui per ora.
                do
                {
                    m.Number = rng.Next() & rng.Next() & rng.Next();
                } while (Bitboards.PopCount((m.Number * m.Mask) >> 56) < 6);

                cnt++;
                for (i = 0; i < size; i++)
                {
                    uint idx = m.Index(occupancy[i]);
                    if (epoch[idx] < cnt)
                    {
                        epoch[idx] = cnt;
                        m.Attacks[idx] = reference[i];
                    }
                    else if (m.Attacks[idx] != reference[i])
                    {
                        break;
                    }
                }
            }

            Magics[(byte)s, pt - PieceType.Bishop] = m;
        }
    }

    private static void InitPseudoAttacks()
    {
        for (var s1 = Square.A1; s1 <= Square.H8; s1++)
        {
            PseudoAttacksTable[(byte)Color.White, (byte)s1] = Bitboards.PawnAttacksBB(Color.White, Bitboards.SquareBB(s1));
            PseudoAttacksTable[(byte)Color.Black, (byte)s1] = Bitboards.PawnAttacksBB(Color.Black, Bitboards.SquareBB(s1));

            PseudoAttacksTable[(byte)PieceType.King, (byte)s1] = KingAttack(s1);
            PseudoAttacksTable[(byte)PieceType.Knight, (byte)s1] = KnightAttack(s1);

            ulong bishop = SlidingAttack(PieceType.Bishop, s1, 0);
            ulong rook = SlidingAttack(PieceType.Rook, s1, 0);
            PseudoAttacksTable[(byte)PieceType.Bishop, (byte)s1] = bishop;
            PseudoAttacksTable[(byte)PieceType.Rook, (byte)s1] = rook;
            PseudoAttacksTable[(byte)PieceType.Queen, (byte)s1] = bishop | rook;

            PawnPushOrAttacksTable[(byte)Color.White, (byte)s1] =
                Bitboards.PawnSinglePushBB(Color.White, Bitboards.SquareBB(s1)) | PseudoAttacksTable[(byte)Color.White, (byte)s1];
            PawnPushOrAttacksTable[(byte)Color.Black, (byte)s1] =
                Bitboards.PawnSinglePushBB(Color.Black, Bitboards.SquareBB(s1)) | PseudoAttacksTable[(byte)Color.Black, (byte)s1];
        }
    }

    /// <summary>Tabelle di linea/segmento fra due case — <c>Attacks::init</c>, attacks.cpp:173-188.</summary>
    private static void InitLineBetweenRayPass()
    {
        for (var s1 = Square.A1; s1 <= Square.H8; s1++)
        {
            foreach (var pt in new[] { PieceType.Bishop, PieceType.Rook })
            {
                for (var s2 = Square.A1; s2 <= Square.H8; s2++)
                {
                    if ((PseudoAttacksTable[(byte)pt, (byte)s1] & Bitboards.SquareBB(s2)) != 0)
                    {
                        LineBB[(byte)s1, (byte)s2] =
                            (AttacksBb(pt, s1) & AttacksBb(pt, s2)) | Bitboards.SquareBB(s1) | Bitboards.SquareBB(s2);
                        BetweenBB[(byte)s1, (byte)s2] =
                            AttacksBb(pt, s1, Bitboards.SquareBB(s2)) & AttacksBb(pt, s2, Bitboards.SquareBB(s1));
                        RayPassBB[(byte)s1, (byte)s2] =
                            AttacksBb(pt, s1) & (AttacksBb(pt, s2, Bitboards.SquareBB(s1)) | Bitboards.SquareBB(s2));
                    }
                }
            }

            for (var s2 = Square.A1; s2 <= Square.H8; s2++)
                BetweenBB[(byte)s1, (byte)s2] |= Bitboards.SquareBB(s2);
        }
    }

    public static ulong LineOf(Square s1, Square s2) => LineBB[(byte)s1, (byte)s2];

    public static ulong Between(Square s1, Square s2) => BetweenBB[(byte)s1, (byte)s2];

    public static ulong RayPass(Square s1, Square s2) => RayPassBB[(byte)s1, (byte)s2];

    /// <summary>Attacchi "pseudo" (a scacchiera vuota) di un pezzo non pedone — <c>attacks_bb&lt;Pt&gt;
    /// (Square, Color)</c>, attacks.h:276-281, ramo non-pedone. Nome distinto da
    /// <see cref="PawnAttacksBb"/> invece di un overload con parametro <see cref="Color"/> di
    /// default: in C# il letterale 0 converte implicitamente sia a <see cref="Color"/> che a
    /// <c>ulong</c>, rendendo ambigua la chiamata con occupazione 0 usata sotto — nella fonte
    /// questo non succede perché i due casi sono specializzazioni template diverse, risolte a
    /// tempo di compilazione dal parametro <c>Pt</c>.</summary>
    public static ulong AttacksBb(PieceType pt, Square s) => PseudoAttacksTable[(byte)pt, (byte)s];

    /// <summary>Attacchi di un pedone del colore dato, a scacchiera vuota — stesso ramo pedone di
    /// <c>attacks_bb&lt;PAWN&gt;(Square, Color)</c>, attacks.h:276-281.</summary>
    public static ulong PawnAttacksBb(Square s, Color c) => PseudoAttacksTable[(byte)c, (byte)s];

    /// <summary>Attacchi reali dato lo stato di occupazione — <c>attacks_bb&lt;Pt&gt;(Square,
    /// Bitboard)</c>, attacks.h:286-317 (solo il ramo "fancy magic" classico, vedi nota in cima
    /// al file).</summary>
    public static ulong AttacksBb(PieceType pt, Square s, ulong occupied)
    {
        switch (pt)
        {
            case PieceType.Bishop:
            case PieceType.Rook:
                return Magics[(byte)s, pt - PieceType.Bishop].AttacksBb(occupied);
            case PieceType.Queen:
                return AttacksBb(PieceType.Bishop, s, occupied) | AttacksBb(PieceType.Rook, s, occupied);
            default:
                return PseudoAttacksTable[(byte)pt, (byte)s];
        }
    }

    public static ulong AttacksBb(Piece pc, Square s, ulong occupied) =>
        Types.TypeOf(pc) == PieceType.Pawn
            ? PseudoAttacksTable[(byte)Types.ColorOf(pc), (byte)s]
            : AttacksBb(Types.TypeOf(pc), s, occupied);
}

/// <summary>PRNG usato SOLO per generare i magic bitboard — <c>PRNG</c> in misc.h, xorshift64star.
/// Portato qui invece che in un file Misc.cs a parte perché per ora è l'unico uso; se servirà
/// altrove (es. Zobrist in Position) verrà promosso a file proprio.</summary>
internal sealed class XorShift64StarRng
{
    private ulong _s;

    public XorShift64StarRng(ulong seed) => _s = seed;

    /// <summary>xorshift64star — stesso algoritmo di <c>PRNG::rand64</c> in misc.h, verificato
    /// riga per riga contro la fonte.</summary>
    public ulong Next()
    {
        _s ^= _s >> 12;
        _s ^= _s << 25;
        _s ^= _s >> 27;
        return _s * 2685821657736338717UL;
    }
}

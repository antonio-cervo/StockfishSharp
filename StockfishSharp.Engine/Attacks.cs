// Corrisponde a src/attacks.h + src/attacks.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// La fonte reale sceglie tra tre implementazioni delle sliding attacks a seconda della CPU:
// hyperbola quintessence su ARM (non portata, specifica di quella piattaforma), la variante AVX2
// "dual hyperbola quintessence" su x86 moderni, e i classici "fancy magic bitboard" come fallback
// generico. Qui portiamo le ultime DUE, con selezione a runtime (Avx2.IsSupported) invece che a
// tempo di compilazione come nella fonte — stesso spirito (usa il meglio disponibile sulla CPU
// target) ma senza bisogno di build separate. Portiamo anche solo il ramo a 64 bit (Is64Bit è
// sempre vero su .NET moderno): il ramo a 32 bit della fonte (attacks.cpp:156-159, index() a due
// metà) non ha equivalente qui.

using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    /// <summary>Attacchi di torre/alfiere SEMPRE via il percorso magic bitboard classico,
    /// indipendentemente da <see cref="UsingAvx2"/> — usato dal fallback quando l'hardware non ha
    /// AVX2 e dai test che confrontano i due percorsi indipendenti fra loro.</summary>
    public static ulong MagicAttacksBb(PieceType pt, Square s, ulong occupied) =>
        Magics[(byte)s, pt - PieceType.Bishop].AttacksBb(occupied);


    // magics[square][pieceType - Bishop] — Bishop=0, Rook=1, stessa indicizzazione della fonte.
    private static readonly Magic[,] Magics = new Magic[Squares.Nb, 2];

    private static readonly ulong[,] PseudoAttacksTable = new ulong[PieceTypes.Nb, Squares.Nb];
    private static readonly ulong[,] PawnPushOrAttacksTable = new ulong[Colors.Nb, Squares.Nb];

    private static readonly ulong[,] LineBB = new ulong[Squares.Nb, Squares.Nb];
    private static readonly ulong[,] BetweenBB = new ulong[Squares.Nb, Squares.Nb];
    private static readonly ulong[,] RayPassBB = new ulong[Squares.Nb, Squares.Nb];

    private static bool _initialized;

    // --- Dati per la variante AVX2 "dual hyperbola quintessence" — attacks.h:89-141,
    // attacks.cpp:67-97 (make_dual_magics). Vedi BothAttacksBbAvx2 sotto per l'algoritmo.
    private static Vector256<ulong>[] _dualMasks = [];
    private static ulong[] _dualR = [];
    private static ulong[] _dualRr = [];
    private static int[] _dualShift = [];
    private static byte[,] _rankAttacksLookup = new byte[Files.Nb, 64];

    /// <summary>Vero se questo processo usa davvero il percorso AVX2 (hardware disponibile) —
    /// esposto per diagnostica/test, così si può verificare a runtime quale dei due percorsi è
    /// stato scelto senza doverlo dedurre indirettamente.</summary>
    public static bool UsingAvx2 { get; private set; }

    /// <summary>Genera magic bitboard e tabelle derivate — chiamato una sola volta, come
    /// <c>Attacks::init()</c> nella fonte (invocato all'avvio del programma, main.cpp). Qui è
    /// idempotente e thread-safe tramite lock, cosi' non serve un punto di ingresso esplicito
    /// separato: la prima chiamata a una qualunque funzione di questa classe lo attiva. A
    /// differenza della fonte (che sceglie il percorso a tempo di compilazione), qui si generano
    /// SEMPRE entrambe le tabelle (magic classici + dati AVX2): il costo di inizializzazione
    /// (poche centinaia di microsecondi) è trascurabile, ed evita due percorsi di codice diversi
    /// da testare a seconda di come è stato compilato l'assembly.</summary>
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
            InitDualMagics();
            UsingAvx2 = Avx2.IsSupported;
            _initialized = true;
        }
    }

    /// <summary>Maschera di linea (senza includere la casa di partenza) in due direzioni opposte
    /// — <c>line_mask</c>, attacks.cpp:39-51. Usata SOLO per i dati AVX2 sotto: le maschere dei
    /// magic bitboard classici (<see cref="InitMagics"/>) restano quelle della fonte (attacchi a
    /// scacchiera vuota meno i bordi), un calcolo diverso per un algoritmo diverso.</summary>
    private static ulong LineMask(Square sq, Direction d1, Direction d2)
    {
        ulong mask = 0;
        foreach (var d in new[] { d1, d2 })
        {
            Square s = sq;
            while (true)
            {
                ulong dest = SafeDestination(s, (sbyte)d);
                if (dest == 0) break;
                mask |= dest;
                s = Types.AddDirection(s, d);
            }
        }

        return mask;
    }

    private static void InitDualMagics()
    {
        // RankAttacks[file][occ6]: attacco di torre lungo la sola traversa, indicizzato dai 6 bit
        // "interni" dell'occupazione (le case di bordo A/H non influenzano mai se l'attacco arriva
        // fino al bordo) — attacks.cpp:72-78. Il cast a byte scarta deliberatamente la componente
        // verticale che sliding_attack(ROOK, ...) produrrebbe insieme a quella orizzontale (la
        // torre "virtuale" è sulla traversa 1, quindi quella componente cade tutta oltre l'ottavo
        // bit) — stesso trucco della fonte, non un troncamento accidentale.
        for (int file = 0; file < Files.Nb; file++)
            for (int occ6 = 0; occ6 < 64; occ6++)
                _rankAttacksLookup[file, occ6] = (byte)SlidingAttack(PieceType.Rook, (Square)file, (ulong)occ6 << 1);

        _dualMasks = new Vector256<ulong>[Squares.Nb];
        _dualR = new ulong[Squares.Nb];
        _dualRr = new ulong[Squares.Nb];
        _dualShift = new int[Squares.Nb];

        for (var s = Square.A1; s <= Square.H8; s++)
        {
            ulong maskFile = LineMask(s, Direction.North, Direction.South);
            ulong maskDiag = LineMask(s, Direction.NorthEast, Direction.SouthWest);
            const ulong maskNone = 0; // corsia inutilizzata, solo per riempire il quarto lane SIMD — attacks.h:93
            ulong maskAntidiag = LineMask(s, Direction.NorthWest, Direction.SouthEast);

            _dualMasks[(byte)s] = Vector256.Create(maskFile, maskDiag, maskNone, maskAntidiag);
            _dualR[(byte)s] = Bitboards.SquareBB(s) * 2;
            _dualRr[(byte)s] = Bitboards.SquareBB((Square)(63 - (byte)s)) * 2;
            _dualShift[(byte)s] = 8 * (byte)Types.RankOf(s);
        }
    }

    // Maschera di shuffle per invertire l'ordine dei byte DENTRO ciascuna corsia da 64 bit di un
    // Vector256<byte> (quattro inversioni indipendenti in una sola istruzione AVX2) — equivalente
    // a chiamare BinaryPrimitives.ReverseEndianness su ognuno dei 4 ulong della corsia, ma in
    // parallelo. Diversa dalla maschera della fonte (attacks.cpp: la lambda "bswap" dentro
    // both_attacks_bb): quella inverte gli interi 16 byte di ciascuna metà da 128 bit, scambiando
    // DELIBERATAMENTE le due corsie da 64 bit adiacenti (file<->diag, none<->antidiag) — un
    // trucco per ottenere le quattro inversioni indipendenti con un solo shuffle sfruttando il
    // fatto che l'operazione viene applicata due volte (lo scambio si annulla). Qui si preferisce
    // la versione più diretta (nessuno scambio, ogni corsia inverte solo se stessa): stesso
    // risultato matematico, verificato via test, più facile da controllare a occhio.
    private static readonly Vector256<byte> ByteSwapEachLaneMask = Vector256.Create<byte>(
        [7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8,
         7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8]);

    private static Vector256<ulong> ByteSwapEachLane(Vector256<ulong> v) =>
        Avx2.Shuffle(v.AsByte(), ByteSwapEachLaneMask).AsUInt64();

    /// <summary>Attacchi di alfiere e torre calcolati insieme via AVX2 — <c>DualMagic::
    /// both_attacks_bb</c>, attacks.h:109-137. Hyperbola Quintessence su quattro corsie in
    /// parallelo (file, diagonale, [inutilizzata], antidiagonale): la formula per corsia è
    /// <c>((o - r) ^ rev(rev(o) - rr)) &amp; mask</c>, con "o" l'occupazione ristretta alla
    /// maschera di quella corsia; le componenti diagonale+antidiagonale sommate danno l'alfiere,
    /// la componente file sommata (OR, mai sovrapposizione) all'attacco di traversa da tabella dà
    /// la torre. <see cref="EnsureInitialized"/> deve essere già stata chiamata (nessun controllo
    /// qui, stesso pattern del resto della classe: percorso caldo, chiamato per ogni nodo di
    /// ricerca una volta che Position/MoveGen esisteranno).</summary>
    public static (ulong Bishop, ulong Rook) BothAttacksBbAvx2(Square s, ulong occupied)
    {
        var mask = _dualMasks[(byte)s];
        var o = mask & Vector256.Create(occupied);

        var fwd = o - Vector256.Create(_dualR[(byte)s]);
        var rev = ByteSwapEachLane(ByteSwapEachLane(o) - Vector256.Create(_dualRr[(byte)s]));

        var result = (fwd ^ rev) & mask;

        ulong fileAttacks = result.GetElement(0);
        ulong diagAttacks = result.GetElement(1);
        ulong antidiagAttacks = result.GetElement(3);

        int shift = _dualShift[(byte)s];
        int file = (byte)Types.FileOf(s);
        int occ6 = (int)((occupied >> (shift + 1)) & 0x3F);
        ulong rankAttacks = (ulong)_rankAttacksLookup[file, occ6] << shift;

        return (diagAttacks | antidiagAttacks, fileAttacks | rankAttacks);
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
    /// Bitboard)</c>, attacks.h:286-317. Sceglie fra i due percorsi portati (AVX2 se disponibile
    /// sull'hardware corrente, altrimenti i magic bitboard classici) — <see cref="UsingAvx2"/>.</summary>
    public static ulong AttacksBb(PieceType pt, Square s, ulong occupied)
    {
        switch (pt)
        {
            case PieceType.Bishop:
                return UsingAvx2 ? BothAttacksBbAvx2(s, occupied).Bishop : MagicAttacksBb(pt, s, occupied);
            case PieceType.Rook:
                return UsingAvx2 ? BothAttacksBbAvx2(s, occupied).Rook : MagicAttacksBb(pt, s, occupied);
            case PieceType.Queen:
                if (UsingAvx2)
                {
                    var (bishop, rook) = BothAttacksBbAvx2(s, occupied);
                    return bishop | rook;
                }

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

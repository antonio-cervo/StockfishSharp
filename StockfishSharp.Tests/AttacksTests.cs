using StockfishSharp.Engine;
using Xunit;
// "File" esiste sia in StockfishSharp.Engine (colonna della scacchiera) sia in System.IO — qui
// serve sempre il primo, quindi si disambigua una volta sola con un alias invece di qualificare
// ogni uso per esteso.
using File = StockfishSharp.Engine.File;

namespace StockfishSharp.Tests;

/// <summary>Verifiche di correttezza sulle tabelle di attacco/magic bitboard — non ci sono valori
/// "ufficiali" di Stockfish da confrontare qui (sono tabelle derivate internamente), quindi si
/// verificano via proprietà geometriche note a mano e via confronto coi risultati "a forza bruta"
/// (SlidingAttack, calcolato di nuovo qui in modo indipendente da come lo fa Attacks al suo
/// interno) su un campione ampio di occupazioni casuali — se i magic bitboard avessero una
/// collisione non rilevata, questo la troverebbe.</summary>
public class AttacksTests
{
    public AttacksTests() => Attacks.EnsureInitialized();

    [Fact]
    public void KnightOnE4AttacksEightSquares()
    {
        ulong attacks = Attacks.AttacksBb(PieceType.Knight, Square.E4);
        Assert.Equal(8, Bitboards.PopCount(attacks));
    }

    [Fact]
    public void KnightOnA1AttacksTwoSquares()
    {
        ulong attacks = Attacks.AttacksBb(PieceType.Knight, Square.A1);
        Assert.Equal(2, Bitboards.PopCount(attacks));
        Assert.True((attacks & Bitboards.SquareBB(Square.B3)) != 0);
        Assert.True((attacks & Bitboards.SquareBB(Square.C2)) != 0);
    }

    [Fact]
    public void KingOnE4AttacksEightSquares()
    {
        ulong attacks = Attacks.AttacksBb(PieceType.King, Square.E4);
        Assert.Equal(8, Bitboards.PopCount(attacks));
    }

    [Fact]
    public void RookOnEmptyBoardAttacksFourteenSquares()
    {
        ulong attacks = Attacks.AttacksBb(PieceType.Rook, Square.D4, 0UL);
        Assert.Equal(14, Bitboards.PopCount(attacks));
    }

    [Fact]
    public void BishopOnEmptyBoardCenterAttacksThirteenSquares()
    {
        ulong attacks = Attacks.AttacksBb(PieceType.Bishop, Square.D4, 0UL);
        Assert.Equal(13, Bitboards.PopCount(attacks));
    }

    [Fact]
    public void RookStopsAtFirstBlocker()
    {
        // Torre in a1, pedone in a4: la torre deve vedere a2,a3,a4 (si ferma sull'occupante) e
        // tutta la prima traversa, non oltre a4.
        ulong occupied = Bitboards.SquareBB(Square.A4);
        ulong attacks = Attacks.AttacksBb(PieceType.Rook, Square.A1, occupied);
        Assert.True((attacks & Bitboards.SquareBB(Square.A4)) != 0);
        Assert.False((attacks & Bitboards.SquareBB(Square.A5)) != 0);
    }

    [Fact]
    public void PawnAttacksAreDiagonalOnly()
    {
        ulong whiteAttacksFromE4 = Attacks.PawnAttacksBb(Square.E4, Color.White);
        Assert.Equal(2, Bitboards.PopCount(whiteAttacksFromE4));
        Assert.True((whiteAttacksFromE4 & Bitboards.SquareBB(Square.D5)) != 0);
        Assert.True((whiteAttacksFromE4 & Bitboards.SquareBB(Square.F5)) != 0);

        ulong blackAttacksFromE4 = Attacks.PawnAttacksBb(Square.E4, Color.Black);
        Assert.True((blackAttacksFromE4 & Bitboards.SquareBB(Square.D3)) != 0);
        Assert.True((blackAttacksFromE4 & Bitboards.SquareBB(Square.F3)) != 0);
    }

    [Theory]
    [InlineData(PieceType.Rook)]
    [InlineData(PieceType.Bishop)]
    public void MagicAttacksMatchBruteForceOnRandomOccupancies(PieceType pt)
    {
        var rng = new Random(12345);
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            for (int trial = 0; trial < 200; trial++)
            {
                ulong occupied = ((ulong)rng.NextInt64() & (ulong)rng.NextInt64());
                ulong expected = BruteForceSlidingAttack(pt, s, occupied);
                ulong actual = Attacks.AttacksBb(pt, s, occupied);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Avx2AvailableOnThisMachine()
    {
        // Non un requisito del porting (c'è il fallback classico) — ma su QUESTA macchina deve
        // essere vero, altrimenti il confronto sotto non starebbe testando il percorso AVX2 per
        // davvero.
        Assert.True(Attacks.UsingAvx2, "Attesa CPU con AVX2 su questa macchina di sviluppo/CI.");
    }

    [Theory]
    [InlineData(PieceType.Rook)]
    [InlineData(PieceType.Bishop)]
    public void Avx2PathMatchesBruteForceOnRandomOccupancies(PieceType pt)
    {
        Assert.True(Attacks.UsingAvx2);
        var rng = new Random(67890);
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            for (int trial = 0; trial < 200; trial++)
            {
                ulong occupied = (ulong)rng.NextInt64() & (ulong)rng.NextInt64();
                ulong expected = BruteForceSlidingAttack(pt, s, occupied);
                var (bishop, rook) = Attacks.BothAttacksBbAvx2(s, occupied);
                ulong actual = pt == PieceType.Rook ? rook : bishop;
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Avx2PathMatchesClassicMagicPathExactly()
    {
        // Le due implementazioni (AVX2 e magic bitboard classici) sono codice completamente
        // indipendente per lo stesso identico algoritmo concettuale — se il porting AVX2 avesse
        // un bug, questo confronto lo troverebbe anche in occupazioni che il solo confronto con
        // BruteForceSlidingAttack potrebbe non coprire allo stesso modo.
        var rng = new Random(13579);
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            for (int trial = 0; trial < 200; trial++)
            {
                ulong occupied = (ulong)rng.NextInt64() & (ulong)rng.NextInt64();
                var (avx2Bishop, avx2Rook) = Attacks.BothAttacksBbAvx2(s, occupied);
                ulong classicBishop = Attacks.MagicAttacksBb(PieceType.Bishop, s, occupied);
                ulong classicRook = Attacks.MagicAttacksBb(PieceType.Rook, s, occupied);
                Assert.Equal(classicBishop, avx2Bishop);
                Assert.Equal(classicRook, avx2Rook);
            }
        }
    }

    /// <summary>Ricalcolo indipendente delle sliding attacks (non passa per Attacks/Magic), usato
    /// SOLO per verificare il porting sopra — se copiasse la stessa implementazione non
    /// proverebbe nulla.</summary>
    private static ulong BruteForceSlidingAttack(PieceType pt, Square sq, ulong occupied)
    {
        Direction[] dirs = pt == PieceType.Rook
            ? [Direction.North, Direction.South, Direction.East, Direction.West]
            : [Direction.NorthEast, Direction.SouthEast, Direction.SouthWest, Direction.NorthWest];

        ulong attacks = 0;
        foreach (var d in dirs)
        {
            int file = (byte)Types.FileOf(sq);
            int rank = (byte)Types.RankOf(sq);
            int df = d is Direction.NorthEast or Direction.East or Direction.SouthEast ? 1
                   : d is Direction.NorthWest or Direction.West or Direction.SouthWest ? -1 : 0;
            int dr = d is Direction.North or Direction.NorthEast or Direction.NorthWest ? 1
                   : d is Direction.South or Direction.SouthEast or Direction.SouthWest ? -1 : 0;

            int f = file + df, r = rank + dr;
            while (f is >= 0 and < 8 && r is >= 0 and < 8)
            {
                var dest = Types.MakeSquare((File)f, (Rank)r);
                attacks |= Bitboards.SquareBB(dest);
                if ((occupied & Bitboards.SquareBB(dest)) != 0) break;
                f += df;
                r += dr;
            }
        }

        return attacks;
    }
}

// Corrisponde a src/movegen.h + src/movegen.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// Il parametro template Color Us/GenType Type della fonte (specializzazione a tempo di
// compilazione, zero branch a runtime) diventa qui un parametro normale con branch a runtime —
// stessa logica, un po' di overhead in meno ottimizzato dal JIT rispetto a quattro/otto copie
// del codice generate dal compilatore C++. Non portata l'ottimizzazione SIMD AVX-512ICL
// "splat_pawn_moves"/"splat_moves" (movegen.cpp, dietro #if USE_AVX512ICL): micro-ottimizzazione
// di impacchettamento della move-list, non parte della logica scacchistica, e specifica di CPU
// non disponibili qui — portato solo il ramo equivalente con pop_lsb in loop.

namespace StockfishSharp.Engine;

public enum GenType
{
    Captures,
    Quiets,
    Evasions,
    NonEvasions,
    Legal,
}

public static class MoveGen
{
    /// <summary>Genera le mosse pseudo-legali del tipo richiesto (o le legali, per
    /// <see cref="GenType.Legal"/>) e le accoda a <paramref name="moveList"/> — <c>generate&lt;Type&gt;</c>,
    /// movegen.cpp:251-289.</summary>
    public static void Generate(GenType type, Position pos, List<Move> moveList)
    {
        if (type == GenType.Legal)
        {
            GenerateLegal(pos, moveList);
            return;
        }

        Color us = pos.SideToMove;
        if (us == Color.White) GenerateAll(Color.White, type, pos, moveList);
        else GenerateAll(Color.Black, type, pos, moveList);
    }

    /// <summary><c>generate&lt;LEGAL&gt;</c>, movegen.cpp:271-289: genera evasioni o mosse non-evasive
    /// a seconda che il re al tratto sia sotto scacco, poi scarta quelle che risultano illegali
    /// (mosse di pezzi inchiodati, mosse del re verso una casa attaccata, en passant che
    /// scoprirebbe scacco).</summary>
    private static void GenerateLegal(Position pos, List<Move> moveList)
    {
        Color us = pos.SideToMove;
        ulong pinned = pos.BlockersForKing(us) & pos.Pieces(us);
        Square ksq = pos.SquareOf(PieceType.King, us);

        int start = moveList.Count;
        if (pos.Checkers() != 0) GenerateAll(us, GenType.Evasions, pos, moveList);
        else GenerateAll(us, GenType.NonEvasions, pos, moveList);

        for (int i = moveList.Count - 1; i >= start; i--)
        {
            Move m = moveList[i];
            bool needsCheck = (pinned & Bitboards.SquareBB(m.FromSq)) != 0
                            || m.FromSq == ksq
                            || m.TypeOf == MoveType.EnPassant;

            if (needsCheck && !pos.Legal(m))
            {
                // Rimozione O(1): sostituisce con l'ultimo elemento invece di scalare la lista —
                // stesso trucco della fonte (*cur = *(--moveList)), l'ordine delle mosse generate
                // non è mai un requisito (l'ordinamento vero avviene altrove, in fase di ricerca).
                moveList[i] = moveList[^1];
                moveList.RemoveAt(moveList.Count - 1);
            }
        }
    }

    private static void GenerateAll(Color us, GenType type, Position pos, List<Move> moveList)
    {
        var them = Types.Opposite(us);
        Square ksq = pos.SquareOf(PieceType.King, us);

        if (type != GenType.Evasions || !Bitboards.MoreThanOne(pos.Checkers()))
        {
            ulong target = type switch
            {
                GenType.Evasions => Attacks.Between(ksq, Bitboards.Lsb(pos.Checkers())),
                GenType.NonEvasions => ~pos.Pieces(us),
                GenType.Captures => pos.Pieces(them),
                _ => ~pos.Pieces(), // QUIETS
            };

            GeneratePawnMoves(us, type, pos, moveList, target);
            GeneratePieceMoves(us, PieceType.Knight, pos, moveList, target);
            GeneratePieceMoves(us, PieceType.Bishop, pos, moveList, target);
            GeneratePieceMoves(us, PieceType.Rook, pos, moveList, target);
            GeneratePieceMoves(us, PieceType.Queen, pos, moveList, target);
        }

        ulong kingTarget = type == GenType.Evasions ? ~pos.Pieces(us)
            : type switch
            {
                GenType.NonEvasions => ~pos.Pieces(us),
                GenType.Captures => pos.Pieces(them),
                _ => ~pos.Pieces(),
            };
        ulong kingMoves = Attacks.AttacksBb(PieceType.King, ksq) & kingTarget;
        SplatMoves(moveList, ksq, kingMoves);

        if ((type == GenType.Quiets || type == GenType.NonEvasions) && pos.CanCastle(Types.CastlingFor(us, CastlingRights.AnyCastling)))
        {
            foreach (var cr in new[] { Types.CastlingFor(us, CastlingRights.KingSide), Types.CastlingFor(us, CastlingRights.QueenSide) })
            {
                if (cr != CastlingRights.None && !pos.CastlingImpeded(cr) && pos.CanCastle(cr))
                    moveList.Add(Move.Make(MoveType.Castling, ksq, pos.CastlingRookSquareOf(cr)));
            }
        }
    }

    private static void GeneratePieceMoves(Color us, PieceType pt, Position pos, List<Move> moveList, ulong target)
    {
        ulong bb = pos.Pieces(us, pt);
        while (bb != 0)
        {
            Square from = Bitboards.PopLsb(ref bb);
            ulong b = Attacks.AttacksBb(pt, from, pos.Pieces()) & target;
            SplatMoves(moveList, from, b);
        }
    }

    private static void SplatMoves(List<Move> moveList, Square from, ulong toBb)
    {
        while (toBb != 0)
            moveList.Add(new Move(from, Bitboards.PopLsb(ref toBb)));
    }

    private static void SplatPawnMoves(List<Move> moveList, Direction offset, ulong toBb)
    {
        while (toBb != 0)
        {
            Square to = Bitboards.PopLsb(ref toBb);
            moveList.Add(new Move(Types.SubDirection(to, offset), to));
        }
    }

    /// <summary><c>make_promotions&lt;Type, D, Enemy&gt;</c>, movegen.cpp:86-103: aggiunge le
    /// promozioni valide per il tipo di generazione richiesto — sempre la donna; anche
    /// torre/alfiere/cavallo negli stadi che generano "tutto" (EVASIONS/NON_EVASIONS), o quando
    /// la promozione è una cattura verso stadio CAPTURES, o una spinta diritta verso stadio
    /// QUIETS.</summary>
    private static void MakePromotions(GenType type, Direction d, bool enemy, Square to, List<Move> moveList)
    {
        bool all = type is GenType.Evasions or GenType.NonEvasions;
        Square from = Types.SubDirection(to, d);

        if (type == GenType.Captures || all)
            moveList.Add(Move.Make(MoveType.Promotion, from, to, PieceType.Queen));

        if ((type == GenType.Captures && enemy) || (type == GenType.Quiets && !enemy) || all)
        {
            moveList.Add(Move.Make(MoveType.Promotion, from, to, PieceType.Rook));
            moveList.Add(Move.Make(MoveType.Promotion, from, to, PieceType.Bishop));
            moveList.Add(Move.Make(MoveType.Promotion, from, to, PieceType.Knight));
        }
    }

    /// <summary><c>generate_pawn_moves&lt;Us, Type&gt;</c>, movegen.cpp:106-185.</summary>
    private static void GeneratePawnMoves(Color us, GenType type, Position pos, List<Move> moveList, ulong target)
    {
        var them = Types.Opposite(us);
        ulong tRank7BB = us == Color.White ? Bitboards.Rank7BB : Bitboards.Rank2BB;
        ulong tRank3BB = us == Color.White ? Bitboards.Rank3BB : Bitboards.Rank6BB;
        Direction up = Types.PawnPush(us);
        Direction upRight = us == Color.White ? Direction.NorthEast : Direction.SouthWest;
        Direction upLeft = us == Color.White ? Direction.NorthWest : Direction.SouthEast;

        ulong emptySquares = ~pos.Pieces();
        ulong enemies = type == GenType.Evasions ? pos.Checkers() : pos.Pieces(them);

        ulong pawnsOn7 = pos.Pieces(us, PieceType.Pawn) & tRank7BB;
        ulong pawnsNotOn7 = pos.Pieces(us, PieceType.Pawn) & ~tRank7BB;

        // Spinte singole e doppie, niente promozioni
        if (type != GenType.Captures)
        {
            ulong b1 = Bitboards.Shift(pawnsNotOn7, up) & emptySquares;
            ulong b2 = Bitboards.Shift(b1 & tRank3BB, up) & emptySquares;

            if (type == GenType.Evasions) // solo case che bloccano lo scacco
            {
                b1 &= target;
                b2 &= target;
            }

            SplatPawnMoves(moveList, up, b1);
            SplatPawnMoves(moveList, (Direction)((int)up * 2), b2);
        }

        // Promozioni e sotto-promozioni
        if (pawnsOn7 != 0)
        {
            ulong b1 = Bitboards.Shift(pawnsOn7, upRight) & enemies;
            ulong b2 = Bitboards.Shift(pawnsOn7, upLeft) & enemies;
            ulong b3 = Bitboards.Shift(pawnsOn7, up) & emptySquares;

            if (type == GenType.Evasions) b3 &= target;

            while (b1 != 0) MakePromotions(type, upRight, true, Bitboards.PopLsb(ref b1), moveList);
            while (b2 != 0) MakePromotions(type, upLeft, true, Bitboards.PopLsb(ref b2), moveList);
            while (b3 != 0) MakePromotions(type, up, false, Bitboards.PopLsb(ref b3), moveList);
        }

        // Catture standard ed en passant
        if (type is GenType.Captures or GenType.Evasions or GenType.NonEvasions)
        {
            ulong b1 = Bitboards.Shift(pawnsNotOn7, upRight) & enemies;
            ulong b2 = Bitboards.Shift(pawnsNotOn7, upLeft) & enemies;

            SplatPawnMoves(moveList, upRight, b1);
            SplatPawnMoves(moveList, upLeft, b2);

            if (pos.EpSquare != Square.None)
            {
                // Una cattura en passant non può risolvere uno scacco per scoperta (il pedone
                // catturato non è la casa che va bloccata).
                if (type == GenType.Evasions && (target & Bitboards.SquareBB(Types.AddDirection(pos.EpSquare, up))) != 0)
                    return;

                ulong epAttackers = pawnsNotOn7 & Attacks.PawnAttacksBb(pos.EpSquare, them);
                while (epAttackers != 0)
                    moveList.Add(Move.Make(MoveType.EnPassant, Bitboards.PopLsb(ref epAttackers), pos.EpSquare));
            }
        }
    }
}

// Corrisponde a src/nnue/features/half_ka_v2_hm.h+.cpp, full_threats.h+.cpp, pp_3wide.h+.cpp
// della fonte upstream. Vedi ../Types.cs per la nota generale sul porting.
//
// Le tabelle che nella fonte sono "constexpr" (calcolate a tempo di compilazione) diventano qui
// tabelle statiche calcolate al primo uso — stesso schema già seguito in Attacks.cs per i magic
// bitboard e le tabelle di pseudo-attacco.

using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

/// <summary>Feature PSQ classiche (posizione del proprio re + posizione di ogni pezzo, specchiate
/// così che il re sia sempre nelle colonne e-h) — <c>HalfKAv2_hm</c>, half_ka_v2_hm.h+.cpp.</summary>
public static class HalfKAv2Hm
{
    private const int PsNone = 0;
    private const int PsWPawn = 0;
    private const int PsBPawn = 1 * 64;
    private const int PsWKnight = 2 * 64;
    private const int PsBKnight = 3 * 64;
    private const int PsWBishop = 4 * 64;
    private const int PsBBishop = 5 * 64;
    private const int PsWRook = 6 * 64;
    private const int PsBRook = 7 * 64;
    private const int PsWQueen = 8 * 64;
    private const int PsBQueen = 9 * 64;
    private const int PsKing = 10 * 64;
    private const int PsNb = 11 * 64;

    public const int Dimensions = 64 * PsNb / 2; // 22528

    // [perspective][piece] — half_ka_v2_hm.h:51-57.
    private static readonly int[,] PieceSquareIndex =
    {
        { PsNone, PsWPawn, PsWKnight, PsWBishop, PsWRook, PsWQueen, PsKing, PsNone,
          PsNone, PsBPawn, PsBKnight, PsBBishop, PsBRook, PsBQueen, PsKing, PsNone },
        { PsNone, PsBPawn, PsBKnight, PsBBishop, PsBRook, PsBQueen, PsKing, PsNone,
          PsNone, PsWPawn, PsWKnight, PsWBishop, PsWRook, PsWQueen, PsKing, PsNone },
    };

    // King bucket per casa del re (simmetrico per colonna, 4 valori distinti per traversa) —
    // half_ka_v2_hm.h:69-78.
    private static readonly int[] KingBuckets =
    {
        28 * PsNb, 29 * PsNb, 30 * PsNb, 31 * PsNb, 31 * PsNb, 30 * PsNb, 29 * PsNb, 28 * PsNb,
        24 * PsNb, 25 * PsNb, 26 * PsNb, 27 * PsNb, 27 * PsNb, 26 * PsNb, 25 * PsNb, 24 * PsNb,
        20 * PsNb, 21 * PsNb, 22 * PsNb, 23 * PsNb, 23 * PsNb, 22 * PsNb, 21 * PsNb, 20 * PsNb,
        16 * PsNb, 17 * PsNb, 18 * PsNb, 19 * PsNb, 19 * PsNb, 18 * PsNb, 17 * PsNb, 16 * PsNb,
        12 * PsNb, 13 * PsNb, 14 * PsNb, 15 * PsNb, 15 * PsNb, 14 * PsNb, 13 * PsNb, 12 * PsNb,
         8 * PsNb,  9 * PsNb, 10 * PsNb, 11 * PsNb, 11 * PsNb, 10 * PsNb,  9 * PsNb,  8 * PsNb,
         4 * PsNb,  5 * PsNb,  6 * PsNb,  7 * PsNb,  7 * PsNb,  6 * PsNb,  5 * PsNb,  4 * PsNb,
         0 * PsNb,  1 * PsNb,  2 * PsNb,  3 * PsNb,  3 * PsNb,  2 * PsNb,  1 * PsNb,  0 * PsNb,
    };

    // Orientamento: riflette la casa secondo la prospettiva (rotazione di 180° per il nero) —
    // half_ka_v2_hm.h:83-92. H1/A1 come "maschera" di riflessione orizzontale/nessuna, non come
    // case letterali: sono usate solo come operando XOR più sotto. Tabella distinta da
    // FullThreats.OrientTbl: pattern diverso (qui per colonna assoluta, là per lato della
    // scacchiera) nonostante il nome uguale nella fonte.
    private static readonly int[] OrientTbl =
    {
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
        (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1,
    };

    /// <summary>Indice della feature per un pezzo su una casa, data la prospettiva e la casa del
    /// re — <c>make_index</c>, half_ka_v2_hm.cpp:81-85.</summary>
    public static int MakeIndex(Color perspective, Square s, Piece pc, Square ksq)
    {
        int flip = 56 * (byte)perspective;
        return ((byte)s ^ OrientTbl[(byte)ksq] ^ flip) + PieceSquareIndex[(byte)perspective, (byte)pc] + KingBuckets[(byte)ksq ^ flip];
    }

    /// <summary>Tutte le feature attive per una prospettiva — non nella fonte come funzione a sé
    /// (lì si usa solo <c>append_changed_indices</c> incrementale, mai riletto da zero perché
    /// c'è sempre una Finny table cache) — qui serve per il ricalcolo completo (N3, nessuna
    /// cache).</summary>
    public static void AppendActiveIndices(Color perspective, Position pos, List<int> active)
    {
        Square ksq = pos.SquareOf(PieceType.King, perspective);
        for (var s = Square.A1; s <= Square.H8; s++)
        {
            Piece pc = pos.PieceOn(s);
            if (pc == Piece.None) continue;
            active.Add(MakeIndex(perspective, s, pc, ksq));
        }
    }

    /// <summary>Vero se il cambiamento richiede un refresh completo (il re si è mosso) —
    /// <c>requires_refresh</c>, half_ka_v2_hm.cpp:102-104.</summary>
    public static bool RequiresRefresh(Piece movedPiece, Color perspective) =>
        movedPiece == Types.MakePiece(perspective, PieceType.King);

    /// <summary><c>append_changed_indices</c>, half_ka_v2_hm.cpp:89-100 — le feature che cambiano
    /// a causa di un singolo DirtyPiece (N9, aggiornamento incrementale).</summary>
    public static void AppendChangedIndices(Color perspective, Square ksq, DirtyPiece diff, List<int> removed, List<int> added)
    {
        removed.Add(MakeIndex(perspective, diff.From, diff.Pc, ksq));
        if (diff.To != Square.None)
            added.Add(MakeIndex(perspective, diff.To, diff.Pc, ksq));

        if (diff.RemoveSq != Square.None)
            removed.Add(MakeIndex(perspective, diff.RemoveSq, diff.RemovePc, ksq));

        if (diff.AddSq != Square.None)
            added.Add(MakeIndex(perspective, diff.AddSq, diff.AddPc, ksq));
    }
}

/// <summary>Feature "chi minaccia chi" — <c>FullThreats</c>, full_threats.h+.cpp. La più grande e
/// intricata delle tre: offset cumulativi per pezzo/casa-di-partenza (quante case può raggiungere
/// quel pezzo da lì), più due tabelle di lookup che insieme danno l'indice finale.</summary>
public static class FullThreats
{
    public const int Dimensions = ThreatDimensions; // 59808

    // Pedoni: solo 4 bersagli validi (cavallo/torre, amico/nemico) — le coppie pedone-pedone le
    // gestisce PP_3Wide. Indicizzato per Piece (0..15, gap su None/7/8/15 come sempre).
    private static readonly int[] NumValidTargets =
    [
        0, 4, 10, 8, 8, 10, 0, 0,
        0, 4, 10, 8, 8, 10, 0, 0,
    ];

    // map[attaccante-1][attaccato-1] — full_threats.h:58-65. -1 = combinazione esclusa.
    private static readonly int[,] Map =
    {
        { -1, 0, -1, 1, -1, -1 },
        { 0, 1, 2, 3, 4, -1 },
        { 0, 1, 2, 3, -1, -1 },
        { 0, 1, 2, 3, -1, -1 },
        { 0, 1, 2, 3, 4, -1 },
        { -1, -1, -1, -1, -1, -1 },
    };

    // internal (non private): la fonte la dichiara pubblica dentro la classe FullThreats proprio
    // perché PP_3Wide::make_index la riusa direttamente (pp_3wide.cpp:51,
    // "FullThreats::OrientTBL[ksq]") invece di avere una propria copia — vedi Pp3Wide sotto.
    internal static readonly int[] OrientTbl =
    {
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
        (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.A1, (int)Square.H1, (int)Square.H1, (int)Square.H1, (int)Square.H1,
    };

    // offsets[piece][from] = quante case raggiungibili ci sono state PRIMA di "from" per quel
    // pezzo — full_threats.cpp:116-150 (init_threat_offsets), qui a runtime invece che constexpr.
    private static readonly int[,] Offsets = new int[16, 64];

    // cumulativePieceOffset[piece] = totale case raggiungibili da quel pezzo su tutta la
    // scacchiera; cumulativeOffset[piece] = dove inizia il blocco di quel pezzo nello spazio a
    // 59.808 dimensioni.
    private static readonly int[] CumulativePieceOffset = new int[16];
    private static readonly int[] CumulativeOffset = new int[16];

    // index_lut2[piece][from][to] = indice compresso (popcount) di "to" fra le case raggiungibili
    // da "from" per quel pezzo — full_threats.cpp:46-114.
    private static readonly int[,,] IndexLut2 = new int[16, 64, 64];

    // index_lut1[attaccante][attaccato][from<to] — full_threats.cpp:156-181.
    private static readonly int[,,] IndexLut1 = new int[16, 16, 2];

    // full_threats.cpp:41-44 (AllPieces) — SOLO i 12 pezzi reali. Iterare grezzamente su 0..15
    // (come in un primo tentativo) incontra anche i due valori "buco" del pacchettamento
    // colore*8+tipo (Piece=7 e Piece=15, nessun PieceType valido corrispondente), che non sono
    // né None né King e quindi sfuggivano al filtro — mandavano Map[6, ...] fuori indice.
    private static readonly Piece[] AllPieces =
    [
        Piece.WPawn, Piece.WKnight, Piece.WBishop, Piece.WRook, Piece.WQueen, Piece.WKing,
        Piece.BPawn, Piece.BKnight, Piece.BBishop, Piece.BRook, Piece.BQueen, Piece.BKing,
    ];

    static FullThreats()
    {
        Attacks.EnsureInitialized();

        int cumulative = 0;
        foreach (Piece piece in AllPieces)
        {
            int pieceIdx = (byte)piece;
            var pt = Types.TypeOf(piece);

            int pieceCum = 0;
            for (var from = Square.A1; from <= Square.H8; from++)
            {
                Offsets[pieceIdx, (byte)from] = pieceCum;

                ulong attacks;
                if (pt == PieceType.Pawn)
                {
                    if (from < Square.A2 || from > Square.H7) continue;
                    var c = Types.ColorOf(piece);
                    attacks = Attacks.PawnAttacksBb(from, c);
                }
                else
                {
                    attacks = Attacks.AttacksBb(pt, from);
                }

                int cnt = Bitboards.PopCount(attacks);
                ulong bb = attacks;
                int idx = 0;
                for (var to = Square.A1; to <= Square.H8; to++)
                {
                    if ((bb & Bitboards.SquareBB(to)) != 0)
                    {
                        IndexLut2[pieceIdx, (byte)from, (byte)to] = idx;
                        idx++;
                    }
                }

                pieceCum += cnt;
            }

            CumulativePieceOffset[pieceIdx] = pieceCum;
            CumulativeOffset[pieceIdx] = cumulative;
            cumulative += NumValidTargets[pieceIdx] * pieceCum;
        }

        foreach (Piece attacker in AllPieces)
        {
            int attackerIdx = (byte)attacker;
            var attackerType = Types.TypeOf(attacker);

            foreach (Piece attacked in AllPieces)
            {
                int attackedIdx = (byte)attacked;
                var attackedType = Types.TypeOf(attacked);

                bool enemy = (attackerIdx ^ attackedIdx) == 8;
                int map = Map[(byte)attackerType - 1, (byte)attackedType - 1];
                bool semiExcluded = attackerType == attackedType && (enemy || attackerType != PieceType.Pawn);

                var attackedColor = Types.ColorOf(attacked);
                int feature = CumulativeOffset[attackerIdx]
                    + (((int)attackedColor * (NumValidTargets[attackerIdx] / 2)) + map) * CumulativePieceOffset[attackerIdx];

                bool excluded = map < 0;
                IndexLut1[attackerIdx, attackedIdx, 0] = excluded ? Dimensions : feature;
                IndexLut1[attackerIdx, attackedIdx, 1] = excluded || semiExcluded ? Dimensions : feature;
            }
        }
    }

    /// <summary><c>make_index</c>, full_threats.cpp:192-205.</summary>
    public static int MakeIndex(Color perspective, Piece attacker, Square from, Square to, Piece attacked, Square ksq)
    {
        int orientation = OrientTbl[(byte)ksq] ^ (56 * (byte)perspective);
        int fromOriented = (byte)from ^ orientation;
        int toOriented = (byte)to ^ orientation;

        int swap = 8 * (byte)perspective;
        int attackerOriented = (byte)attacker ^ swap;
        int attackedOriented = (byte)attacked ^ swap;

        return IndexLut1[attackerOriented, attackedOriented, fromOriented < toOriented ? 1 : 0]
             + Offsets[attackerOriented, fromOriented]
             + IndexLut2[attackerOriented, fromOriented, toOriented];
    }

    /// <summary><c>append_active_indices</c>, full_threats.cpp:209-257.</summary>
    public static void AppendActiveIndices(Color perspective, Position pos, List<int> active)
    {
        Square ksq = pos.SquareOf(PieceType.King, perspective);
        ulong occupied = pos.Pieces();
        ulong pawnTargets = pos.Pieces(PieceType.Knight, PieceType.Rook);
        ulong minorSliderTargets = pos.Pieces(PieceType.Pawn, PieceType.Knight) | pos.Pieces(PieceType.Bishop, PieceType.Rook);
        ulong queenTargets = minorSliderTargets | pos.Pieces(PieceType.Queen);

        void ProcessPawnAttacks(Color c, Direction attkDir)
        {
            ulong cPawns = pos.Pieces(c, PieceType.Pawn);
            ulong attacks = Bitboards.Shift(cPawns, attkDir) & pawnTargets;
            while (attacks != 0)
            {
                Square to = Bitboards.PopLsb(ref attacks);
                Square from = Types.SubDirection(to, attkDir);
                Piece attacked = pos.PieceOn(to);
                Piece attacker = Types.MakePiece(c, PieceType.Pawn);
                int index = MakeIndex(perspective, attacker, from, to, attacked, ksq);
                if (index < Dimensions) active.Add(index);
            }
        }

        ProcessPawnAttacks(Color.White, Direction.NorthEast);
        ProcessPawnAttacks(Color.White, Direction.NorthWest);
        ProcessPawnAttacks(Color.Black, Direction.SouthWest);
        ProcessPawnAttacks(Color.Black, Direction.SouthEast);

        foreach (var c in new[] { Color.White, Color.Black })
        {
            for (var pt = PieceType.Knight; pt < PieceType.King; pt++)
            {
                Piece attacker = Types.MakePiece(c, pt);
                ulong bb = pos.Pieces(c, pt);
                ulong targets = pt is PieceType.Knight or PieceType.Queen ? queenTargets : minorSliderTargets;
                while (bb != 0)
                {
                    Square from = Bitboards.PopLsb(ref bb);
                    ulong attacks = Attacks.AttacksBb(pt, from, occupied) & targets;
                    while (attacks != 0)
                    {
                        Square to = Bitboards.PopLsb(ref attacks);
                        Piece attacked = pos.PieceOn(to);
                        int index = MakeIndex(perspective, attacker, from, to, attacked, ksq);
                        if (index < Dimensions) active.Add(index);
                    }
                }
            }
        }
    }

    /// <summary><c>append_changed_indices</c>, full_threats.cpp:261-285 — itera direttamente la
    /// lista di <see cref="DirtyThreat"/> raccolta da <c>Position.UpdatePieceThreats</c> durante la
    /// mossa: ciascuno diventa un indice aggiunto o rimosso a seconda del suo flag <c>Add</c>.</summary>
    public static void AppendChangedIndices(Color perspective, Square ksq, List<DirtyThreat> dirtyThreats, List<int> removed, List<int> added)
    {
        foreach (var dirty in dirtyThreats)
        {
            int index = MakeIndex(perspective, dirty.Pc, dirty.PcSq, dirty.ThreatenedSq, dirty.ThreatenedPc, ksq);
            if (index >= Dimensions) continue; // combinazione esclusa dalla feature — vedi AppendActiveIndices

            (dirty.Add ? added : removed).Add(index);
        }
    }
}

/// <summary>Feature "coppie di pedoni" — <c>PP_3Wide</c>, pp_3wide.h+.cpp.</summary>
public static class Pp3Wide
{
    private const int PawnIds = 2 * 48; // 96
    public const int Dimensions = PawnIds * (PawnIds - 1) / 2; // 4560
    public const int IndexBase = FullThreats.Dimensions; // 59808 — concatenate dopo le minacce

    private static int MakePawnId(Color color, Square square) => (48 * (byte)color) + (byte)square - (byte)Square.A2;

    /// <summary><c>make_index</c>, pp_3wide.cpp:49-67.</summary>
    public static int MakeIndex(Color perspective, Color color, Square from, Square to, Color pairedColor, Square ksq)
    {
        // Riusa FullThreats.OrientTbl invece di una copia propria — pp_3wide.cpp:51 fa lo stesso
        // nella fonte (FullThreats::OrientTBL[ksq]), la tabella è pubblica lì apposta per questo.
        int orientation = FullThreats.OrientTbl[(byte)ksq] ^ (56 * (byte)perspective);
        int fromOriented = (byte)from ^ orientation;
        int toOriented = (byte)to ^ orientation;

        var colorOriented = (Color)((byte)color ^ (byte)perspective);
        var pairedColorOriented = (Color)((byte)pairedColor ^ (byte)perspective);

        int idA = MakePawnId(colorOriented, (Square)fromOriented);
        int idB = MakePawnId(pairedColorOriented, (Square)toOriented);
        int hi = Math.Max(idA, idB);
        int lo = Math.Min(idA, idB);

        return (hi * (hi - 1) / 2) + lo + IndexBase;
    }

    /// <summary><c>append_active_indices</c>, pp_3wide.cpp:69-93.</summary>
    public static void AppendActiveIndices(Color perspective, Position pos, List<int> active)
    {
        Square ksq = pos.SquareOf(PieceType.King, perspective);
        ulong white = pos.Pieces(Color.White, PieceType.Pawn);
        ulong black = pos.Pieces(Color.Black, PieceType.Pawn);

        ulong bb = white;
        while (bb != 0)
        {
            Square from = Bitboards.PopLsb(ref bb);
            ulong band = Bitboards.PawnPairBB(from);
            ulong ww = band & bb;
            while (ww != 0) active.Add(MakeIndex(perspective, Color.White, from, Bitboards.PopLsb(ref ww), Color.White, ksq));
            ulong wb = band & black;
            while (wb != 0) active.Add(MakeIndex(perspective, Color.White, from, Bitboards.PopLsb(ref wb), Color.Black, ksq));
        }

        bb = black;
        while (bb != 0)
        {
            Square from = Bitboards.PopLsb(ref bb);
            ulong band = Bitboards.PawnPairBB(from);
            ulong bbk = band & bb;
            while (bbk != 0) active.Add(MakeIndex(perspective, Color.Black, from, Bitboards.PopLsb(ref bbk), Color.Black, ksq));
        }
    }

    /// <summary><c>append_changed_indices</c> (ramo scalare), pp_3wide.cpp:144-169 — a differenza
    /// delle altre due feature non itera un dirty già pronto: confronta le bitboard pedoni
    /// prima/dopo per trovare quali sono apparsi/spariti, poi genera le coppie con i loro vicini
    /// (<c>Bitboards.PawnPairBB</c>) che erano già presenti o sono anch'essi apparsi/spariti nello
    /// stesso momento.</summary>
    public static void AppendChangedIndices(Color perspective, Square ksq, DirtyPawnPairs diff, List<int> removed, List<int> added)
    {
        ulong whiteBefore = diff.Before[(byte)Color.White];
        ulong blackBefore = diff.Before[(byte)Color.Black];
        ulong whiteAfter = diff.After[(byte)Color.White];
        ulong blackAfter = diff.After[(byte)Color.Black];

        if (whiteBefore == whiteAfter && blackBefore == blackAfter) return;

        void Generate(ulong updatedW, ulong updatedB, ulong pawnsW, ulong pawnsB, List<int> outList)
        {
            ulong unchanged = (pawnsW | pawnsB) & ~(updatedW | updatedB);
            ulong updated = updatedW | updatedB;

            while (updated != 0)
            {
                Square a = Bitboards.PopLsb(ref updated);
                // "unchanged | updated" (il residuo DOPO aver estratto "a", non il valore
                // originale intatto) — evita di generare la stessa coppia due volte quando
                // ENTRAMBI i pedoni della coppia sono "aggiornati": una volta rimosso "a" da
                // "updated", l'altro pedone della coppia già processato non vi compare più, quindi
                // la coppia si genera una sola volta (dal lato che viene estratto per primo).
                ulong mask = Bitboards.PawnPairBB(a) & (unchanged | updated);
                Color aCol = (pawnsB & Bitboards.SquareBB(a)) != 0 ? Color.Black : Color.White;

                ulong pb = pawnsB & mask;
                while (pb != 0) outList.Add(MakeIndex(perspective, aCol, a, Bitboards.PopLsb(ref pb), Color.Black, ksq));

                ulong pw = pawnsW & mask;
                while (pw != 0) outList.Add(MakeIndex(perspective, aCol, a, Bitboards.PopLsb(ref pw), Color.White, ksq));
            }
        }

        Generate(whiteAfter & ~whiteBefore, blackAfter & ~blackBefore, whiteAfter, blackAfter, added);
        Generate(whiteBefore & ~whiteAfter, blackBefore & ~blackAfter, whiteBefore, blackBefore, removed);
    }
}

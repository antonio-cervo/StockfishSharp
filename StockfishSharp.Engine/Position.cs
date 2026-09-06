// Corrisponde a src/position.h + src/position.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// Semplificazioni deliberate rispetto alla fonte, tutte per rimandare a fasi successive del
// porting (vedi docs/porting-plan.md), non per correttezza dell'algoritmo di base:
// - NNUE: i tre meccanismi "dirty" (DirtyThreat.cs/DirtyPiece.cs/DirtyPawnPairs.cs) sono ora
//   tracciati da DoMove/DoCastling/PutPiece/RemovePiece/MovePiece/SwapPiece (parametri opzionali,
//   null di default — nessun costo per i chiamanti esistenti, che non li passano ancora). Manca
//   ancora chi li CONSUMA: l'aggiornamento incrementale vero dell'accumulatore NNUE (N9).
// - Transposition table / SharedHistories (prefetch in do_move): assenti — servono solo a
//   prefetchare la cache prima che la ricerca ne abbia bisogno, non alla correttezza.
// - Static Exchange Evaluation (see_ge), is_draw/is_repetition/upcoming_repetition (cuckoo table),
//   pos_is_ok(), flip(): non ancora portati — non servono a perft (che conta TUTTI i nodi foglia,
//   ripetizioni comprese, fino alla profondità richiesta) né a do_move/undo_move di base.
// - FEN: parsing per token di stringa invece che via std::istringstream carattere per carattere —
//   stessa validazione, meccanica di parsing diversa (C# non ha un equivalente diretto pulito).
// - Errori di parsing FEN: eccezione (PositionSetException) invece di
//   std::optional<PositionSetError> restituito — idiomatico C#, la logica di validazione è la
//   stessa fonte per fonte.

using System.Globalization;
using System.Text;

namespace StockfishSharp.Engine;

public sealed class PositionSetException(string message) : Exception(message);

public sealed class Position
{
    private readonly Piece[] _board = new Piece[Squares.Nb];
    private readonly ulong[] _byTypeBB = new ulong[PieceTypes.Nb];
    private readonly ulong[] _byColorBB = new ulong[Colors.Nb];
    private readonly int[] _pieceCount = new int[PieceSlots.Nb];
    private readonly CastlingRights[] _castlingRightsMask = new CastlingRights[Squares.Nb];
    private readonly Square[] _castlingRookSquare = new Square[16];
    private readonly ulong[] _castlingPath = new ulong[16];

    private StateInfo _st = null!;
    private int _gamePly;
    private Color _sideToMove;
    private bool _chess960;

    public static void Init() => Zobrist.EnsureInitialized();

    // --- Rappresentazione della posizione — position.h:99-116 ---

    public ulong Pieces() => _byTypeBB[(byte)PieceType.AllPieces];

    public ulong Pieces(PieceType pt) => _byTypeBB[(byte)pt];

    public ulong Pieces(PieceType pt1, PieceType pt2) => _byTypeBB[(byte)pt1] | _byTypeBB[(byte)pt2];

    public ulong Pieces(Color c) => _byColorBB[(byte)c];

    public ulong Pieces(Color c, PieceType pt) => _byColorBB[(byte)c] & _byTypeBB[(byte)pt];

    public ulong Pieces(Color c, PieceType pt1, PieceType pt2) =>
        _byColorBB[(byte)c] & (_byTypeBB[(byte)pt1] | _byTypeBB[(byte)pt2]);

    public Piece PieceOn(Square s) => _board[(byte)s];

    public bool Empty(Square s) => PieceOn(s) == Piece.None;

    public Square EpSquare => _st.EpSquare;

    public int Count(PieceType pt, Color c) => _pieceCount[(byte)Types.MakePiece(c, pt)];

    public int Count(PieceType pt) => Count(pt, Color.White) + Count(pt, Color.Black);

    /// <summary>Casa dell'unico pezzo del tipo/colore dato — <c>Position::square&lt;Pt&gt;</c>,
    /// position.h:273-277. Assume (come la fonte) che ce ne sia esattamente uno: usato per il re,
    /// mai per pezzi che possono essere multipli.</summary>
    public Square SquareOf(PieceType pt, Color c) => Bitboards.Lsb(Pieces(c, pt));

    // --- Arrocco — position.h:117-120 ---

    public bool CanCastle(CastlingRights cr) => (_st.CastlingRights & cr) != 0;

    public bool CastlingImpeded(CastlingRights cr) => (Pieces() & _castlingPath[(int)cr]) != 0;

    public Square CastlingRookSquareOf(CastlingRights cr) => _castlingRookSquare[(int)cr];

    // --- Scacco — position.h:122-134 ---

    public ulong Checkers() => _st.CheckersBB;

    public ulong BlockersForKing(Color c) => _st.BlockersForKing[(byte)c];

    public ulong CheckSquaresOf(PieceType pt) => _st.CheckSquares[(byte)pt];

    /// <summary>Case attaccate da tutti i pezzi di tipo <paramref name="pt"/> e colore
    /// <paramref name="c"/> — <c>Position::attacks_by</c>, position.h:296-309. Per i pedoni usa lo
    /// spostamento di massa della bitboard (niente ciclo per pezzo); per gli altri tipi itera ogni
    /// pezzo e unisce i suoi attacchi (con l'occupazione reale, per gli scorrevoli).</summary>
    public ulong AttacksBy(PieceType pt, Color c)
    {
        if (pt == PieceType.Pawn)
        {
            ulong pawns = Pieces(c, PieceType.Pawn);
            return c == Color.White
                ? Bitboards.Shift(pawns, Direction.NorthWest) | Bitboards.Shift(pawns, Direction.NorthEast)
                : Bitboards.Shift(pawns, Direction.SouthWest) | Bitboards.Shift(pawns, Direction.SouthEast);
        }

        ulong threats = 0;
        ulong attackers = Pieces(c, pt);
        while (attackers != 0)
        {
            Square s = Bitboards.PopLsb(ref attackers);
            threats |= Attacks.AttacksBb(pt, s, Pieces());
        }

        return threats;
    }

    /// <summary><c>can_slider_threat</c>, position.cpp:1189-1191 — una regina è minacciata da uno
    /// slider SOLO se lo slider è a sua volta una regina (limita la combinatoria della feature
    /// FullThreats); ogni altro bersaglio può essere minacciato da qualunque slider che lo veda.</summary>
    private static bool CanSliderThreat(Piece threatenedPc, Piece slider) =>
        Types.TypeOf(threatenedPc) != PieceType.Queen || Types.TypeOf(slider) == PieceType.Queen;

    /// <summary>Porting fedele di <c>Position::update_piece_threats&lt;ComputeRay&gt;</c>,
    /// position.cpp:1193-1291 (solo il ramo scalare — la variante AVX-512ICL con
    /// write_multiple_dirties, dietro #ifdef USE_AVX512ICL, non è portata, stesso trattamento di
    /// ogni altro codice SIMD-specifico in questo porting). Popola <paramref name="dts"/> con i
    /// cambiamenti di minaccia causati dall'aggiunta (<paramref name="putPiece"/>=true) o dalla
    /// rimozione (=false) del pezzo <paramref name="pc"/> sulla casa <paramref name="s"/> — sia le
    /// minacce dirette che <paramref name="pc"/> genera/riceve da <paramref name="s"/>, sia quelle
    /// "scoperte" da sliders la cui linea di vista passa per <paramref name="s"/> (quando
    /// <paramref name="computeRay"/> è vero — la fonte lo pone a falso solo in
    /// <c>swap_piece</c>, dove la casa non è mai vuota e quindi non può esserci nulla da
    /// scoprire). <paramref name="noRaysContaining"/> (usato solo da <c>move_piece</c>) esclude i
    /// raggi che contengono SIA la casa di partenza che quella di arrivo dello stesso pezzo in
    /// movimento — altrimenti il proprio spostamento lungo la propria retta genererebbe uno
    /// "scoperto" fittizio.</summary>
    private void UpdatePieceThreats(Piece pc, bool putPiece, Square s, List<DirtyThreat> dts,
        bool computeRay = true, ulong noRaysContaining = ulong.MaxValue)
    {
        ulong occupied = Pieces();
        ulong bAttacks = Attacks.AttacksBb(PieceType.Bishop, s, occupied);
        ulong rAttacks = Attacks.AttacksBb(PieceType.Rook, s, occupied);
        ulong sliderAttacks = bAttacks | rAttacks;
        ulong occupiedNoK = occupied ^ Pieces(PieceType.King);
        PieceType pt = Types.TypeOf(pc);
        ulong sliders = (Pieces(PieceType.Bishop, PieceType.Queen) & bAttacks) | (Pieces(PieceType.Rook, PieceType.Queen) & rAttacks);

        void ProcessSliders(bool addDirectAttacks)
        {
            ulong b = sliders;
            while (b != 0)
            {
                Square sliderSq = Bitboards.PopLsb(ref b);
                Piece slider = PieceOn(sliderSq);

                ulong ray = Attacks.RayPass(sliderSq, s);
                ulong discovered = ray & sliderAttacks & occupiedNoK;

                if (discovered != 0 && (ray & noRaysContaining) != noRaysContaining)
                {
                    Square threatenedSq = Bitboards.Lsb(discovered);
                    Piece threatenedPc = PieceOn(threatenedSq);
                    if (CanSliderThreat(threatenedPc, slider))
                        dts.Add(new DirtyThreat(slider, threatenedPc, sliderSq, threatenedSq, !putPiece));
                }

                if (addDirectAttacks && CanSliderThreat(pc, slider))
                    dts.Add(new DirtyThreat(slider, pc, sliderSq, s, putPiece));
            }
        }

        // I re non emettono mai minacce dirette (position.cpp:1231-1237) — ma possono comunque
        // "scoprire" una minaccia altrui muovendosi, da cui la chiamata a ProcessSliders qui sotto.
        if (pt == PieceType.King)
        {
            if (computeRay) ProcessSliders(false);
            return;
        }

        ulong threatTargets = pt == PieceType.Pawn ? Pieces(PieceType.Knight, PieceType.Rook)
            : pt is PieceType.Bishop or PieceType.Rook
                ? Pieces(PieceType.Pawn) | Pieces(PieceType.Knight) | Pieces(PieceType.Bishop) | Pieces(PieceType.Rook)
                : occupiedNoK;

        ulong threatened = (pt switch
        {
            PieceType.Bishop => bAttacks,
            PieceType.Rook => rAttacks,
            PieceType.Queen => sliderAttacks,
            PieceType.Pawn => Attacks.PawnAttacksBb(s, Types.ColorOf(pc)),
            _ => Attacks.AttacksBb(pt, s),
        }) & threatTargets;

        ulong incomingThreats = Attacks.AttacksBb(PieceType.Knight, s) & Pieces(PieceType.Knight);
        if (pt is PieceType.Knight or PieceType.Rook)
            incomingThreats |= (Attacks.PawnAttacksBb(s, Color.White) & Pieces(Color.Black, PieceType.Pawn))
                              | (Attacks.PawnAttacksBb(s, Color.Black) & Pieces(Color.White, PieceType.Pawn));

        while (threatened != 0)
        {
            Square threatenedSq = Bitboards.PopLsb(ref threatened);
            Piece threatenedPc = PieceOn(threatenedSq);
            dts.Add(new DirtyThreat(pc, threatenedPc, s, threatenedSq, putPiece));
        }

        if (computeRay)
            ProcessSliders(true);
        else
            incomingThreats |= pt == PieceType.Queen ? sliders & Pieces(PieceType.Queen) : sliders;

        while (incomingThreats != 0)
        {
            Square srcSq = Bitboards.PopLsb(ref incomingThreats);
            Piece srcPc = PieceOn(srcSq);
            dts.Add(new DirtyThreat(srcPc, pc, srcSq, s, putPiece));
        }
    }

    public ulong Pinners(Color c) => _st.Pinners[(byte)c];

    public ulong AttackersTo(Square s) => AttackersTo(s, Pieces());

    /// <summary>Tutti i pezzi (di qualunque colore) che attaccano la casa s data l'occupazione
    /// indicata — <c>Position::attackers_to</c>, position.cpp:643-651.</summary>
    public ulong AttackersTo(Square s, ulong occupied)
    {
        var (bishopAttacks, rookAttacks) = BothAttacksBb(s, occupied);
        return (rookAttacks & Pieces(PieceType.Rook, PieceType.Queen))
             | (bishopAttacks & Pieces(PieceType.Bishop, PieceType.Queen))
             | (Attacks.PawnAttacksBb(s, Color.Black) & Pieces(Color.White, PieceType.Pawn))
             | (Attacks.PawnAttacksBb(s, Color.White) & Pieces(Color.Black, PieceType.Pawn))
             | (Attacks.AttacksBb(PieceType.Knight, s) & Pieces(PieceType.Knight))
             | (Attacks.AttacksBb(PieceType.King, s) & Pieces(PieceType.King));
    }

    /// <summary>Vero se il colore c ha un pezzo che attacca la casa s data l'occupazione indicata —
    /// <c>Position::attackers_to_exist</c>, position.cpp:653-659. Più economica di
    /// <see cref="AttackersTo(Square, ulong)"/> quando serve solo un sì/no per un colore, si ferma
    /// al primo attaccante trovato.</summary>
    public bool AttackersToExist(Square s, ulong occupied, Color c) =>
        (Attacks.AttacksBb(PieceType.Rook, s, occupied) & Pieces(c, PieceType.Rook, PieceType.Queen)) != 0
        || (Attacks.AttacksBb(PieceType.Bishop, s, occupied) & Pieces(c, PieceType.Bishop, PieceType.Queen)) != 0
        || (Attacks.PawnAttacksBb(s, Types.Opposite(c)) & Pieces(c, PieceType.Pawn)) != 0
        || (Attacks.AttacksBb(PieceType.Knight, s) & Pieces(c, PieceType.Knight)) != 0
        || (Attacks.AttacksBb(PieceType.King, s) & Pieces(c, PieceType.King)) != 0;

    private static (ulong Bishop, ulong Rook) BothAttacksBb(Square s, ulong occupied) =>
        Attacks.UsingAvx2
            ? Attacks.BothAttacksBbAvx2(s, occupied)
            : (Attacks.MagicAttacksBb(PieceType.Bishop, s, occupied), Attacks.MagicAttacksBb(PieceType.Rook, s, occupied));

    /// <summary>Calcola <c>blockersForKing[c]</c> e <c>pinners[~c]</c>: i pezzi che, se rimossi,
    /// esporrebbero il re di colore c a scacco, e gli inchiodanti avversari corrispondenti —
    /// <c>Position::update_slider_blockers</c>, position.cpp:613-638.</summary>
    private void UpdateSliderBlockers(Color c)
    {
        Square ksq = SquareOf(PieceType.King, c);
        var them = Types.Opposite(c);

        _st.BlockersForKing[(byte)c] = 0;
        _st.Pinners[(byte)them] = 0;

        // "Cecchini": sliders che attaccherebbero ksq se il pezzo che (eventualmente) la blocca e
        // altri cecchini fossero rimossi dalla scacchiera.
        ulong snipers = ((Attacks.AttacksBb(PieceType.Rook, ksq) & Pieces(PieceType.Queen, PieceType.Rook))
                        | (Attacks.AttacksBb(PieceType.Bishop, ksq) & Pieces(PieceType.Queen, PieceType.Bishop)))
                      & Pieces(them);
        ulong occupancy = Pieces() ^ snipers;

        while (snipers != 0)
        {
            Square sniperSq = Bitboards.PopLsb(ref snipers);
            ulong b = Attacks.Between(ksq, sniperSq) & occupancy;

            if (b != 0 && !Bitboards.MoreThanOne(b))
            {
                _st.BlockersForKing[(byte)c] |= b;
                if ((b & Pieces(c)) != 0)
                    _st.Pinners[(byte)them] |= Bitboards.SquareBB(sniperSq);
            }
        }
    }

    // --- Proprietà delle mosse — position.h:136-143 ---

    public Piece MovedPiece(Move m) => PieceOn(m.FromSq);

    public Piece CapturedPiece() => _st.CapturedPiece;

    public bool Capture(Move m)
    {
        var mt = m.TypeOf;
        if (mt is MoveType.Normal or MoveType.Promotion) return !Empty(m.ToSq);
        return mt == MoveType.EnPassant;
    }

    /// <summary>Vero se la mossa è generata dallo stadio CAPTURES (include anche le promozioni a
    /// Donna) — <c>Position::capture_stage</c>, position.h:365-377. Coerenza con MoveGen: evita di
    /// generare la stessa mossa due volte fra CAPTURES e QUIETS.</summary>
    public bool CaptureStage(Move m)
    {
        var mt = m.TypeOf;
        if (mt == MoveType.Normal) return !Empty(m.ToSq);
        if (mt == MoveType.Promotion) return !Empty(m.ToSq) || m.PromotionType == PieceType.Queen;
        return mt == MoveType.EnPassant;
    }

    /// <summary>Testa se una mossa pseudo-legale è anche legale — <c>Position::legal</c>,
    /// position.cpp:661-699.</summary>
    public bool Legal(Move m)
    {
        Color us = _sideToMove;
        Square from = m.FromSq;
        Square to = m.ToSq;

        if (m.TypeOf == MoveType.Castling)
        {
            // Dopo l'arrocco, le posizioni finali di re e torre sono le stesse dello scacchi
            // standard anche in Chess960.
            to = Types.RelativeSquare(us, to > from ? Square.G1 : Square.C1);
            Direction step = to > from ? Direction.West : Direction.East;

            for (Square s = to; s != from; s = Types.AddDirection(s, step))
                if (AttackersToExist(s, Pieces(), Types.Opposite(us)))
                    return false;

            // Caso Chess960: verifica se la torre stessa blocca uno scacco (es. una donna
            // avversaria su a1 quando la torre che arrocca è su b1).
            return !_chess960 || (BlockersForKing(us) & Bitboards.SquareBB(m.ToSq)) == 0;
        }

        if (Types.TypeOf(PieceOn(from)) == PieceType.King)
            return !AttackersToExist(to, Pieces() ^ Bitboards.SquareBB(from), Types.Opposite(us));

        // Una mossa che non è del re è legale se e solo se il pezzo non è inchiodato, oppure si
        // muove lungo la linea verso o dal re.
        return (BlockersForKing(us) & Bitboards.SquareBB(from)) == 0
            || (Attacks.LineOf(from, to) & Pieces(us, PieceType.King)) != 0;
    }

    /// <summary>Testa se una mossa qualunque (potenzialmente corrotta, es. da una entry TT
    /// obsoleta) è pseudo-legale — <c>Position::pseudo_legal</c>, position.cpp:705-762. Per i tipi
    /// di mossa speciali (arrocco/promozione/en passant) ricade sulla generazione completa via
    /// <see cref="MoveGen"/>, più lenta ma più semplice, esattamente come la fonte.</summary>
    public bool PseudoLegal(Move m)
    {
        Color us = _sideToMove;
        Square from = m.FromSq;
        Square to = m.ToSq;
        Piece pc = MovedPiece(m);

        if (m.TypeOf != MoveType.Normal)
        {
            var list = new List<Move>();
            if (Checkers() != 0) MoveGen.Generate(GenType.Evasions, this, list);
            else MoveGen.Generate(GenType.NonEvasions, this, list);
            return list.Contains(m);
        }

        if (pc == Piece.None || Types.ColorOf(pc) != us) return false;
        if ((Pieces(us) & Bitboards.SquareBB(to)) != 0) return false;

        if (Types.TypeOf(pc) == PieceType.Pawn)
        {
            if (((Bitboards.Rank8BB | Bitboards.Rank1BB) & Bitboards.SquareBB(to)) != 0) return false;

            bool isCapture = (Attacks.PawnAttacksBb(from, us) & Pieces(Types.Opposite(us)) & Bitboards.SquareBB(to)) != 0;
            bool isSinglePush = Types.AddDirection(from, Types.PawnPush(us)) == to && Empty(to);
            bool isDoublePush = Types.AddDirection(from, (Direction)((int)Types.PawnPush(us) * 2)) == to
                                 && Types.RelativeRank(us, from) == Rank.Rank2 && Empty(to)
                                 && Empty(Types.SubDirection(to, Types.PawnPush(us)));

            if (!(isCapture || isSinglePush || isDoublePush)) return false;
        }
        else if ((Attacks.AttacksBb(Types.TypeOf(pc), from, Pieces()) & Bitboards.SquareBB(to)) == 0)
        {
            return false;
        }

        if (Checkers() != 0 && Types.TypeOf(pc) != PieceType.King)
        {
            if (Bitboards.MoreThanOne(Checkers())) return false;
            if ((Attacks.Between(SquareOf(PieceType.King, us), Bitboards.Lsb(Checkers())) & Bitboards.SquareBB(to)) == 0)
                return false;
        }

        return true;
    }

    /// <summary>Testa se una mossa pseudo-legale dà scacco — <c>Position::gives_check</c>,
    /// position.cpp:765-809. Serve PRIMA di eseguire la mossa (usa lo stato corrente), il
    /// risultato va passato a <see cref="DoMove(Move, StateInfo, bool)"/>.</summary>
    public bool GivesCheck(Move m)
    {
        Square from = m.FromSq;
        Square to = m.ToSq;

        if ((CheckSquaresOf(Types.TypeOf(PieceOn(from))) & Bitboards.SquareBB(to)) != 0) return true;

        var them = Types.Opposite(_sideToMove);
        if ((BlockersForKing(them) & Bitboards.SquareBB(from)) != 0)
            return (Attacks.LineOf(from, to) & Pieces(them, PieceType.King)) == 0 || m.TypeOf == MoveType.Castling;

        switch (m.TypeOf)
        {
            case MoveType.Normal:
                return false;

            case MoveType.Promotion:
                return (Attacks.AttacksBb(m.PromotionType, to, Pieces() ^ Bitboards.SquareBB(from)) & Pieces(them, PieceType.King)) != 0;

            case MoveType.EnPassant:
            {
                Square capsq = Types.MakeSquare(Types.FileOf(to), Types.RankOf(from));
                ulong b = (Pieces() ^ Bitboards.SquareBB(from) ^ Bitboards.SquareBB(capsq)) | Bitboards.SquareBB(to);
                var (bishopAttacks, rookAttacks) = BothAttacksBb(SquareOf(PieceType.King, them), b);
                return (rookAttacks & Pieces(_sideToMove, PieceType.Queen, PieceType.Rook)) != 0
                    || (bishopAttacks & Pieces(_sideToMove, PieceType.Queen, PieceType.Bishop)) != 0;
            }

            default: // CASTLING — codificato come "il re cattura la propria torre"
            {
                Square rto = Types.RelativeSquare(_sideToMove, to > from ? Square.F1 : Square.D1);
                return (CheckSquaresOf(PieceType.Rook) & Bitboards.SquareBB(rto)) != 0;
            }
        }
    }

    // --- Chiavi hash — position.h:160-166 ---

    public ulong Key => _st.Key;

    public ulong MaterialKey => _st.MaterialKey;

    public ulong PawnKey => _st.PawnKey;

    public ulong MinorPieceKey => _st.MinorPieceKey;

    public ulong NonPawnKey(Color c) => _st.NonPawnKey[(byte)c];

    // --- Altre proprietà — position.h:168-179 ---

    public Color SideToMove => _sideToMove;

    public int GamePly => _gamePly;

    public bool IsChess960 => _chess960;

    public int Rule50Count => _st.Rule50;

    public int PliesFromNull => _st.PliesFromNull;

    /// <summary>Patta per regola delle 50 mosse o per ripetizione — NON rileva lo stallo (quello
    /// si vede dall'assenza di mosse legali nel ciclo di ricerca) — <c>Position::is_draw</c>,
    /// position.cpp:1496-1502.</summary>
    public bool IsDraw(int ply)
    {
        if (_st.Rule50 > 99)
        {
            if (Checkers() == 0) return true;
            var moves = new List<Move>();
            MoveGen.Generate(GenType.Legal, this, moves);
            if (moves.Count > 0) return true;
        }

        return IsRepetition(ply);
    }

    /// <summary>Vero se la posizione si è già ripetuta una volta STRETTAMENTE dopo la radice, o
    /// due volte prima o alla radice — <c>Position::is_repetition</c>, position.cpp:1504-1506.</summary>
    public bool IsRepetition(int ply) => _st.Repetition != 0 && _st.Repetition < ply;

    /// <summary>Vero se c'è stata almeno una ripetizione dall'ultima cattura o mossa di pedone —
    /// <c>Position::has_repeated</c>, position.cpp:1508-1522.</summary>
    public bool HasRepeated()
    {
        StateInfo? stc = _st;
        int end = Math.Min(_st.Rule50, _st.PliesFromNull);
        while (end-- >= 4)
        {
            if (stc!.Repetition != 0) return true;
            stc = stc.Previous;
        }

        return false;
    }

    /// <summary>Vero se esiste una mossa che porterebbe a una ripetizione — usa le tabelle cuckoo
    /// per verificarlo senza generare/provare ogni mossa candidata. Combacia esattamente con
    /// l'esito di <see cref="IsDraw"/> su tutte le mosse legali — <c>Position::upcoming_repetition</c>,
    /// position.cpp:1525-1568.</summary>
    public bool UpcomingRepetition(int ply)
    {
        int end = Math.Min(_st.Rule50, _st.PliesFromNull);
        if (end < 3) return false;

        ulong originalKey = _st.Key;
        StateInfo? stp = _st.Previous;
        ulong other = originalKey ^ stp!.Key ^ Zobrist.Side;

        for (int i = 3; i <= end; i += 2)
        {
            stp = stp!.Previous;
            other ^= stp!.Key ^ stp.Previous!.Key ^ Zobrist.Side;
            stp = stp.Previous;

            if (other != 0) continue;

            ulong moveKey = originalKey ^ stp!.Key;

            int j = Zobrist.H1(moveKey);
            if (Zobrist.Cuckoo[j] != moveKey)
            {
                j = Zobrist.H2(moveKey);
                if (Zobrist.Cuckoo[j] != moveKey) continue;
            }

            Move move = Zobrist.CuckooMove[j];
            Square s1 = move.FromSq;
            Square s2 = move.ToSq;

            if (((Attacks.Between(s1, s2) ^ Bitboards.SquareBB(s2)) & Pieces()) == 0)
            {
                if (ply > i) return true;

                // Per i nodi prima o alla radice, verifica che la mossa sia una ripetizione
                // rispetto a una mossa verso la posizione attuale.
                if (stp.Repetition != 0) return true;
            }
        }

        return false;
    }

    public int NonPawnMaterial(Color c) => _st.NonPawnMaterial[(byte)c];

    public int NonPawnMaterial() => NonPawnMaterial(Color.White) + NonPawnMaterial(Color.Black);

    public StateInfo State => _st;

    // --- Modifica della scacchiera — position.h:381-421. Il parametro NNUE "dts" (facoltativo,
    // null di default e per tutti i chiamanti finora) è ora presente — vedi UpdatePieceThreats
    // sopra e DirtyThreat.cs; "dp" (DirtyPiece, per l'accumulatore incrementale N9) resta ancora
    // da aggiungere. ---

    private void PutPiece(Piece pc, Square s, List<DirtyThreat>? dts = null)
    {
        _board[(byte)s] = pc;
        _byTypeBB[(byte)PieceType.AllPieces] |= Bitboards.SquareBB(s);
        _byTypeBB[(byte)Types.TypeOf(pc)] |= Bitboards.SquareBB(s);
        _byColorBB[(byte)Types.ColorOf(pc)] |= Bitboards.SquareBB(s);
        _pieceCount[(byte)pc]++;
        _pieceCount[(byte)Types.MakePiece(Types.ColorOf(pc), PieceType.AllPieces)]++;

        if (dts != null) UpdatePieceThreats(pc, true, s, dts);
    }

    private void RemovePiece(Square s, List<DirtyThreat>? dts = null)
    {
        Piece pc = _board[(byte)s];

        if (dts != null) UpdatePieceThreats(pc, false, s, dts);

        _byTypeBB[(byte)PieceType.AllPieces] ^= Bitboards.SquareBB(s);
        _byTypeBB[(byte)Types.TypeOf(pc)] ^= Bitboards.SquareBB(s);
        _byColorBB[(byte)Types.ColorOf(pc)] ^= Bitboards.SquareBB(s);
        _board[(byte)s] = Piece.None;
        _pieceCount[(byte)pc]--;
        _pieceCount[(byte)Types.MakePiece(Types.ColorOf(pc), PieceType.AllPieces)]--;
    }

    private void MovePiece(Square from, Square to, List<DirtyThreat>? dts = null)
    {
        Piece pc = _board[(byte)from];
        ulong fromTo = Bitboards.SquareBB(from) | Bitboards.SquareBB(to);

        // noRaysContaining=fromTo esclude i raggi che vedono ENTRAMBE le case: altrimenti il
        // pezzo che si sposta lungo la propria retta scoprirebbe una minaccia fittizia verso se
        // stesso — position.h:406-421.
        if (dts != null) UpdatePieceThreats(pc, false, from, dts, noRaysContaining: fromTo);

        _byTypeBB[(byte)PieceType.AllPieces] ^= fromTo;
        _byTypeBB[(byte)Types.TypeOf(pc)] ^= fromTo;
        _byColorBB[(byte)Types.ColorOf(pc)] ^= fromTo;
        _board[(byte)from] = Piece.None;
        _board[(byte)to] = pc;

        if (dts != null) UpdatePieceThreats(pc, true, to, dts, noRaysContaining: fromTo);
    }

    private void SwapPiece(Square s, Piece pc, List<DirtyThreat>? dts = null)
    {
        Piece old = _board[(byte)s];

        RemovePiece(s);
        // ComputeRay=false, position.h:423-435: la casa s non è mai vuota durante lo swap (il
        // pezzo catturato lascia il posto direttamente al nuovo), quindi non può esserci nulla da
        // "scoprire" attraverso di essa — l'unica differenza è chi la occupa.
        if (dts != null) UpdatePieceThreats(old, false, s, dts, computeRay: false);

        PutPiece(pc, s);
        if (dts != null) UpdatePieceThreats(pc, true, s, dts, computeRay: false);
    }

    // --- Impostazione da FEN — position.cpp:169-443 ---

    private static readonly string PieceToChar = " PNBRQK  pnbrqk";

    public void Set(string fenStr, bool isChess960)
    {
        Array.Fill(_board, Piece.None);
        Array.Clear(_byTypeBB);
        Array.Clear(_byColorBB);
        Array.Clear(_pieceCount);
        Array.Fill(_castlingRightsMask, CastlingRights.None);
        Array.Fill(_castlingRookSquare, Square.None);
        Array.Clear(_castlingPath);
        _st = new StateInfo();
        _gamePly = 0;
        _chess960 = isChess960;

        string[] fields = fenStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 1) throw new PositionSetException("FEN non valida: stringa vuota.");

        // 1. Disposizione dei pezzi
        string[] ranks = fields[0].Split('/');
        if (ranks.Length != 8) throw new PositionSetException("FEN non valida: attese 8 traverse separate da '/'.");

        for (int i = 0; i < 8; i++)
        {
            var rank = (Rank)(7 - i);
            int file = 0;
            foreach (char token in ranks[i])
            {
                if (char.IsDigit(token))
                {
                    int diff = token - '0';
                    if (diff is < 1 or > 8) throw new PositionSetException("FEN non valida: numero di case da saltare non valido.");
                    file += diff;
                }
                else
                {
                    int idx = PieceToChar.IndexOf(token);
                    if (idx < 0 || file >= 8) throw new PositionSetException($"FEN non valida: pezzo non valido '{token}'.");
                    PutPiece((Piece)idx, Types.MakeSquare((File)file, rank));
                    file++;
                }
            }

            if (file != 8) throw new PositionSetException("FEN non valida: traversa non conclusa correttamente.");
        }

        if ((Pieces(PieceType.Pawn) & (Bitboards.Rank1BB | Bitboards.Rank8BB)) != 0)
            throw new PositionSetException("Posizione non supportata: pedoni sulla prima o ottava traversa.");
        if (Count(PieceType.King, Color.White) != 1 || Count(PieceType.King, Color.Black) != 1)
            throw new PositionSetException("Posizione non supportata: numero di re non corretto.");

        // 2. Colore al tratto
        if (fields.Length < 2) throw new PositionSetException("FEN non valida: manca il colore al tratto.");
        _sideToMove = fields[1] switch
        {
            "w" => Color.White,
            "b" => Color.Black,
            _ => throw new PositionSetException($"FEN non valida: colore al tratto non valido '{fields[1]}'."),
        };

        // 3. Diritti di arrocco (compatibile con FEN standard e Shredder-FEN/X-FEN Chess960)
        if (fields.Length > 2 && fields[2] != "-")
        {
            foreach (char token in fields[2])
            {
                Square rsq = Square.None, ksq = Square.None;
                Color c = char.IsLower(token) ? Color.Black : Color.White;
                Piece rook = Types.MakePiece(c, PieceType.Rook);
                Piece king = Types.MakePiece(c, PieceType.King);
                char upper = char.ToUpperInvariant(token);

                if (upper is 'K' or 'Q')
                {
                    int dir = upper == 'K' ? -1 : 1;
                    Square sq = Types.RelativeSquare(c, upper == 'K' ? Square.H1 : Square.A1);
                    for (int i = 0; i < 7; i++, sq = (Square)((byte)sq + dir))
                    {
                        Piece pc = PieceOn(sq);
                        if (pc == king) { ksq = sq; break; }
                        if (pc == rook && rsq == Square.None) rsq = sq;
                    }
                }
                else if (upper is >= 'A' and <= 'H')
                {
                    var rsqCandidate = Types.MakeSquare((File)(upper - 'A'), Types.RelativeRank(c, Rank.Rank1));
                    if (PieceOn(rsqCandidate) == rook) rsq = rsqCandidate;

                    Square sq = Types.RelativeSquare(c, Square.B1);
                    for (int i = 0; i < 6; i++, sq = (Square)((byte)sq + 1))
                        if (PieceOn(sq) == king) ksq = sq;
                }
                else
                {
                    throw new PositionSetException($"FEN non valida: diritto di arrocco atteso, trovato '{token}'.");
                }

                if (ksq != Square.None && rsq != Square.None) SetCastlingRight(c, rsq);
            }
        }

        // 4. Casa en passant — considerata solo se davvero catturabile (X-FEN)
        _st.EpSquare = Square.None;
        if (fields.Length > 3 && fields[3] != "-")
        {
            string ep = fields[3];
            if (ep.Length == 2 && ep[0] is >= 'a' and <= 'h' && ep[1] == (_sideToMove == Color.White ? '6' : '3'))
            {
                var epSquare = Types.MakeSquare((File)(ep[0] - 'a'), (Rank)(ep[1] - '1'));
                var them = Types.Opposite(_sideToMove);

                ulong pawns = Attacks.PawnAttacksBb(epSquare, them) & Pieces(_sideToMove, PieceType.Pawn);
                ulong target = Pieces(them, PieceType.Pawn) & Bitboards.SquareBB(Types.AddDirection(epSquare, Types.PawnPush(them)));
                ulong occ = Pieces() ^ target ^ Bitboards.SquareBB(epSquare);

                bool enPassantPossible = pawns != 0 && target != 0
                    && (Pieces() & (Bitboards.SquareBB(epSquare) | Bitboards.SquareBB(Types.AddDirection(epSquare, Types.PawnPush(_sideToMove))))) == 0;

                bool legalEp = false;
                ulong pawnsCopy = pawns;
                while (pawnsCopy != 0)
                {
                    Square from = Bitboards.PopLsb(ref pawnsCopy);
                    ulong afterCapture = occ ^ Bitboards.SquareBB(from);
                    legalEp |= (AttackersTo(SquareOf(PieceType.King, _sideToMove), afterCapture) & Pieces(them) & ~target) == 0;
                }

                if (enPassantPossible && legalEp) _st.EpSquare = epSquare;
            }
            else
            {
                throw new PositionSetException($"FEN non valida: casa en passant non valida '{ep}'.");
            }
        }

        // 5-6. Contatore delle 50 mosse e numero di mossa piena
        _st.Rule50 = fields.Length > 4 && int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r50) ? r50 : 0;
        int fullMove = fields.Length > 5 && int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fm) ? fm : 1;
        _gamePly = Math.Max(2 * (fullMove - 1), 0) + (_sideToMove == Color.Black ? 1 : 0);

        SetState();

        if (AttackersToExist(SquareOf(PieceType.King, Types.Opposite(_sideToMove)), Pieces(), _sideToMove))
            throw new PositionSetException("Posizione non supportata: il re al tratto avversario è già catturabile.");
    }

    private void SetCastlingRight(Color c, Square rfrom)
    {
        Square kfrom = SquareOf(PieceType.King, c);
        var cr = Types.CastlingFor(c, kfrom < rfrom ? CastlingRights.KingSide : CastlingRights.QueenSide);

        _st.CastlingRights |= cr;
        _castlingRightsMask[(byte)kfrom] |= cr;
        _castlingRightsMask[(byte)rfrom] |= cr;
        _castlingRookSquare[(int)cr] = rfrom;

        Square kto = Types.RelativeSquare(c, (cr & CastlingRights.KingSide) != 0 ? Square.G1 : Square.C1);
        Square rto = Types.RelativeSquare(c, (cr & CastlingRights.KingSide) != 0 ? Square.F1 : Square.D1);

        _castlingPath[(int)cr] = (Attacks.Between(rfrom, rto) | Attacks.Between(kfrom, kto)) & ~(Bitboards.SquareBB(kfrom) | Bitboards.SquareBB(rfrom));
    }

    private void SetCheckInfo()
    {
        UpdateSliderBlockers(Color.White);
        UpdateSliderBlockers(Color.Black);

        Square ksq = SquareOf(PieceType.King, Types.Opposite(_sideToMove));
        var (bishopAttacks, rookAttacks) = BothAttacksBb(ksq, Pieces());

        _st.CheckSquares[(byte)PieceType.Pawn] = Attacks.PawnAttacksBb(ksq, Types.Opposite(_sideToMove));
        _st.CheckSquares[(byte)PieceType.Knight] = Attacks.AttacksBb(PieceType.Knight, ksq);
        _st.CheckSquares[(byte)PieceType.Bishop] = bishopAttacks;
        _st.CheckSquares[(byte)PieceType.Rook] = rookAttacks;
        _st.CheckSquares[(byte)PieceType.Queen] = bishopAttacks | rookAttacks;
        _st.CheckSquares[(byte)PieceType.King] = 0;
    }

    /// <summary>Calcola le chiavi hash e altri dati che poi vengono aggiornati incrementalmente —
    /// <c>Position::set_state</c>, position.cpp:487-529. Usata solo quando si imposta una nuova
    /// posizione da zero (FEN).</summary>
    private void SetState()
    {
        _st.Key = 0;
        _st.MinorPieceKey = 0;
        _st.NonPawnKey[(byte)Color.White] = 0;
        _st.NonPawnKey[(byte)Color.Black] = 0;
        _st.PawnKey = Zobrist.NoPawns;
        _st.NonPawnMaterial[(byte)Color.White] = 0;
        _st.NonPawnMaterial[(byte)Color.Black] = 0;
        _st.CheckersBB = AttackersTo(SquareOf(PieceType.King, _sideToMove)) & Pieces(Types.Opposite(_sideToMove));

        SetCheckInfo();

        ulong occ = Pieces();
        while (occ != 0)
        {
            Square s = Bitboards.PopLsb(ref occ);
            Piece pc = PieceOn(s);
            _st.Key ^= Zobrist.Psq[(byte)pc, (byte)s];

            if (Types.TypeOf(pc) == PieceType.Pawn)
            {
                _st.PawnKey ^= Zobrist.Psq[(byte)pc, (byte)s];
            }
            else
            {
                var color = Types.ColorOf(pc);
                _st.NonPawnKey[(byte)color] ^= Zobrist.Psq[(byte)pc, (byte)s];

                if (Types.TypeOf(pc) != PieceType.King)
                {
                    _st.NonPawnMaterial[(byte)color] += Values.PieceValue[(byte)pc];
                    if (Types.TypeOf(pc) <= PieceType.Bishop)
                        _st.MinorPieceKey ^= Zobrist.Psq[(byte)pc, (byte)s];
                }
            }
        }

        if (_st.EpSquare != Square.None)
            _st.Key ^= Zobrist.EnPassant[(byte)Types.FileOf(_st.EpSquare)];

        if (_sideToMove == Color.Black)
            _st.Key ^= Zobrist.Side;

        _st.Key ^= Zobrist.Castling[(int)_st.CastlingRights];
        _st.MaterialKey = ComputeMaterialKey();
    }

    private ulong ComputeMaterialKey()
    {
        ulong k = 0;
        foreach (var pc in AllPieces.Values)
            for (int cnt = 0; cnt < _pieceCount[(byte)pc]; cnt++)
                k ^= Zobrist.Psq[(byte)pc, 8 + cnt];
        return k;
    }

    public string Fen()
    {
        var sb = new StringBuilder();
        for (var r = Rank.Rank8; ; r--)
        {
            int emptyCount = 0;
            for (var f = File.A; f <= File.H; f++)
            {
                if (Empty(Types.MakeSquare(f, r)))
                {
                    emptyCount++;
                    continue;
                }

                if (emptyCount > 0) { sb.Append(emptyCount); emptyCount = 0; }
                sb.Append(PieceToChar[(byte)PieceOn(Types.MakeSquare(f, r))]);
            }

            if (emptyCount > 0) sb.Append(emptyCount);
            if (r == Rank.Rank1) break;
            sb.Append('/');
        }

        sb.Append(_sideToMove == Color.White ? " w " : " b ");

        int beforeCastling = sb.Length;
        if (CanCastle(CastlingRights.WhiteOo)) sb.Append(_chess960 ? (char)('A' + (byte)Types.FileOf(CastlingRookSquareOf(CastlingRights.WhiteOo))) : 'K');
        if (CanCastle(CastlingRights.WhiteOoo)) sb.Append(_chess960 ? (char)('A' + (byte)Types.FileOf(CastlingRookSquareOf(CastlingRights.WhiteOoo))) : 'Q');
        if (CanCastle(CastlingRights.BlackOo)) sb.Append(_chess960 ? (char)('a' + (byte)Types.FileOf(CastlingRookSquareOf(CastlingRights.BlackOo))) : 'k');
        if (CanCastle(CastlingRights.BlackOoo)) sb.Append(_chess960 ? (char)('a' + (byte)Types.FileOf(CastlingRookSquareOf(CastlingRights.BlackOoo))) : 'q');
        if (sb.Length == beforeCastling) sb.Append('-');

        sb.Append(' ').Append(EpSquare == Square.None ? "-" : SquareName(EpSquare));
        sb.Append(' ').Append(_st.Rule50);
        sb.Append(' ').Append(1 + (_gamePly - (_sideToMove == Color.Black ? 1 : 0)) / 2);

        return sb.ToString();
    }

    internal static string SquareName(Square s) =>
        $"{(char)('a' + (byte)Types.FileOf(s))}{(char)('1' + (byte)Types.RankOf(s))}";

    // --- Fare e disfare mosse — position.cpp:812-1143, 1311-1339 ---

    /// <summary>Esegue una mossa (assunta legale) salvando lo stato necessario a disfarla —
    /// <c>Position::do_move</c>, position.cpp:817-1081 (senza gli agganci NNUE/TT/history, vedi
    /// nota in cima al file).</summary>
    public void DoMove(Move m, StateInfo newSt) => DoMove(m, newSt, GivesCheck(m));

    /// <summary><paramref name="dirtyThreats"/> (facoltativo, null di default) raccoglie i
    /// cambiamenti di minaccia FullThreats causati da questa mossa — vedi UpdatePieceThreats.
    /// <paramref name="dirtyPiece"/> (DirtyPiece, types.h:296-306) e <paramref
    /// name="dirtyPawnPairs"/> (DirtyPawnPairs, types.h:347-350) sono gli altri due meccanismi
    /// "dirty" della fonte usati dall'accumulatore NNUE incrementale (N9, non ancora portato) per
    /// le feature HalfKA e Pp3Wide rispettivamente. Nessun costo per i chiamanti esistenti (che
    /// non li passano): restano il comportamento originale.</summary>
    public void DoMove(Move m, StateInfo newSt, bool givesCheck, List<DirtyThreat>? dirtyThreats = null,
        DirtyPiece? dirtyPiece = null, DirtyPawnPairs? dirtyPawnPairs = null)
    {
        ulong k = _st.Key ^ Zobrist.Side;

        newSt.CopyMoveFieldsFrom(_st);
        newSt.Previous = _st;
        _st = newSt;

        _gamePly++;
        _st.Rule50++;
        _st.PliesFromNull++;

        if (dirtyPawnPairs != null)
        {
            dirtyPawnPairs.Before[(byte)Color.White] = Pieces(Color.White, PieceType.Pawn);
            dirtyPawnPairs.Before[(byte)Color.Black] = Pieces(Color.Black, PieceType.Pawn);
        }

        Color us = _sideToMove;
        Color them = Types.Opposite(us);
        Square from = m.FromSq;
        Square to = m.ToSq;
        Piece pc = PieceOn(from);
        Piece captured = m.TypeOf == MoveType.EnPassant ? Types.MakePiece(them, PieceType.Pawn) : PieceOn(to);

        if (dirtyPiece != null)
        {
            dirtyPiece.Pc = pc;
            dirtyPiece.From = from;
            dirtyPiece.To = to;
            dirtyPiece.AddSq = Square.None;
        }

        if (m.TypeOf == MoveType.Castling)
        {
            DoCastling(true, us, from, ref to, out Square rfrom, out Square rto, dirtyThreats, dirtyPiece);
            k ^= Zobrist.Psq[(byte)captured, (byte)rfrom] ^ Zobrist.Psq[(byte)captured, (byte)rto];
            _st.NonPawnKey[(byte)us] ^= Zobrist.Psq[(byte)captured, (byte)rfrom] ^ Zobrist.Psq[(byte)captured, (byte)rto];
            captured = Piece.None;
        }
        else if (captured != Piece.None)
        {
            Square capsq = to;

            if (Types.TypeOf(captured) == PieceType.Pawn)
            {
                if (m.TypeOf == MoveType.EnPassant)
                {
                    capsq = Types.SubDirection(capsq, Types.PawnPush(us));
                    RemovePiece(capsq, dirtyThreats);
                }

                _st.PawnKey ^= Zobrist.Psq[(byte)captured, (byte)capsq];
            }
            else
            {
                _st.NonPawnMaterial[(byte)them] -= Values.PieceValue[(byte)captured];
                _st.NonPawnKey[(byte)them] ^= Zobrist.Psq[(byte)captured, (byte)capsq];
                if (Types.TypeOf(captured) <= PieceType.Bishop)
                    _st.MinorPieceKey ^= Zobrist.Psq[(byte)captured, (byte)capsq];
            }

            if (dirtyPiece != null)
            {
                dirtyPiece.RemovePc = captured;
                dirtyPiece.RemoveSq = capsq;
            }

            k ^= Zobrist.Psq[(byte)captured, (byte)capsq];
            _st.MaterialKey ^= Zobrist.Psq[(byte)captured, 8 + _pieceCount[(byte)captured] - (m.TypeOf != MoveType.EnPassant ? 1 : 0)];

            _st.Rule50 = 0;
        }
        else if (dirtyPiece != null)
        {
            dirtyPiece.RemoveSq = Square.None;
        }

        k ^= Zobrist.Psq[(byte)pc, (byte)from] ^ Zobrist.Psq[(byte)pc, (byte)to];

        if (_st.EpSquare != Square.None)
        {
            k ^= Zobrist.EnPassant[(byte)Types.FileOf(_st.EpSquare)];
            _st.EpSquare = Square.None;
        }

        k ^= Zobrist.Castling[(int)_st.CastlingRights];
        _st.CastlingRights &= ~(_castlingRightsMask[(byte)from] | _castlingRightsMask[(byte)to]);
        k ^= Zobrist.Castling[(int)_st.CastlingRights];

        if (Types.TypeOf(pc) == PieceType.Pawn)
        {
            if (((byte)to ^ (byte)from) == 16)
            {
                Square epSquare = Types.SubDirection(to, Types.PawnPush(us));
                ulong pawns = Attacks.PawnAttacksBb(epSquare, us) & Pieces(them, PieceType.Pawn);

                if (pawns != 0)
                {
                    Square ksq = SquareOf(PieceType.King, them);
                    ulong notBlockers = ~(_st.Previous!.BlockersForKing[(byte)them]);
                    bool noDiscovery = (Bitboards.SquareBB(from) & notBlockers) != 0 || Types.FileOf(from) == Types.FileOf(ksq);

                    if (noDiscovery && (pawns & (notBlockers | Attacks.LineOf(epSquare, ksq))) != 0)
                    {
                        _st.EpSquare = epSquare;
                        k ^= Zobrist.EnPassant[(byte)Types.FileOf(epSquare)];
                    }
                }
            }
            else if (m.TypeOf == MoveType.Promotion)
            {
                PieceType pt = m.PromotionType;
                Piece promotion = Types.MakePiece(us, pt);

                if (dirtyPiece != null)
                {
                    dirtyPiece.AddPc = promotion;
                    dirtyPiece.AddSq = to;
                    dirtyPiece.To = Square.None;
                }

                k ^= Zobrist.Psq[(byte)promotion, (byte)to];
                _st.MaterialKey ^= Zobrist.Psq[(byte)promotion, 8 + _pieceCount[(byte)promotion]]
                                 ^ Zobrist.Psq[(byte)pc, 8 + _pieceCount[(byte)pc] - 1];
                _st.NonPawnKey[(byte)us] ^= Zobrist.Psq[(byte)promotion, (byte)to];
                if (pt <= PieceType.Bishop)
                    _st.MinorPieceKey ^= Zobrist.Psq[(byte)promotion, (byte)to];
                _st.NonPawnMaterial[(byte)us] += Values.PieceValue[(byte)promotion];
            }

            _st.PawnKey ^= Zobrist.Psq[(byte)pc, (byte)from] ^ Zobrist.Psq[(byte)pc, (byte)to];
            _st.Rule50 = 0;
        }
        else
        {
            _st.NonPawnKey[(byte)us] ^= Zobrist.Psq[(byte)pc, (byte)from] ^ Zobrist.Psq[(byte)pc, (byte)to];
            if (Types.TypeOf(pc) <= PieceType.Bishop)
                _st.MinorPieceKey ^= Zobrist.Psq[(byte)pc, (byte)from] ^ Zobrist.Psq[(byte)pc, (byte)to];
        }

        _st.Key = k;

        if (m.TypeOf != MoveType.Castling)
        {
            Piece toPc = pc;
            if (m.TypeOf == MoveType.Promotion) toPc = Types.MakePiece(us, m.PromotionType);

            if (captured != Piece.None && m.TypeOf != MoveType.EnPassant)
            {
                RemovePiece(from, dirtyThreats);
                SwapPiece(to, toPc, dirtyThreats);
            }
            else if (pc == toPc)
            {
                MovePiece(from, to, dirtyThreats);
            }
            else
            {
                RemovePiece(from, dirtyThreats);
                PutPiece(toPc, to, dirtyThreats);
            }
        }

        _st.CapturedPiece = captured;
        _st.CheckersBB = givesCheck ? AttackersTo(SquareOf(PieceType.King, them)) & Pieces(us) : 0;

        _sideToMove = them;
        SetCheckInfo();

        // Calcola l'informazione di ripetizione — position.cpp:1053-1069: distanza in ply
        // dall'occorrenza precedente della stessa posizione, negativa nel caso di tripla
        // ripetizione (l'occorrenza precedente era già essa stessa una ripetizione), zero se la
        // posizione non si è ripetuta. Risale la catena Previous a due ply per volta (stesso lato
        // al tratto) fino a min(rule50, pliesFromNull) ply indietro.
        _st.Repetition = 0;
        int repEnd = Math.Min(_st.Rule50, _st.PliesFromNull);
        if (repEnd >= 4)
        {
            StateInfo? stp = _st.Previous!.Previous;
            for (int i = 4; i <= repEnd; i += 2)
            {
                stp = stp!.Previous!.Previous;
                if (stp!.Key == _st.Key)
                {
                    _st.Repetition = stp.Repetition != 0 ? -i : i;
                    break;
                }
            }
        }

        if (dirtyPawnPairs != null)
        {
            dirtyPawnPairs.After[(byte)Color.White] = Pieces(Color.White, PieceType.Pawn);
            dirtyPawnPairs.After[(byte)Color.Black] = Pieces(Color.Black, PieceType.Pawn);
        }
    }

    /// <summary>Disfa una mossa, riportando la posizione esattamente allo stato precedente —
    /// <c>Position::undo_move</c>, position.cpp:1086-1143.</summary>
    public void UndoMove(Move m)
    {
        _sideToMove = Types.Opposite(_sideToMove);
        Color us = _sideToMove;
        Square from = m.FromSq;
        Square to = m.ToSq;
        Piece pc = PieceOn(to);

        if (m.TypeOf == MoveType.Promotion)
        {
            pc = Types.MakePiece(us, PieceType.Pawn);
            SwapPiece(to, pc);
        }

        if (m.TypeOf == MoveType.Castling)
        {
            DoCastling(false, us, from, ref to, out _, out _);
        }
        else
        {
            MovePiece(to, from);

            if (_st.CapturedPiece != Piece.None)
            {
                Square capsq = to;
                if (m.TypeOf == MoveType.EnPassant)
                    capsq = Types.SubDirection(capsq, Types.PawnPush(us));

                PutPiece(_st.CapturedPiece, capsq);
            }
        }

        _st = _st.Previous!;
        _gamePly--;
    }

    /// <summary>"Mossa nulla": passa il turno senza muovere nulla sulla scacchiera —
    /// <c>Position::do_null_move</c>/<c>undo_null_move</c>, position.cpp:1344 e seguenti.</summary>
    public void DoNullMove(StateInfo newSt)
    {
        // Copia completa (non solo i campi "Copiati quando si fa una mossa"): una mossa nulla non
        // cambia nulla sulla scacchiera, quindi anche i campi "ricalcolati" restano validi finché
        // non li si aggiorna esplicitamente sotto — stesso std::memcpy(&newSt, st, sizeof(StateInfo))
        // della fonte (position.cpp:1349), qui campo per campo perché StateInfo è una classe.
        newSt.CopyMoveFieldsFrom(_st);
        newSt.Key = _st.Key;
        newSt.CheckersBB = _st.CheckersBB;
        Array.Copy(_st.BlockersForKing, newSt.BlockersForKing, Colors.Nb);
        Array.Copy(_st.Pinners, newSt.Pinners, Colors.Nb);
        Array.Copy(_st.CheckSquares, newSt.CheckSquares, PieceTypes.Nb);
        newSt.CapturedPiece = Piece.None;
        newSt.Repetition = 0;

        newSt.Previous = _st;
        _st = newSt;

        if (_st.EpSquare != Square.None)
        {
            _st.Key ^= Zobrist.EnPassant[(byte)Types.FileOf(_st.EpSquare)];
            _st.EpSquare = Square.None;
        }

        _st.Key ^= Zobrist.Side;
        _st.Rule50++;
        _st.PliesFromNull = 0;
        _sideToMove = Types.Opposite(_sideToMove);

        SetCheckInfo();
    }

    public void UndoNullMove()
    {
        _st = _st.Previous!;
        _sideToMove = Types.Opposite(_sideToMove);
    }

    /// <summary>Static Exchange Evaluation: vero se il guadagno netto di materiale della mossa m
    /// (una sequenza di catture/ricatture sulla stessa casa) è almeno <paramref name="threshold"/>
    /// — <c>Position::see_ge</c>, position.cpp:1389-1492. Usata per l'ordinamento delle mosse
    /// (Fase 2): scarta/ordina le catture in base a se "conviene" giocarle, senza dover eseguire
    /// davvero l'intera sequenza di catture sulla scacchiera.</summary>
    public bool SeeGe(Move m, int threshold = 0)
    {
        if (m.TypeOf != MoveType.Normal) return 0 >= threshold;

        Square from = m.FromSq, to = m.ToSq;

        int swap = Values.PieceValue[(byte)PieceOn(to)] - threshold;
        if (swap < 0) return false;

        swap = Values.PieceValue[(byte)PieceOn(from)] - swap;
        if (swap <= 0) return true;

        Color stm = _sideToMove;
        ulong occupied = Pieces() ^ Bitboards.SquareBB(from) ^ Bitboards.SquareBB(to);
        ulong attackers = AttackersTo(to, occupied);
        int res = 1;

        while (true)
        {
            stm = Types.Opposite(stm);
            attackers &= occupied;

            ulong stmAttackers = attackers & Pieces(stm);
            if (stmAttackers == 0) break;

            // Non permettere a un pezzo inchiodato di "attaccare" finché l'inchiodante è ancora
            // sulla sua casa originale.
            if ((Pinners(Types.Opposite(stm)) & occupied) != 0)
            {
                stmAttackers &= ~BlockersForKing(stm);
                if (stmAttackers == 0) break;
            }

            res ^= 1;

            ulong bb;
            if ((bb = stmAttackers & Pieces(PieceType.Pawn)) != 0)
            {
                if ((swap = Values.Pawn - swap) < res) break;
                occupied ^= Bitboards.LeastSignificantSquareBB(bb);
                attackers |= Attacks.AttacksBb(PieceType.Bishop, to, occupied) & Pieces(PieceType.Bishop, PieceType.Queen);
            }
            else if ((bb = stmAttackers & Pieces(PieceType.Knight)) != 0)
            {
                if ((swap = Values.Knight - swap) < res) break;
                occupied ^= Bitboards.LeastSignificantSquareBB(bb);
            }
            else if ((bb = stmAttackers & Pieces(PieceType.Bishop)) != 0)
            {
                if ((swap = Values.Bishop - swap) < res) break;
                occupied ^= Bitboards.LeastSignificantSquareBB(bb);
                attackers |= Attacks.AttacksBb(PieceType.Bishop, to, occupied) & Pieces(PieceType.Bishop, PieceType.Queen);
            }
            else if ((bb = stmAttackers & Pieces(PieceType.Rook)) != 0)
            {
                if ((swap = Values.Rook - swap) < res) break;
                occupied ^= Bitboards.LeastSignificantSquareBB(bb);
                attackers |= Attacks.AttacksBb(PieceType.Rook, to, occupied) & Pieces(PieceType.Rook, PieceType.Queen);
            }
            else if ((bb = stmAttackers & Pieces(PieceType.Queen)) != 0)
            {
                swap = Values.Queen - swap;
                occupied ^= Bitboards.LeastSignificantSquareBB(bb);
                var (bishopAttacks, rookAttacks) = BothAttacksBb(to, occupied);
                attackers |= (bishopAttacks & Pieces(PieceType.Bishop, PieceType.Queen)) | (rookAttacks & Pieces(PieceType.Rook, PieceType.Queen));
            }
            else // Re: se dopo aver "catturato" col re l'avversario ha ancora attaccanti, si inverte il risultato
            {
                return (attackers & ~Pieces(stm)) != 0 ? res == 0 : res != 0;
            }
        }

        return res != 0;
    }

    /// <summary>Esegue/disfa un arrocco — <c>Position::do_castling&lt;Do&gt;</c>,
    /// position.cpp:1311-1339. Rimuove entrambi i pezzi prima di riposizionarli (le case possono
    /// sovrapporsi in Chess960, es. la torre già sulla casa di arrivo del re).</summary>
    private void DoCastling(bool doIt, Color us, Square from, ref Square to, out Square rfrom, out Square rto,
        List<DirtyThreat>? dts = null, DirtyPiece? dp = null)
    {
        bool kingSide = to > from;
        rfrom = to; // l'arrocco è codificato come "il re cattura la propria torre"
        rto = Types.RelativeSquare(us, kingSide ? Square.F1 : Square.D1);
        to = Types.RelativeSquare(us, kingSide ? Square.G1 : Square.C1);

        // dp è popolato solo per "fare" la mossa (doIt=true), mai per "disfarla" — position.cpp:
        // 1324 "assert(!Do || dp)".
        if (doIt && dp != null)
        {
            dp.To = to;
            dp.RemovePc = dp.AddPc = Types.MakePiece(us, PieceType.Rook);
            dp.RemoveSq = rfrom;
            dp.AddSq = rto;
        }

        // Rimuove entrambi i pezzi prima di rimetterli — in Chess960 le case potrebbero
        // sovrapporsi (position.cpp:1334 "Remove both pieces first since squares could overlap").
        RemovePiece(doIt ? from : to, dts);
        RemovePiece(doIt ? rfrom : rto, dts);
        PutPiece(Types.MakePiece(us, PieceType.King), doIt ? to : from, dts);
        PutPiece(Types.MakePiece(us, PieceType.Rook), doIt ? rto : rfrom, dts);
    }
}

// Corrisponde a src/position.h + src/position.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// Semplificazioni deliberate rispetto alla fonte, tutte per rimandare a fasi successive del
// porting (vedi docs/porting-plan.md), non per correttezza dell'algoritmo di base:
// - NNUE (DirtyPiece/DirtyThreats/scratchDirties, i parametri "dts"/"dp" di put_piece/remove_piece/
//   move_piece/swap_piece/do_castling): del tutto assenti, non solo disattivati — la fase NNUE li
//   aggiungerà quando servirà l'aggiornamento incrementale delle feature.
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

    public int NonPawnMaterial(Color c) => _st.NonPawnMaterial[(byte)c];

    public int NonPawnMaterial() => NonPawnMaterial(Color.White) + NonPawnMaterial(Color.Black);

    public StateInfo State => _st;

    // --- Modifica della scacchiera — position.h:381-421, senza i parametri NNUE "dts"/"dp" (vedi
    // nota in cima al file: aggiunti nella fase NNUE) ---

    private void PutPiece(Piece pc, Square s)
    {
        _board[(byte)s] = pc;
        _byTypeBB[(byte)PieceType.AllPieces] |= Bitboards.SquareBB(s);
        _byTypeBB[(byte)Types.TypeOf(pc)] |= Bitboards.SquareBB(s);
        _byColorBB[(byte)Types.ColorOf(pc)] |= Bitboards.SquareBB(s);
        _pieceCount[(byte)pc]++;
        _pieceCount[(byte)Types.MakePiece(Types.ColorOf(pc), PieceType.AllPieces)]++;
    }

    private void RemovePiece(Square s)
    {
        Piece pc = _board[(byte)s];
        _byTypeBB[(byte)PieceType.AllPieces] ^= Bitboards.SquareBB(s);
        _byTypeBB[(byte)Types.TypeOf(pc)] ^= Bitboards.SquareBB(s);
        _byColorBB[(byte)Types.ColorOf(pc)] ^= Bitboards.SquareBB(s);
        _board[(byte)s] = Piece.None;
        _pieceCount[(byte)pc]--;
        _pieceCount[(byte)Types.MakePiece(Types.ColorOf(pc), PieceType.AllPieces)]--;
    }

    private void MovePiece(Square from, Square to)
    {
        Piece pc = _board[(byte)from];
        ulong fromTo = Bitboards.SquareBB(from) | Bitboards.SquareBB(to);
        _byTypeBB[(byte)PieceType.AllPieces] ^= fromTo;
        _byTypeBB[(byte)Types.TypeOf(pc)] ^= fromTo;
        _byColorBB[(byte)Types.ColorOf(pc)] ^= fromTo;
        _board[(byte)from] = Piece.None;
        _board[(byte)to] = pc;
    }

    private void SwapPiece(Square s, Piece pc)
    {
        RemovePiece(s);
        PutPiece(pc, s);
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

    public void DoMove(Move m, StateInfo newSt, bool givesCheck)
    {
        ulong k = _st.Key ^ Zobrist.Side;

        newSt.CopyMoveFieldsFrom(_st);
        newSt.Previous = _st;
        _st = newSt;

        _gamePly++;
        _st.Rule50++;
        _st.PliesFromNull++;

        Color us = _sideToMove;
        Color them = Types.Opposite(us);
        Square from = m.FromSq;
        Square to = m.ToSq;
        Piece pc = PieceOn(from);
        Piece captured = m.TypeOf == MoveType.EnPassant ? Types.MakePiece(them, PieceType.Pawn) : PieceOn(to);

        if (m.TypeOf == MoveType.Castling)
        {
            DoCastling(true, us, from, ref to, out Square rfrom, out Square rto);
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
                    RemovePiece(capsq);
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

            k ^= Zobrist.Psq[(byte)captured, (byte)capsq];
            _st.MaterialKey ^= Zobrist.Psq[(byte)captured, 8 + _pieceCount[(byte)captured] - (m.TypeOf != MoveType.EnPassant ? 1 : 0)];

            _st.Rule50 = 0;
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
                RemovePiece(from);
                SwapPiece(to, toPc);
            }
            else if (pc == toPc)
            {
                MovePiece(from, to);
            }
            else
            {
                RemovePiece(from);
                PutPiece(toPc, to);
            }
        }

        _st.CapturedPiece = captured;
        _st.CheckersBB = givesCheck ? AttackersTo(SquareOf(PieceType.King, them)) & Pieces(us) : 0;

        _sideToMove = them;
        SetCheckInfo();

        // Ripetizione: rimandata (vedi nota in cima al file) — repetition resta sempre 0 per ora.
        _st.Repetition = 0;
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

    /// <summary>Esegue/disfa un arrocco — <c>Position::do_castling&lt;Do&gt;</c>,
    /// position.cpp:1311-1339. Rimuove entrambi i pezzi prima di riposizionarli (le case possono
    /// sovrapporsi in Chess960, es. la torre già sulla casa di arrivo del re).</summary>
    private void DoCastling(bool doIt, Color us, Square from, ref Square to, out Square rfrom, out Square rto)
    {
        bool kingSide = to > from;
        rfrom = to; // l'arrocco è codificato come "il re cattura la propria torre"
        rto = Types.RelativeSquare(us, kingSide ? Square.F1 : Square.D1);
        to = Types.RelativeSquare(us, kingSide ? Square.G1 : Square.C1);

        RemovePiece(doIt ? from : to);
        RemovePiece(doIt ? rfrom : rto);
        PutPiece(Types.MakePiece(us, PieceType.King), doIt ? to : from);
        PutPiece(Types.MakePiece(us, PieceType.Rook), doIt ? rto : rfrom);
    }
}

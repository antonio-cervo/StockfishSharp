// Porting di Stockfish::Tablebases — decompress_pairs (tbprobe.cpp:620-744), do_probe_table
// (793-1021), probe_table (1428-1440), search&lt;CheckZeroingMoves&gt; (1455-1513), init
// (1521-1683), probe_wdl/probe_dtz (1693-1784), root_probe/root_probe_wdl/rank_root_moves
// (1787-1965). Vedi docs/syzygy-porting-plan.md per le fasi (qui: TB4, TB6, TB7, TB8, TB9) e le
// deviazioni dichiarate (per TB9: niente Search::RootMoves vere, vedi TbRootMove in TbTypes.cs).
// TB10 (wiring nel nodo di ricerca + opzioni UCI) resta da fare.

namespace StockfishSharp.Engine.Tablebases;

public static class Tablebase
{
    public static int MaxCardinality { get; internal set; }

    private static readonly TbTables Tables = new();

    /// <summary><c>Tablebases::init</c>, tbprobe.cpp:1521-1683.</summary>
    public static void Init(string paths)
    {
        Tables.Clear();
        MaxCardinality = 0;
        TbFile.Paths.Clear();

        if (!string.IsNullOrEmpty(paths))
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (string path in paths.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                TbFile.Paths.Add(path);
        }

        if (TbFile.Paths.Count == 0)
            return;

        TbConstants.BuildCombinatorialTables();

        // tbprobe.cpp:1639-1680 — tutte le combinazioni di pezzi fino a 6 non-Re (7 pezzi totali).
        for (PieceType p1 = PieceType.Pawn; p1 < PieceType.King; p1++)
        {
            Tables.Add([PieceType.King, p1, PieceType.King]);

            for (PieceType p2 = PieceType.Pawn; p2 <= p1; p2++)
            {
                Tables.Add([PieceType.King, p1, p2, PieceType.King]);
                Tables.Add([PieceType.King, p1, PieceType.King, p2]);

                for (PieceType p3 = PieceType.Pawn; p3 < PieceType.King; p3++)
                    Tables.Add([PieceType.King, p1, p2, PieceType.King, p3]);

                for (PieceType p3 = PieceType.Pawn; p3 <= p2; p3++)
                {
                    Tables.Add([PieceType.King, p1, p2, p3, PieceType.King]);

                    for (PieceType p4 = PieceType.Pawn; p4 <= p3; p4++)
                    {
                        Tables.Add([PieceType.King, p1, p2, p3, p4, PieceType.King]);

                        for (PieceType p5 = PieceType.Pawn; p5 <= p4; p5++)
                            Tables.Add([PieceType.King, p1, p2, p3, p4, p5, PieceType.King]);

                        for (PieceType p5 = PieceType.Pawn; p5 < PieceType.King; p5++)
                            Tables.Add([PieceType.King, p1, p2, p3, p4, PieceType.King, p5]);
                    }

                    for (PieceType p4 = PieceType.Pawn; p4 < PieceType.King; p4++)
                    {
                        Tables.Add([PieceType.King, p1, p2, p3, PieceType.King, p4]);

                        for (PieceType p5 = PieceType.Pawn; p5 <= p4; p5++)
                            Tables.Add([PieceType.King, p1, p2, p3, PieceType.King, p4, p5]);
                    }
                }

                for (PieceType p3 = PieceType.Pawn; p3 <= p1; p3++)
                    for (PieceType p4 = PieceType.Pawn; p4 <= (p1 == p3 ? p2 : p3); p4++)
                        Tables.Add([PieceType.King, p1, p2, PieceType.King, p3, p4]);
            }
        }

        Tables.Info();
    }

    // --- decompress_pairs, tbprobe.cpp:620-744 ---
    private static int DecompressPairs(PairsData d, ulong idx)
    {
        if ((d.Flags & (byte)TbFlag.SingleValue) != 0)
            return d.MinSymLen;

        byte[] file = d.RawData;

        uint k = (uint)(idx / (ulong)d.Span);
        int offset = TbBinary.ReadU16Le(file, d.SparseIndexOffset + (int)k * 6 + 4);
        uint block = TbBinary.ReadU32Le(file, d.SparseIndexOffset + (int)k * 6);
        long diff = (long)(idx % (ulong)d.Span) - d.Span / 2;
        offset += (int)diff;

        if (block >= d.BlockLengthSize)
            block = d.BlockLengthSize - 1;

        while (offset < 0 && block > 0)
        {
            block--;
            offset += TbBinary.ReadU16Le(file, d.BlockLengthOffset + (int)block * 2) + 1;
        }
        while (offset > TbBinary.ReadU16Le(file, d.BlockLengthOffset + (int)block * 2) && block + 1 < d.BlockLengthSize)
        {
            offset -= TbBinary.ReadU16Le(file, d.BlockLengthOffset + (int)block * 2) + 1;
            block++;
        }

        int ptr = d.DataOffset + (int)((long)block * d.SizeofBlock);

        ulong buf64 = ptr + 8 <= d.DataEndOffset ? TbBinary.ReadU64Be(file, ptr) : 0;
        ptr += 8;
        int buf64Size = 64;
        ushort sym;

        while (true)
        {
            int len = 0;
            while (buf64 < d.Base64[len]) len++;

            sym = (ushort)((buf64 - d.Base64[len]) >> (64 - len - d.MinSymLen));
            sym += TbBinary.ReadU16Le(file, d.LowestSymOffset + len * 2);
            sym &= TbConstants.SymCount - 1;

            if (offset < d.SymLen[sym] + 1)
                break;

            offset -= d.SymLen[sym] + 1;
            len += d.MinSymLen;
            buf64 <<= len;
            buf64Size -= len;

            if (buf64Size <= 32)
            {
                buf64Size += 32;
                if (ptr + 4 <= d.DataEndOffset)
                    buf64 |= (ulong)TbBinary.ReadU32Be(file, ptr) << (64 - buf64Size);
                ptr += 4;
            }
        }

        while (d.SymLen[sym] != 0)
        {
            ushort left = d.Btree.Left[sym];
            if (offset < d.SymLen[left] + 1)
                sym = left;
            else
            {
                offset -= d.SymLen[left] + 1;
                sym = d.Btree.Right[sym];
            }
        }
        return d.Btree.Left[sym];
    }

    // --- do_probe_table, tbprobe.cpp:793-1021 ---
    private static int DoProbeTable(Position pos, TbTable entry, WdlScore wdl, ref ProbeState result)
    {
        var squares = new Square[TbConstants.TbPieces];
        var pieces = new Piece[TbConstants.TbPieces];
        ulong idx;
        int size = 0, leadPawnsCnt = 0;
        ulong leadPawns = 0;
        File tbFile = File.A;

        bool symmetricBlackToMove = entry.Key == entry.Key2 && pos.SideToMove == Color.Black;
        bool blackStronger = pos.MaterialKey != entry.Key;
        int flipColor = (symmetricBlackToMove || blackStronger) ? 8 : 0;
        int flipSquares = (symmetricBlackToMove || blackStronger) ? 56 : 0;
        int stm = ((symmetricBlackToMove || blackStronger) ? 1 : 0) ^ (byte)pos.SideToMove;

        if (entry.HasPawns)
        {
            Piece pc = (Piece)((byte)entry.Get(0, 0).Pieces[0] ^ flipColor);
            ulong b = pos.Pieces(Types.ColorOf(pc), PieceType.Pawn);
            leadPawns = b;
            do
                squares[size++] = (Square)((byte)Bitboards.PopLsb(ref b) ^ flipSquares);
            while (b != 0);

            leadPawnsCnt = size;

            int maxIdx = TbConstants.MaxPawnsCompIndex(squares, 0, leadPawnsCnt);
            (squares[0], squares[maxIdx]) = (squares[maxIdx], squares[0]);

            tbFile = (File)Bitboards.EdgeDistance(Types.FileOf(squares[0]));
        }

        if (!entry.CheckDtzStm(stm, tbFile))
        {
            result = ProbeState.ChangeStm;
            return 0;
        }

        {
            ulong b = pos.Pieces() ^ leadPawns;
            do
            {
                Square s = Bitboards.PopLsb(ref b);
                squares[size] = (Square)((byte)s ^ flipSquares);
                pieces[size++] = (Piece)((byte)pos.PieceOn(s) ^ flipColor);
            } while (b != 0);
        }

        PairsData d = entry.Get(stm, (int)tbFile);

        for (int i = leadPawnsCnt; i < size - 1; i++)
            for (int j = i + 1; j < size; j++)
                if (d.Pieces[i] == pieces[j])
                {
                    (pieces[i], pieces[j]) = (pieces[j], pieces[i]);
                    (squares[i], squares[j]) = (squares[j], squares[i]);
                    break;
                }

        if (Types.FileOf(squares[0]) > File.D)
            for (int i = 0; i < size; i++)
                squares[i] = Types.FlipFile(squares[i]);

        if (entry.HasPawns)
        {
            idx = (ulong)TbConstants.LeadPawnIdx[leadPawnsCnt, (byte)squares[0]];

            TbConstants.StableSortByMapPawns(squares, 1, leadPawnsCnt - 1);

            for (int i = 1; i < leadPawnsCnt; i++)
                idx += (ulong)TbConstants.Binomial[i, TbConstants.MapPawns[(byte)squares[i]]];
        }
        else
        {
            if (Types.RankOf(squares[0]) > Rank.Rank4)
                for (int i = 0; i < size; i++)
                    squares[i] = Types.FlipRank(squares[i]);

            for (int i = 0; i < d.GroupLen[0]; i++)
            {
                if (TbConstants.OffA1H8(squares[i]) == 0) continue;

                if (TbConstants.OffA1H8(squares[i]) > 0)
                    for (int j = i; j < size; j++)
                        squares[j] = (Square)((((byte)squares[j] >> 3) | ((byte)squares[j] << 3)) & 63);
                break;
            }

            if (entry.HasUniquePieces)
            {
                int adjust1 = squares[1] > squares[0] ? 1 : 0;
                int adjust2 = (squares[2] > squares[0] ? 1 : 0) + (squares[2] > squares[1] ? 1 : 0);

                if (TbConstants.OffA1H8(squares[0]) != 0)
                    idx = (ulong)((TbConstants.MapA1D1D4[(byte)squares[0]] * 63 + ((byte)squares[1] - adjust1)) * 62
                        + (byte)squares[2] - adjust2);
                else if (TbConstants.OffA1H8(squares[1]) != 0)
                    idx = (ulong)((6 * 63 + (byte)Types.RankOf(squares[0]) * 28 + TbConstants.MapB1H1H7[(byte)squares[1]]) * 62
                        + (byte)squares[2] - adjust2);
                else if (TbConstants.OffA1H8(squares[2]) != 0)
                    idx = (ulong)(6 * 63 * 62 + 4 * 28 * 62 + (byte)Types.RankOf(squares[0]) * 7 * 28
                        + ((byte)Types.RankOf(squares[1]) - adjust1) * 28 + TbConstants.MapB1H1H7[(byte)squares[2]]);
                else
                    idx = (ulong)(6 * 63 * 62 + 4 * 28 * 62 + 4 * 7 * 28 + (byte)Types.RankOf(squares[0]) * 7 * 6
                        + ((byte)Types.RankOf(squares[1]) - adjust1) * 6 + ((byte)Types.RankOf(squares[2]) - adjust2));
            }
            else
                idx = (ulong)TbConstants.MapKK[TbConstants.MapA1D1D4[(byte)squares[0]], (byte)squares[1]];
        }

        idx *= d.GroupIdx[0];
        int groupStart = d.GroupLen[0];
        bool remainingPawns = entry.HasPawns && entry.PawnCount[1] != 0;

        int next = 0;
        while (d.GroupLen[++next] != 0)
        {
            Array.Sort(squares, groupStart, d.GroupLen[next]);
            ulong n = 0;
            for (int i = 0; i < d.GroupLen[next]; i++)
            {
                int adjust = 0;
                for (int si = 0; si < groupStart; si++)
                    if (squares[groupStart + i] > squares[si]) adjust++;
                n += (ulong)TbConstants.Binomial[i + 1, (byte)squares[groupStart + i] - adjust - (remainingPawns ? 8 : 0)];
            }
            remainingPawns = false;
            idx += n * d.GroupIdx[next];
            groupStart += d.GroupLen[next];
        }

        return entry.MapScore(tbFile, DecompressPairs(d, idx), wdl);
    }

    private static int ProbeWdlTable(Position pos, ref ProbeState result)
    {
        if (Bitboards.PopCount(pos.Pieces()) == 2) return (int)WdlScore.Draw;

        var entry = Tables.GetWdl(pos.MaterialKey);
        if (entry is null || entry.Mapped(pos, isWdl: true) is null)
        {
            result = ProbeState.Fail;
            return 0;
        }
        return DoProbeTable(pos, entry, WdlScore.Draw, ref result);
    }

    private static int ProbeDtzTable(Position pos, WdlScore wdl, ref ProbeState result)
    {
        if (Bitboards.PopCount(pos.Pieces()) == 2) return 0;

        var entry = Tables.GetDtz(pos.MaterialKey);
        if (entry is null || entry.Mapped(pos, isWdl: false) is null)
        {
            result = ProbeState.Fail;
            return 0;
        }
        return DoProbeTable(pos, entry, wdl, ref result);
    }

    // --- search<CheckZeroingMoves>, tbprobe.cpp:1455-1513 ---
    private static WdlScore SearchWdl(Position pos, bool checkZeroingMoves, ref ProbeState result)
    {
        WdlScore bestValue = WdlScore.Loss;
        var st = new StateInfo();

        List<Move> moveList = [];
        MoveGen.Generate(GenType.Legal, pos, moveList);
        int totalCount = moveList.Count, moveCount = 0;

        foreach (Move move in moveList)
        {
            if (!pos.Capture(move) && (!checkZeroingMoves || Types.TypeOf(pos.MovedPiece(move)) != PieceType.Pawn))
                continue;

            moveCount++;
            pos.DoMove(move, st);
            WdlScore value = (WdlScore)(-(int)SearchWdl(pos, false, ref result));
            pos.UndoMove(move);

            if (result == ProbeState.Fail)
                return WdlScore.Draw;

            if (value > bestValue)
            {
                bestValue = value;
                if (value >= WdlScore.Win)
                {
                    result = ProbeState.ZeroingBestMove;
                    return value;
                }
            }
        }

        bool noMoreMoves = moveCount != 0 && moveCount == totalCount;
        WdlScore finalValue;
        if (noMoreMoves)
            finalValue = bestValue;
        else
        {
            finalValue = (WdlScore)ProbeWdlTable(pos, ref result);
            if (result == ProbeState.Fail)
                return WdlScore.Draw;
        }

        if (bestValue >= finalValue)
        {
            result = bestValue > WdlScore.Draw || noMoreMoves ? ProbeState.ZeroingBestMove : ProbeState.Ok;
            return bestValue;
        }
        result = ProbeState.Ok;
        return finalValue;
    }

    /// <summary><c>Tablebases::probe_wdl</c>, tbprobe.cpp:1693-1697.</summary>
    public static WdlScore ProbeWdl(Position pos, out ProbeState result)
    {
        result = ProbeState.Ok;
        return SearchWdl(pos, false, ref result);
    }

    /// <summary><c>Tablebases::probe_dtz</c>, tbprobe.cpp:1725-1784.</summary>
    public static int ProbeDtz(Position pos, out ProbeState result)
    {
        result = ProbeState.Ok;
        WdlScore wdl = SearchWdl(pos, true, ref result);

        if (result == ProbeState.Fail || wdl == WdlScore.Draw)
            return 0;
        if (result == ProbeState.ZeroingBestMove)
            return TbConstants.DtzBeforeZeroing(wdl);

        int dtz = ProbeDtzTable(pos, wdl, ref result);
        if (result == ProbeState.Fail)
            return 0;
        if (result != ProbeState.ChangeStm)
            return (dtz + 100 * ((wdl == WdlScore.BlessedLoss || wdl == WdlScore.CursedWin) ? 1 : 0))
                * TbConstants.SignOf((int)wdl);

        var st = new StateInfo();
        int minDtz = 0xFFFF;

        List<Move> moves = [];
        MoveGen.Generate(GenType.Legal, pos, moves);
        foreach (Move move in moves)
        {
            bool zeroing = pos.Capture(move) || Types.TypeOf(pos.MovedPiece(move)) == PieceType.Pawn;
            pos.DoMove(move, st);

            if (zeroing)
                dtz = -TbConstants.DtzBeforeZeroing(SearchWdl(pos, false, ref result));
            else
                dtz = -ProbeDtz(pos, out result);

            if (dtz == 1 && pos.Checkers() != 0)
            {
                List<Move> replies = [];
                MoveGen.Generate(GenType.Legal, pos, replies);
                if (replies.Count == 0)
                    minDtz = 1;
            }

            if (!zeroing)
                dtz += TbConstants.SignOf(dtz);

            if (dtz < minDtz && TbConstants.SignOf(dtz) == TbConstants.SignOf((int)wdl))
                minDtz = dtz;

            pos.UndoMove(move);

            if (result == ProbeState.Fail)
                return 0;
        }

        return minDtz == 0xFFFF ? -1 : minDtz;
    }

    /// <summary><c>Position::dtz_is_dtm</c>, position.h:346-349 — non un metodo di
    /// <c>Position</c> in questo porting (usato solo qui), tenuto come helper privato.</summary>
    private static bool DtzIsDtm(Position pos)
    {
        int pieceCount = Bitboards.PopCount(pos.Pieces());
        return pos.Pieces(PieceType.Pawn) == 0
            && (pieceCount == 3 || (pieceCount == 4 && pos.Pieces(PieceType.Queen, PieceType.Rook) == 0));
    }

    /// <summary><c>Tablebases::root_probe</c>, tbprobe.cpp:1787-1863 — usa le tabelle DTZ per
    /// ordinare le mosse alla radice. Ritorna <c>false</c> se un probe è fallito o è scaduto il
    /// tempo (<paramref name="timeAbort"/>).</summary>
    public static bool RootProbe(Position pos, List<TbRootMove> rootMoves, bool rule50, bool rankDtz, Func<bool> timeAbort)
    {
        ProbeState result = ProbeState.Ok;
        var st = new StateInfo();

        int cnt50 = pos.Rule50Count;
        bool rep = pos.HasRepeated();
        int bound = rule50 ? (TbConstants.MaxDtz / 2 - 100) : 1;

        foreach (var m in rootMoves)
        {
            pos.DoMove(m.Move, st);

            int dtz;
            if (pos.Rule50Count == 0)
            {
                // Mossa che azzera il conteggio: dtz è uno fra -101/-1/0/1/101.
                WdlScore wdl = (WdlScore)(-(int)ProbeWdl(pos, out result));
                dtz = TbConstants.DtzBeforeZeroing(wdl);
            }
            else if ((rule50 && pos.IsDraw(1)) || pos.IsRepetition(1))
            {
                // Patta per ripetizione/50 mosse a un ply dalla radice: dev'essere una vera
                // tripla ripetizione nella storia della partita.
                dtz = 0;
            }
            else
            {
                dtz = -ProbeDtz(pos, out result);
                dtz = dtz > 0 ? dtz + 1 : dtz < 0 ? dtz - 1 : dtz;
            }

            // Assicura che una mossa di matto riceva sempre dtz=1 (l'aggiustamento sopra la
            // porta a 2 quando probe_dtz sulla posizione già matta ritorna -1, "il lato di
            // turno è matto").
            if (pos.Checkers() != 0 && dtz == 2)
            {
                List<Move> replies = [];
                MoveGen.Generate(GenType.Legal, pos, replies);
                if (replies.Count == 0)
                    dtz = 1;
            }

            pos.UndoMove(m.Move);

            if (timeAbort() || result == ProbeState.Fail)
                return false;

            // Le mosse migliori sono classificate più in alto. Le vittorie certe sono
            // classificate alla pari. Le mosse perdenti sono alla pari a meno che non si
            // profili una patta per 50 mosse.
            int r = dtz > 0
                ? (dtz + cnt50 <= 99 && !rep ? TbConstants.MaxDtz - (rankDtz ? dtz : 0)
                                             : TbConstants.MaxDtz / 2 - (dtz + cnt50))
                : dtz < 0
                    ? (-dtz * 2 + cnt50 < 100 ? -TbConstants.MaxDtz - (rankDtz ? dtz : 0)
                                              : -TbConstants.MaxDtz / 2 + (-dtz + cnt50))
                    : 0;
            m.TbRank = r;

            // Punteggio da mostrare per questa mossa: almeno 1 cp per le vittorie "maledette",
            // fino a 49 cp avvicinandosi a una vittoria vera.
            m.TbScore = r >= bound ? Values.Mate - Ply.MaxPly - 1
                : r > 0 ? Math.Max(3, r - (TbConstants.MaxDtz / 2 - 200)) * Values.Pawn / 200
                : r == 0 ? Values.Draw
                : r > -bound ? Math.Min(-3, r + (TbConstants.MaxDtz / 2 - 200)) * Values.Pawn / 200
                : -Values.Mate + Ply.MaxPly + 1;
        }

        return true;
    }

    /// <summary><c>Tablebases::root_probe_wdl</c>, tbprobe.cpp:1866-1902 — riserva usata quando
    /// mancano (in tutto o in parte) le tabelle DTZ.</summary>
    public static bool RootProbeWdl(Position pos, List<TbRootMove> rootMoves, bool rule50)
    {
        int[] wdlToRank = [-TbConstants.MaxDtz, -TbConstants.MaxDtz + 101, 0, TbConstants.MaxDtz - 101, TbConstants.MaxDtz];

        ProbeState result = ProbeState.Ok;
        var st = new StateInfo();

        foreach (var m in rootMoves)
        {
            pos.DoMove(m.Move, st);

            WdlScore wdl = pos.IsDraw(1) ? WdlScore.Draw : (WdlScore)(-(int)ProbeWdl(pos, out result));

            pos.UndoMove(m.Move);

            if (result == ProbeState.Fail)
                return false;

            m.TbRank = wdlToRank[(int)wdl + 2];

            if (!rule50)
                wdl = wdl > WdlScore.Draw ? WdlScore.Win : wdl < WdlScore.Draw ? WdlScore.Loss : WdlScore.Draw;
            m.TbScore = TbConstants.WdlToValue[(int)wdl + 2];
        }

        return true;
    }

    /// <summary><c>Tablebases::rank_root_moves</c>, tbprobe.cpp:1904-1965 — senza
    /// <c>OptionsMap</c> (non presente in questo porting): le tre opzioni UCI diventano
    /// parametri espliciti, passati dal chiamante (Flow A4/TB10).</summary>
    public static TbConfig RankRootMoves(Position pos, List<TbRootMove> rootMoves,
        bool syzygy50MoveRule, int syzygyProbeDepth, int syzygyProbeLimit,
        bool rankDtz = false, Func<bool>? timeAbort = null)
    {
        timeAbort ??= static () => false;
        var config = new TbConfig();
        if (rootMoves.Count == 0) return config;

        config.RootInTb = false;
        config.UseRule50 = syzygy50MoveRule;
        config.ProbeDepth = syzygyProbeDepth;
        config.Cardinality = syzygyProbeLimit;

        bool dtzAvailable = true;

        // Le tabelle con meno pezzi di SyzygyProbeLimit sono sondate con probeDepth == 0.
        if (config.Cardinality > MaxCardinality)
        {
            config.Cardinality = MaxCardinality;
            config.ProbeDepth = 0;
        }

        if (config.Cardinality >= Bitboards.PopCount(pos.Pieces()) && !pos.CanCastle(CastlingRights.AnyCastling))
        {
            // Usa DTZ per ordinare le mosse se il matto è l'unica mossa che azzera il conteggio.
            rankDtz = rankDtz || DtzIsDtm(pos);

            config.RootInTb = RootProbe(pos, rootMoves, syzygy50MoveRule, rankDtz, timeAbort);

            if (!config.RootInTb && !timeAbort())
            {
                // Le tabelle DTZ mancano: prova a ordinare le mosse con le tabelle WDL.
                dtzAvailable = false;
                config.RootInTb = RootProbeWdl(pos, rootMoves, syzygy50MoveRule);
            }
        }

        if (config.RootInTb)
        {
            // std::stable_sort — LINQ OrderByDescending è garantito stabile.
            var sorted = rootMoves.OrderByDescending(m => m.TbRank).ToList();
            rootMoves.Clear();
            rootMoves.AddRange(sorted);

            // Sonda durante la ricerca solo se DTZ non è disponibile e si sta vincendo.
            if (dtzAvailable || rootMoves[0].TbScore <= Values.Draw)
                config.Cardinality = 0;
        }
        else
        {
            // Pulizia se sia root_probe() sia root_probe_wdl() sono falliti.
            foreach (var m in rootMoves)
                m.TbRank = 0;
        }

        return config;
    }
}

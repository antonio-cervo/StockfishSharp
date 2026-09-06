// Porting fedele di src/movepick.h + src/movepick.cpp — generazione "a stadi" delle mosse
// pseudo-legali: mossa di TT prima (se pseudo-legale), poi catture "buone" (SEE ok) ordinate per
// history+MVV, poi mosse quiete "buone" ordinate per le history a più livelli, poi le catture
// "cattive" scartate dal filtro SEE, infine le mosse quiete rimanenti. Sostituisce la generazione
// eager (MoveGen.Generate(GenType.Legal,...) + MovePick.OrderMoves) usata finora in Search.cs —
// MovePick.cs resta, come nella fonte (lì è "Worker"), solo il contenitore delle tabelle di
// history: questa classe le legge ma non le possiede.
//
// Non portata l'ottimizzazione SIMD AVX-512 di partial_insertion_sort (struct MoveSorter, dietro
// #ifdef USE_AVX512 nella fonte) — stesso trattamento di ogni altro codice SIMD-specifico in
// questo porting (vedi nota in testa a MoveGen.cs): qui solo il ramo scalare, quello eseguito
// comunque dalla fonte reale su CPU senza AVX-512.
//
// A differenza della fonte, che genera le mosse pseudo-legali e lascia al chiamante (search.cpp)
// il filtro pos.legal(move), anche qui NextMove() restituisce mosse pseudo-legali: il filtro va
// fatto nel chiamante, esattamente come nella fonte.
//
// Differenza pratica (non di fedeltà): la fonte tiene "moves[MAX_MOVES]" sullo STACK C++, quindi a
// costo zero di allocazione a ogni nodo. Qui i tre buffer (moves/values/genBuffer) sono passati dal
// chiamante (Search, un buffer per livello di profondità, riusato fra tutti i nodi a quel ply)
// invece di essere allocati `new` a ogni MovePicker — altrimenti ogni nodo della ricerca
// allocherebbe ~1.5KB sull'heap, pressione notevole sul GC su un motore che visita milioni di nodi.

namespace StockfishSharp.Engine;

public sealed class MovePicker
{
    // Stages, movepick.cpp:33-57 — ordine identico alla fonte: l'aritmetica su stage (++stage,
    // stage = X + condizione) dipende da questo ordinamento esatto.
    private enum Stage
    {
        MainTt, CaptureInit, GoodCapture, QuietInit, GoodQuiet, BadCapture, BadQuiet,
        EvasionTt, EvasionInit, Evasion,
        ProbcutTt, ProbcutInit, Probcut,
        QsearchTt, QcaptureInit, QCapture,
    }

    private const int GoodQuietThreshold = -14000; // movepick.cpp:280
    private const int LowPlyHistorySize = 5; // LOW_PLY_HISTORY_SIZE, history.h

    private readonly Position _pos;
    private readonly MovePick _hist;
    private readonly ContinuationRef[] _contRefs = new ContinuationRef[6];
    private readonly Move _ttMove;
    private readonly int _depth;
    private readonly int _ply;
    private readonly int _threshold;

    private Stage _stage;
    private bool _skipQuiets;

    // ExtMove moves[MAX_MOVES] della fonte diventa qui due array paralleli (mossa/valore) indicati
    // per indice invece che per puntatore — stessa aritmetica di cur/endCur/endBadCaptures/
    // endCaptures/endGenerated, solo come interi invece che puntatori. Passati dal chiamante (vedi
    // nota sopra), non allocati qui.
    private readonly Move[] _moves;
    private readonly int[] _values;
    private readonly List<Move> _genBuffer;
    private int _cur, _endCur, _endBadCaptures, _endCaptures, _endGenerated;

    /// <summary>Costruttore per ricerca principale e quiescenza — movepick.cpp:153-177.
    /// <paramref name="movesBuf"/>/<paramref name="valuesBuf"/> (dimensione <see
    /// cref="Ply.MaxMoves"/>) e <paramref name="genBuffer"/> sono buffer riusati dal chiamante per
    /// livello di profondità — vedi nota in testa al file.</summary>
    public MovePicker(Position pos, MovePick hist, Move ttMove, int depth, int ply, ContinuationRef[] contRefs,
        Move[] movesBuf, int[] valuesBuf, List<Move> genBuffer)
    {
        _pos = pos;
        _hist = hist;
        _ttMove = ttMove;
        _depth = depth;
        _ply = ply;
        Array.Copy(contRefs, _contRefs, 6);
        _moves = movesBuf;
        _values = valuesBuf;
        _genBuffer = genBuffer;

        bool ttOk = ttMove != Move.None && pos.PseudoLegal(ttMove);
        _stage = pos.Checkers() != 0
            ? Stage.EvasionTt + (ttOk ? 0 : 1)
            : (depth > 0 ? Stage.MainTt : Stage.QsearchTt) + (ttOk ? 0 : 1);
    }

    /// <summary>Costruttore per ProbCut — movepick.cpp:181-189: genera solo catture con SEE almeno
    /// pari alla soglia data.</summary>
    public MovePicker(Position pos, MovePick hist, Move ttMove, int threshold,
        Move[] movesBuf, int[] valuesBuf, List<Move> genBuffer)
    {
        _pos = pos;
        _hist = hist;
        _ttMove = ttMove;
        _threshold = threshold;
        _moves = movesBuf;
        _values = valuesBuf;
        _genBuffer = genBuffer;

        bool ttOk = ttMove != Move.None && pos.CaptureStage(ttMove) && pos.PseudoLegal(ttMove);
        _stage = Stage.ProbcutTt + (ttOk ? 0 : 1);
    }

    public void SkipQuietMoves() => _skipQuiets = true;

    /// <summary>Emette una mossa pseudo-legale alla volta, nell'ordine di merito stimato, finché
    /// non ce ne sono più (<see cref="Move.None"/>) — movepick.cpp:278-379. Non restituisce mai la
    /// mossa di TT una seconda volta (già emessa nello stadio *_TT).</summary>
    public Move NextMove()
    {
        while (true)
        {
            switch (_stage)
            {
                case Stage.MainTt:
                case Stage.EvasionTt:
                case Stage.QsearchTt:
                case Stage.ProbcutTt:
                    _stage++;
                    return _ttMove;

                case Stage.CaptureInit:
                case Stage.ProbcutInit:
                case Stage.QcaptureInit:
                    _cur = _endBadCaptures = 0;
                    _endCur = _endCaptures = ScoreCaptures();
                    PartialInsertionSort(_cur, _endCur, int.MinValue);
                    _stage++;
                    continue; // goto top

                case Stage.GoodCapture:
                {
                    Move? gc = Select(() =>
                    {
                        if (_pos.SeeGe(_moves[_cur], -_values[_cur] / 18))
                            return true;

                        int idx = _endBadCaptures;
                        (_moves[idx], _moves[_cur]) = (_moves[_cur], _moves[idx]);
                        (_values[idx], _values[_cur]) = (_values[_cur], _values[idx]);
                        _endBadCaptures++;
                        return false;
                    });
                    if (gc.HasValue) return gc.Value;

                    _stage++;
                    goto case Stage.QuietInit;
                }

                case Stage.QuietInit:
                    if (!_skipQuiets)
                    {
                        _endCur = _endGenerated = ScoreQuiets();
                        PartialInsertionSort(_cur, _endCur, -3560 * _depth);
                    }

                    _stage++;
                    goto case Stage.GoodQuiet;

                case Stage.GoodQuiet:
                {
                    if (!_skipQuiets)
                    {
                        Move? gq = Select(() => _values[_cur] > GoodQuietThreshold);
                        if (gq.HasValue) return gq.Value;
                    }

                    _cur = 0;
                    _endCur = _endBadCaptures;
                    _stage++;
                    goto case Stage.BadCapture;
                }

                case Stage.BadCapture:
                {
                    Move? bc = Select(() => true);
                    if (bc.HasValue) return bc.Value;

                    _cur = _endCaptures;
                    _endCur = _endGenerated;
                    _stage++;
                    goto case Stage.BadQuiet;
                }

                case Stage.BadQuiet:
                    if (!_skipQuiets)
                        return Select(() => _values[_cur] <= GoodQuietThreshold) ?? Move.None;
                    return Move.None;

                case Stage.EvasionInit:
                    _cur = 0;
                    _endCur = _endGenerated = ScoreEvasions();
                    PartialInsertionSort(_cur, _endCur, int.MinValue);
                    _stage++;
                    goto case Stage.Evasion;

                case Stage.Evasion:
                case Stage.QCapture:
                    return Select(() => true) ?? Move.None;

                case Stage.Probcut:
                    return Select(() => _pos.SeeGe(_moves[_cur], _threshold)) ?? Move.None;

                default:
                    return Move.None; // assert(false) nella fonte — irraggiungibile
            }
        }
    }

    /// <summary>MovePicker::select, movepick.cpp:266-273 — non restituisce mai la mossa di TT
    /// (già emessa). <paramref name="filter"/> legge/modifica lo stato tramite <see cref="_cur"/>,
    /// come il lambda catturante per riferimento della fonte.</summary>
    private Move? Select(Func<bool> filter)
    {
        while (_cur < _endCur)
        {
            Move candidate = _moves[_cur];
            bool ok = candidate != _ttMove && filter();
            _cur++;
            if (ok) return candidate;
        }

        return null;
    }

    /// <summary>partial_insertion_sort, movepick.cpp:111-143 (solo ramo scalare) — ordina in modo
    /// decrescente le mosse con valore &gt;= limit, lasciando le altre in ordine non specificato.</summary>
    private void PartialInsertionSort(int begin, int end, int limit)
    {
        int sortedEnd = begin;
        for (int p = begin + 1; p < end; p++)
        {
            if (_values[p] >= limit)
            {
                Move tmpM = _moves[p];
                int tmpV = _values[p];

                sortedEnd++;
                _moves[p] = _moves[sortedEnd];
                _values[p] = _values[sortedEnd];

                int q = sortedEnd;
                while (q != begin && _values[q - 1] < tmpV)
                {
                    _moves[q] = _moves[q - 1];
                    _values[q] = _values[q - 1];
                    q--;
                }

                _moves[q] = tmpM;
                _values[q] = tmpV;
            }
        }
    }

    /// <summary>score&lt;CAPTURES&gt;, movepick.cpp:224-226 — MVV (7×valore del pezzo catturato) +
    /// CapturePieceToHistory. Scrive a partire dall'indice <see cref="_cur"/> corrente (non lo
    /// azzera: lo fa il chiamante prima, come <c>cur = endBadCaptures = moves</c> nella fonte).</summary>
    private int ScoreCaptures()
    {
        _genBuffer.Clear();
        MoveGen.Generate(GenType.Captures, _pos, _genBuffer);

        int it = _cur;
        foreach (var m in _genBuffer)
        {
            Square to = m.ToSq;
            Piece pc = _pos.MovedPiece(m);
            Piece capturedPiece = _pos.PieceOn(to);

            _moves[it] = m;
            _values[it] = _hist.GetCaptureHistory(pc, to, Types.TypeOf(capturedPiece))
                        + (7 * Values.PieceValue[(byte)capturedPiece]);
            it++;
        }

        return it;
    }

    /// <summary>score&lt;QUIETS&gt;, movepick.cpp:228-250 — main history, pawn history,
    /// continuation history ai livelli 0,1,2,3,5 (il livello 4/ss-5 è saltato di proposito, come
    /// nella fonte), bonus per le mosse che danno scacco con SEE non troppo negativa, bonus/malus
    /// per muoversi verso/via da una casa minacciata da un pezzo di valore inferiore, bonus di
    /// low-ply history per i primi pochi ply.</summary>
    private int ScoreQuiets()
    {
        Color us = _pos.SideToMove;
        Color them = Types.Opposite(us);

        // threatByLesser[KING+1], movepick.cpp:201-210 — indicizzato per PieceType (0=None/King
        // non usati, restano 0 come nella fonte).
        var threatByLesser = new ulong[7];
        ulong pawnThreat = _pos.AttacksBy(PieceType.Pawn, them);
        threatByLesser[(byte)PieceType.Knight] = pawnThreat;
        threatByLesser[(byte)PieceType.Bishop] = pawnThreat;
        ulong minorThreat = _pos.AttacksBy(PieceType.Knight, them) | _pos.AttacksBy(PieceType.Bishop, them) | pawnThreat;
        threatByLesser[(byte)PieceType.Rook] = minorThreat;
        threatByLesser[(byte)PieceType.Queen] = _pos.AttacksBy(PieceType.Rook, them) | minorThreat;

        _genBuffer.Clear();
        MoveGen.Generate(GenType.Quiets, _pos, _genBuffer);

        int it = _cur;
        foreach (var m in _genBuffer)
        {
            Square from = m.FromSq;
            Square to = m.ToSq;
            Piece pc = _pos.MovedPiece(m);
            PieceType pt = Types.TypeOf(pc);

            int value = 2 * _hist.GetMainHistoryRaw(us, m);
            value += 2 * _hist.GetPawnHistoryValue(_pos, pc, to);
            value += _hist.GetContinuationHistory(_contRefs[0], pc, to);
            value += _hist.GetContinuationHistory(_contRefs[1], pc, to);
            value += _hist.GetContinuationHistory(_contRefs[2], pc, to);
            value += _hist.GetContinuationHistory(_contRefs[3], pc, to);
            value += _hist.GetContinuationHistory(_contRefs[5], pc, to);

            if ((_pos.CheckSquaresOf(pt) & Bitboards.SquareBB(to)) != 0 && _pos.SeeGe(m, -75))
                value += 16384;

            int v = 20 * (((threatByLesser[(byte)pt] & Bitboards.SquareBB(from)) != 0 ? 1 : 0)
                        - ((threatByLesser[(byte)pt] & Bitboards.SquareBB(to)) != 0 ? 1 : 0));
            value += Values.PieceValue[(byte)pt] * v;

            if (_ply < LowPlyHistorySize)
                value += 8 * _hist.GetLowPlyHistoryValue(_ply, m) / (1 + _ply);

            _moves[it] = m;
            _values[it] = value;
            it++;
        }

        return it;
    }

    /// <summary>score&lt;EVASIONS&gt;, movepick.cpp:252-258.</summary>
    private int ScoreEvasions()
    {
        Color us = _pos.SideToMove;
        _genBuffer.Clear();
        MoveGen.Generate(GenType.Evasions, _pos, _genBuffer);

        int it = _cur;
        foreach (var m in _genBuffer)
        {
            Square to = m.ToSq;
            Piece pc = _pos.MovedPiece(m);

            int value;
            if (_pos.CaptureStage(m))
                value = Values.PieceValue[(byte)_pos.PieceOn(to)] + (1 << 28);
            else
                value = _hist.GetMainHistoryRaw(us, m) + _hist.GetContinuationHistory(_contRefs[0], pc, to);

            _moves[it] = m;
            _values[it] = value;
            it++;
        }

        return it;
    }
}

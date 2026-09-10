// Porting del nucleo di src/nnue/nnue_accumulator.h+.cpp (N9, aggiornamento incrementale
// dell'accumulatore) — AccumulatorStack, Push/Pop/Evaluate/FindLastUsableAccumulator/
// ForwardUpdateIncremental. Deliberatamente SENZA due ottimizzazioni della fonte, entrambe sopra
// il nucleo corretto (non richieste per la correttezza):
// - AccumulatorCaches ("Finny Tables", nnue_accumulator.h:58-89): una cache per casa del re che
//   rende più VELOCE un refresh completo quando serve, riusando un accumulatore precedente per
//   quella casa invece di ripartire dai soli bias. Senza, un refresh resta comunque corretto (lo
//   stesso RefreshPerspective/ComputeFromScratch già verificato bit-esatto contro l'oracolo N1-N8)
//   solo più lento nei refresh stessi.
// - update_accumulator_hybrid (nnue_accumulator.cpp:722-879) e backward_update_incremental
//   (nnue_accumulator.cpp:177-193): PORTATE E MISURATE il 2026-09-10, poi TOLTE. Non e' una
//   rinuncia per pigrizia, ed e' il caso di leggere il perche' prima di ritentare.
//
//   Cosa fanno: backward_update_incremental, dopo un refresh, risale riempiendo all'indietro i
//   frame intermedi; update_accumulator_hybrid intercetta la mossa di RE (la causa piu' frequente
//   di refresh) ricostruendo la parte PSQ dalle Finny Tables delle due case coinvolte. Sono state
//   scritte fedeli e VERIFICATE bit-esatte: 152/152 test, sei dei quali attraversano davvero il
//   percorso ibrido, e un test dedicato che falliva col verso invertito.
//
//   MISURA: bench 5011 -> 5149 ms, quattro giri alternati su quattro a sfavore (~2,8% PIU' LENTI);
//   ricerche profonde singole -8,2% e +2,3%. Nessun guadagno.
//
//   PERCHE', ed e' la cosa da ricordare: nella fonte queste tecniche vivono dentro un ciclo per
//   TILE che carica un pezzo di accumulatore nei registri, ci applica sopra TUTTO (feature tolte,
//   aggiunte, l'accumulatore precedente, la voce di cache, le minacce) e lo riscrive una volta
//   sola. Qui ogni operazione e' una funzione separata che scorre l'intero accumulatore da 1024
//   int16: l'ibrido diventa cinque passate di memoria dove la fonte ne fa una. L'aritmetica in piu'
//   che queste tecniche introducono costa quindi molto di piu' da noi di quanto risparmi.
//
//   Diventano convenienti SOLO dopo aver fuso le passate in un ciclo per tile — che e' anche la
//   strada per attaccare ApplyIncrementalDelta, il ~32% del tempo di ricerca. Ritentarle prima
//   significa rimisurare lo stesso -3%.
//
// Verificato (NnueIncrementalTests.cs): per ogni mossa in perft su più posizioni, l'accumulatore
// aggiornato incrementalmente è bit-esatto (accumulation, psqtAccumulation) contro
// NnueAccumulator.ComputeFromScratch sulla stessa posizione raggiunta.

namespace StockfishSharp.Engine.Nnue;

public sealed class AccumulatorStack
{
    public const int MaxSize = Ply.MaxPly + 1;

    private readonly NnueAccumulator[] _stack;
    private int _size = 1;

    /// <summary><c>AccumulatorCaches</c> del thread (nnue_accumulator.h:56): una per AccumulatorStack,
    /// come nella fonte e' una per Worker. Persiste FRA le ricerche — e' proprio quello a renderla
    /// utile — e viene svuotata solo quando cambia la rete.</summary>
    private readonly CacheRefresh _cache = new();

    /// <summary>La rete con cui la cache e' stata riempita. Nella fonte lo svuotamento e' esplicito
    /// in <c>Worker::clear</c>; qui la cache si invalida DA SOLA quando la rete cambia, cosi' non
    /// puo' restare stantia per una dimenticanza del chiamante. Confronto per riferimento: le
    /// voci dipendono dai pesi, non dal loro contenuto logico.</summary>
    private NnueNetwork? _reteDellaCache;

    public AccumulatorStack()
    {
        _stack = new NnueAccumulator[MaxSize];
        for (int i = 0; i < MaxSize; i++) _stack[i] = new NnueAccumulator();
    }

    public NnueAccumulator Latest => _stack[_size - 1];

    /// <summary><c>AccumulatorStack::reset</c>, nnue_accumulator.cpp:71-77 — da chiamare una volta
    /// a inizio ricerca (Search.Search_), come <c>ucinewgame</c>/<c>iterative_deepening</c> fanno
    /// nella fonte tramite <c>Worker::clear</c>/l'inizializzazione dello stack.</summary>
    public void Reset()
    {
        _size = 1;
        _stack[0].Computed[0] = false;
        _stack[0].Computed[1] = false;
    }

    /// <summary><c>AccumulatorStack::push</c>, nnue_accumulator.cpp:79-87 — va chiamato PRIMA di
    /// <c>Position.DoMove</c>, passando <see cref="NnueAccumulator.DirtyThreats"/>/<see
    /// cref="NnueAccumulator.DirtyPiece"/>/<see cref="NnueAccumulator.DirtyPawnPairs"/> del frame
    /// restituito come argomenti dirty di DoMove (esattamente come search.cpp:660-661).</summary>
    public NnueAccumulator Push()
    {
        var st = _stack[_size];
        st.Computed[0] = false;
        st.Computed[1] = false;
        st.DirtyThreats.Clear();
        _size++;
        return st;
    }

    /// <summary><c>AccumulatorStack::pop</c>, nnue_accumulator.cpp:89-92 — va chiamato DOPO
    /// <c>Position.UndoMove</c> (search.cpp:681-684).</summary>
    public void Pop() => _size--;

    /// <summary><c>AccumulatorStack::evaluate</c>, nnue_accumulator.cpp:94-108 (senza il ramo
    /// "both" — vedi nota in testa al file: qui le due prospettive sono sempre trattate
    /// indipendentemente, corretto ma con margine di ottimizzazione non sfruttato quando
    /// condividono lo stesso ultimo accumulatore utilizzabile).</summary>
    public void Evaluate(Position pos, NnueNetwork net)
    {
        EvaluateSide(Color.White, pos, net);
        EvaluateSide(Color.Black, pos, net);
    }

    private void EvaluateSide(Color perspective, Position pos, NnueNetwork net)
    {
        int p = (byte)perspective;
        int lastUsable = FindLastUsableAccumulator(perspective);

        if (_stack[lastUsable].Computed[p])
            ForwardUpdateIncremental(perspective, pos, net, lastUsable);
        else
        {
            if (!ReferenceEquals(_reteDellaCache, net))
            {
                _cache.Svuota(net);
                _reteDellaCache = net;
            }

            _stack[_size - 1].RefreshPerspectiveConCache(net, pos, perspective, _cache);
            _stack[_size - 1].Computed[p] = true;
        }
    }

    /// <summary><c>AccumulatorStack::find_last_usable_accumulator</c>, nnue_accumulator.cpp:
    /// 144-158 — risale lo stack cercando un accumulatore già calcolato per questa prospettiva o,
    /// se prima ne trova uno, il punto in cui il re si è mosso (da lì un refresh è comunque
    /// obbligatorio: gli indici HalfKA/FullThreats dipendono dalla casa del re, un aggiornamento
    /// incrementale non può "attraversare" quel salto).</summary>
    private int FindLastUsableAccumulator(Color perspective)
    {
        int p = (byte)perspective;
        for (int idx = _size - 1; idx > 0; idx--)
        {
            if (_stack[idx].Computed[p]) return idx;
            if (HalfKAv2Hm.RequiresRefresh(_stack[idx].DirtyPiece.Pc, perspective)) return idx;
        }

        return 0;
    }

    /// <summary><c>AccumulatorStack::forward_update_incremental</c>, nnue_accumulator.cpp:
    /// 160-175 — da <paramref name="begin"/> (già "computed") applica in sequenza i delta di ogni
    /// frame successivo, copiando prima i valori del frame precedente (l'aggiornamento è sempre
    /// "accumula sopra al precedente", mai da zero).</summary>
    private void ForwardUpdateIncremental(Color perspective, Position pos, NnueNetwork net, int begin)
    {
        int p = (byte)perspective;
        Square ksq = pos.SquareOf(PieceType.King, perspective);

        for (int next = begin + 1; next < _size; next++)
        {
            var prev = _stack[next - 1];
            var cur = _stack[next];

            // Copia SUL POSTO negli array preallocati del frame corrente: il Clone() precedente
            // allocava 2 KB per prospettiva a OGNI aggiornamento incrementale, cioe' a ogni nodo.
            prev.Accumulation[p].AsSpan().CopyTo(cur.Accumulation[p]);
            prev.PsqtAccumulation[p].AsSpan().CopyTo(cur.PsqtAccumulation[p]);

            cur.ApplyIncrementalDelta(net, perspective, ksq);
            cur.Computed[p] = true;
        }
    }
}

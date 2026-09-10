// Corrisponde alla parte "da zero" (nessuna cache/aggiornamento incrementale, rimandato a N9 —
// vedi docs/nnue-porting-plan.md) di src/nnue/nnue_accumulator.cpp
// (update_accumulator_refresh_cache) e src/nnue/nnue_feature_transformer.h (transform). Vedi
// ../Types.cs per la nota generale sul porting.
//
// Percorso AVX512 (N8, docs/nnue-porting-plan.md): la fonte usa una tassellazione a registri
// (SIMDTiling, simd.h) che ammortizza il caricamento della riga dell'accumulatore su più feature
// prima di riscriverla — un'ottimizzazione di cache, non parte dell'algoritmo. Qui si somma
// direttamente riga per riga con Vector512<short>: risultato numerico identico (stesso ordine di
// addizione, la somma di interi a 16 bit è associativa fra le feature — l'unica differenza dalla
// fonte è quale registro tiene quale porzione della riga, irrilevante al risultato), niente
// permutazione dei pesi da fare (quella serve solo al trucco packus di transform_perspective).

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

/// <summary>Un frame dell'accumulatore per le due prospettive — fonde <c>Accumulator</c>
/// (accumulation/psqtAccumulation/computed, nnue_accumulator.h:45-49) e <c>Dirties</c>
/// (DirtyPiece/DirtyThreats/DirtyPawnPairs, types.h:352-356) in un'unica classe: la fonte li tiene
/// separati (<c>struct AccumulatorState: public Accumulator, Dirties {}</c>) solo perché C++ non
/// alloca extra per l'ereditarietà multipla di struct vuote — qui non c'è motivo di separarli. Da
/// solo (senza <see cref="AccumulatorStack"/>) resta il ricalcolo "da zero" di N1-N8; i campi
/// Computed/Dirty* servono solo quando è parte di uno stack (N9).</summary>
public sealed class NnueAccumulator
{
    // Array PREALLOCATI una volta per accumulatore: nella fonte sono membri fissi della struct
    // Accumulator (nnue_accumulator.h), riempiti sul posto. Qui venivano invece SOSTITUITI a ogni
    // refresh e clonati a ogni aggiornamento incrementale (2 KB per prospettiva, a ogni nodo).
    public readonly short[][] Accumulation = [new short[L1], new short[L1]];
    public readonly int[][] PsqtAccumulation = [new int[PsqtBuckets], new int[PsqtBuckets]];

    // N9 — vedi AccumulatorStack. Computed[c]=vero se Accumulation[c]/PsqtAccumulation[c] sono
    // validi per la posizione che questo frame rappresenta. I tre Dirty descrivono la TRANSIZIONE
    // dal frame precedente sullo stack a questo (popolati da Position.DoMove quando questo frame
    // viene passato come argomento dirtyThreats/dirtyPiece/dirtyPawnPairs).
    public readonly bool[] Computed = new bool[2];

    // Vedi ApplyIncrementalDelta: buffer riusati al posto di quattro liste allocate per chiamata.
    private readonly List<int> _removedPsq = new(64);
    private readonly List<int> _addedPsq = new(64);
    private readonly List<int> _removedThreat = new(256);
    private readonly List<int> _addedThreat = new(256);
    private readonly List<int> _refreshPsq = new(64);
    private readonly List<int> _refreshThreats = new(512);
    private readonly List<int> _refreshRimosse = new(64); // "removed" di update_accumulator_refresh_cache
    public readonly DirtyPiece DirtyPiece = new();
    public readonly List<DirtyThreat> DirtyThreats = new();
    public readonly DirtyPawnPairs DirtyPawnPairs = new();

    /// <summary>Se vero, <see cref="ComputeFromScratch"/> usa il percorso AVX512 per la somma
    /// delle righe di peso (L1=1024 elementi per feature attiva) invece del ciclo scalare.</summary>
    public static bool UsingAvx512 { get; } = Avx512BW.IsSupported && Avx512F.IsSupported;


    /// <summary>Applica in UNA SOLA passata sull'accumulatore tutte le feature tolte e aggiunte,
    /// PSQ (pesi i16) e minacce/coppie (pesi i8) insieme.
    ///
    /// E' la struttura della fonte, che qui mancava: nnue_accumulator.cpp lavora per TILE
    /// ("acc = load_tile(j); acc = apply_psq_features&lt;-1&gt;(...); acc = apply_psq_features&lt;+1&gt;(...);
    /// acc = apply_threat_features&lt;...&gt;(...); store_tile(j, acc)") — carica un pezzo di
    /// accumulatore nei registri, ci applica sopra TUTTO e lo riscrive una volta sola.
    ///
    /// Prima ogni feature era una funzione a se' che scorreva tutti i 1024 int16: con quattro
    /// feature PSQ e una ventina di minacce si facevano ~24 passate di memoria sullo stesso array
    /// da 2 KB. Il numero di righe di peso lette non cambia (quelle vanno lette comunque), cambia
    /// quante volte si rilegge e riscrive l'ACCUMULATORE: da ~24 a 1.
    ///
    /// La misura che ha reso evidente il problema: update_accumulator_hybrid e
    /// backward_update_incremental, portate fedelmente il 2026-09-10, risultavano PIU' LENTE
    /// proprio perche' aggiungevano altre passate — vedi la nota in testa ad AccumulatorStack.cs.</summary>
    private static void ApplicaFuso(short[] acc, NnueNetwork net,
                                    List<int> psqTogliere, List<int> psqSommare,
                                    List<int> thrTogliere, List<int> thrSommare)
    {
        // Span invece dell'enumeratore di List: questi cicli girano una volta per TILE, quindi
        // sedici volte, e l'enumeratore si pagherebbe sedici volte.
        var psqT = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(psqTogliere);
        var psqS = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(psqSommare);
        var thrT = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(thrTogliere);
        var thrS = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(thrSommare);

        short[] w16 = net.Weights;
        sbyte[] w8 = net.ThreatAndPpWeights;

        // Tile da 64 short = due registri: 64 e' anche il passo naturale dei pesi i8, che si
        // caricano 64 byte alla volta e si allargano in due meta'.
        for (int j = 0; j < L1; j += 64)
        {
            var lo = Vector512.LoadUnsafe(ref acc[j]);
            var hi = Vector512.LoadUnsafe(ref acc[j + 32]);

            foreach (int f in psqT)
            {
                int b = (f * L1) + j;
                lo -= Vector512.LoadUnsafe(ref w16[b]);
                hi -= Vector512.LoadUnsafe(ref w16[b + 32]);
            }

            foreach (int f in psqS)
            {
                int b = (f * L1) + j;
                lo += Vector512.LoadUnsafe(ref w16[b]);
                hi += Vector512.LoadUnsafe(ref w16[b + 32]);
            }

            foreach (int f in thrT)
            {
                var w = Vector512.LoadUnsafe(ref w8[(f * L1) + j]);
                lo -= Vector512.WidenLower(w);
                hi -= Vector512.WidenUpper(w);
            }

            foreach (int f in thrS)
            {
                var w = Vector512.LoadUnsafe(ref w8[(f * L1) + j]);
                lo += Vector512.WidenLower(w);
                hi += Vector512.WidenUpper(w);
            }

            lo.StoreUnsafe(ref acc[j]);
            hi.StoreUnsafe(ref acc[j + 32]);
        }
    }

    /// <summary><c>get_changed_pieces</c>, nnue_accumulator.cpp:617-640 — le case in cui la
    /// disposizione memorizzata in cache differisce da quella attuale, come bitboard.
    ///
    /// La fonte fa due confronti da 32 byte e ne ricava la maschera con <c>movemask</c>: qui e'
    /// identico, con Vector256 ed ExtractMostSignificantBits. Prima si scorrevano tutte e 64 le
    /// case a ogni refresh, ed era il motivo per cui nei finali spogli la cache faceva perdere
    /// tempo invece di guadagnarne (misurato: -2,2% a profondita' 26).</summary>
    public static ulong PezziCambiati(ReadOnlySpan<Piece> memorizzati, ReadOnlySpan<Piece> attuali)
    {
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<Piece, byte>(memorizzati);
        var b = System.Runtime.InteropServices.MemoryMarshal.Cast<Piece, byte>(attuali);

        if (Vector256.IsHardwareAccelerated)
        {
            ulong uguali = 0;
            for (int i = 0; i < Squares.Nb; i += 32)
            {
                var va = Vector256.Create(a.Slice(i, 32));
                var vb = Vector256.Create(b.Slice(i, 32));
                uguali |= (ulong)Vector256.Equals(va, vb).ExtractMostSignificantBits() << i;
            }
            return ~uguali;
        }

        ulong cambiati = 0;
        for (int i = 0; i < Squares.Nb; i++)
            if (a[i] != b[i]) cambiati |= 1UL << i;
        return cambiati;
    }

    /// <summary><c>update_accumulator_refresh_cache</c> applicata a una cache vuota (nessun pezzo
    /// "rimosso", tutti i pezzi presenti sono "aggiunti") — nnue_accumulator.cpp:880-949. Somma
    /// bias + riga di peso di ogni feature attiva dei tre insiemi (PSQ, minacce, coppie di
    /// pedoni), sia per i pesi (i16/i8, per prospettiva) sia per i PSQT (i32, per bucket).</summary>
    public static NnueAccumulator ComputeFromScratch(NnueNetwork net, Position pos)
    {
        var result = new NnueAccumulator();
        result.RefreshPerspective(net, pos, Color.White);
        result.RefreshPerspective(net, pos, Color.Black);
        return result;
    }

    /// <summary>Come <see cref="ComputeFromScratch"/> ma per UNA sola prospettiva, scrivendo in
    /// questo oggetto invece di crearne uno nuovo — usata da <see cref="AccumulatorStack"/> (N9)
    /// per il refresh completo di un singolo frame quando non c'è nessun accumulatore precedente
    /// riusabile (il re di quella prospettiva si è mosso, o è il primo utilizzo).</summary>
    public void RefreshPerspective(NnueNetwork net, Position pos, Color perspective)
    {
        int p = (byte)perspective;

        // Riempimento SUL POSTO degli array gia' esistenti (vedi la nota sui campi sopra): prima
        // qui si clonava net.Biases (1024 short = 2 KB) e si allocavano un array PSQT e due liste
        // a ogni singolo refresh.
        short[] acc = Accumulation[p];
        int[] psqt = PsqtAccumulation[p];
        net.Biases.AsSpan(0, L1).CopyTo(acc);
        Array.Clear(psqt);

        var psq = _refreshPsq; psq.Clear();
        HalfKAv2Hm.AppendActiveIndices(perspective, pos, psq);
        foreach (int f in psq)
        {
            AddWeightRowI16(acc, net.Weights, f * L1);

            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.PsqtWeights[pBase + b];
        }

        var threats = _refreshThreats; threats.Clear();
        FullThreats.AppendActiveIndices(perspective, pos, threats);
        Pp3Wide.AppendActiveIndices(perspective, pos, threats);
        foreach (int f in threats)
        {
            AddWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);

            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.ThreatAndPpPsqtWeights[pBase + b];
        }

        // (niente riassegnazione: acc/psqt SONO gia' Accumulation[p]/PsqtAccumulation[p])
    }

    /// <summary><c>update_accumulator_refresh_cache</c>, nnue_accumulator.cpp:880-948 — il refresh
    /// che passa dalle "Finny Tables" (<see cref="CacheRefresh"/>) invece di ripartire dai bias.
    ///
    /// Fa esattamente cio' che fa la fonte, nello stesso ordine: calcola la differenza fra i pezzi
    /// memorizzati nella voce e quelli sulla scacchiera, applica quella differenza ALLA VOCE (che
    /// resta cosi' aggiornata per il prossimo refresh su questa casa del re), e solo dopo copia la
    /// voce nell'accumulatore sommandoci sopra minacce e coppie — che non sono in cache perche'
    /// non dipendono solo dalla disposizione dei pezzi.
    ///
    /// Il guadagno sta tutto nella prima parte: un refresh da zero somma ~32 righe da 1024 int16,
    /// questo ne tocca quante sono le case cambiate rispetto all'ultima volta che si e' passati di
    /// qui con il re su quella casa — in partita quasi sempre pochissime.</summary>
    public void RefreshPerspectiveConCache(NnueNetwork net, Position pos, Color perspective, CacheRefresh cache)
    {
        int p = (byte)perspective;
        Square ksq = pos.SquareOf(PieceType.King, perspective);
        var voce = cache[ksq, perspective];

        // "get_changed_pieces" + la separazione in removedBB/addedBB (nnue_accumulator.cpp:890-908).
        // La fonte usa i bitboard per iterare solo sulle case cambiate; qui si scorrono le 64 case
        // confrontando pezzo memorizzato e pezzo attuale, che e' la stessa cosa con un confronto in
        // piu' per casa — trascurabile rispetto alle righe di peso che si risparmiano.
        var rimosse = _refreshRimosse; rimosse.Clear();
        var aggiunte = _refreshPsq; aggiunte.Clear();

        ulong occupazione = pos.Pieces();
        ulong cambiate = PezziCambiati(voce.Pezzi, pos.ArrayPezzi);
        ulong rimosseBB = cambiate & voce.PezziBB;   // c'era un pezzo li' nella disposizione in cache
        ulong aggiunteBB = cambiate & occupazione;   // ce n'e' uno adesso

        while (rimosseBB != 0)
        {
            Square sq = Bitboards.PopLsb(ref rimosseBB);
            rimosse.Add(HalfKAv2Hm.MakeIndex(perspective, sq, voce.Pezzi[(byte)sq], ksq));
        }

        while (aggiunteBB != 0)
        {
            Square sq = Bitboards.PopLsb(ref aggiunteBB);
            aggiunte.Add(HalfKAv2Hm.MakeIndex(perspective, sq, pos.PieceOn(sq), ksq));
        }

        // Solo DOPO aver letto i pezzi vecchi: "entry.pieces = pos.piece_array()".
        pos.ArrayPezzi.CopyTo(voce.Pezzi);
        voce.PezziBB = occupazione; // "entry.pieceBB = pos.pieces()"

        // La voce si aggiorna PRIMA di essere copiata: e' il "store_tile(j, &entry.accumulation[0])"
        // della fonte, che avviene prima di sommarci sopra le minacce.
        short[] accCache = voce.Accumulation;
        int[] psqtCache = voce.PsqtAccumulation;

        foreach (int f in rimosse)
        {
            SubtractWeightRowI16(accCache, net.Weights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqtCache[b] -= net.PsqtWeights[pBase + b];
        }

        foreach (int f in aggiunte)
        {
            AddWeightRowI16(accCache, net.Weights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqtCache[b] += net.PsqtWeights[pBase + b];
        }

        short[] acc = Accumulation[p];
        int[] psqt = PsqtAccumulation[p];
        accCache.AsSpan(0, L1).CopyTo(acc);
        psqtCache.AsSpan(0, PsqtBuckets).CopyTo(psqt);

        // Minacce e coppie: ricalcolate e sommate sopra, MAI messe in cache (la fonte fa lo stesso,
        // nnue_accumulator.cpp:913-916 e 930-934).
        var minacce = _refreshThreats; minacce.Clear();
        FullThreats.AppendActiveIndices(perspective, pos, minacce);
        Pp3Wide.AppendActiveIndices(perspective, pos, minacce);
        foreach (int f in minacce)
        {
            AddWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.ThreatAndPpPsqtWeights[pBase + b];
        }
    }

    /// <summary>N9 — applica a QUESTA prospettiva (già inizializzata a copia del frame precedente
    /// sullo stack, vedi <see cref="AccumulatorStack"/>) i delta descritti dai tre Dirty di questo
    /// frame: rimuove le feature "removed", aggiunge le "added", sia per i pesi (accumulation) sia
    /// per i PSQT. Nessuna controparte diretta nella fonte (lì è inline dentro
    /// <c>update_accumulator_incremental</c>, nnue_accumulator.cpp:533-579) — qui separata per
    /// riusare gli <c>AppendChangedIndices</c> già scritti per ciascuna feature.</summary>
    public void ApplyIncrementalDelta(NnueNetwork net, Color perspective, Square ksq)
    {
        int p = (byte)perspective;
        short[] acc = Accumulation[p];
        int[] psqt = PsqtAccumulation[p];

        // Buffer FISSI riusati, non quattro liste nuove a ogni chiamata: nella fonte sono array a
        // capacita' fissa dichiarati sullo stack ("IndexList removed[2], added[2]",
        // nnue_accumulator.cpp), qui erano l'allocazione piu' pesante rimasta del percorso caldo —
        // ApplyIncrementalDelta gira per OGNI prospettiva a OGNI nodo che valuta, e le liste delle
        // minacce possono contenere decine di indici, quindi anche i loro array interni venivano
        // riallocati piu' volte mentre crescevano. Sono campi di istanza: ogni accumulatore
        // appartiene a un solo frame di un solo AccumulatorStack, e quindi a un solo thread.
        var removedPsq = _removedPsq; removedPsq.Clear();
        var addedPsq = _addedPsq; addedPsq.Clear();
        HalfKAv2Hm.AppendChangedIndices(perspective, ksq, DirtyPiece, removedPsq, addedPsq);

        var removedThreat = _removedThreat; removedThreat.Clear();
        var addedThreat = _addedThreat; addedThreat.Clear();
        FullThreats.AppendChangedIndices(perspective, ksq, DirtyThreats, removedThreat, addedThreat, net.ThreatAndPpWeights);
        Pp3Wide.AppendChangedIndices(perspective, ksq, DirtyPawnPairs, removedThreat, addedThreat);

        // L'accumulatore: UNA passata sola con tutto dentro (vedi ApplicaFuso). Il ripiego
        // scalare resta per l'hardware senza AVX-512.
        if (UsingAvx512)
            ApplicaFuso(acc, net, removedPsq, addedPsq, removedThreat, addedThreat);
        else
        {
            foreach (int f in removedPsq) SubtractWeightRowI16(acc, net.Weights, f * L1);
            foreach (int f in addedPsq) AddWeightRowI16(acc, net.Weights, f * L1);
            foreach (int f in removedThreat) SubtractWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);
            foreach (int f in addedThreat) AddWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);
        }

        // I PSQT sono otto interi in un array diverso: restano cicli a se', il loro costo e'
        // trascurabile e fonderli non guadagnerebbe nulla.
        foreach (int f in removedPsq)
        {
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] -= net.PsqtWeights[pBase + b];
        }

        foreach (int f in addedPsq)
        {
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.PsqtWeights[pBase + b];
        }

        foreach (int f in removedThreat)
        {
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] -= net.ThreatAndPpPsqtWeights[pBase + b];
        }

        foreach (int f in addedThreat)
        {
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.ThreatAndPpPsqtWeights[pBase + b];
        }
    }

    /// <summary>acc[0..L1) += weights[wBase..wBase+L1) — pesi PSQ (i16), apply&lt;+1&gt;
    /// scalare/vettoriale in nnue_accumulator.cpp:265-273/433-441.</summary>
    private static void AddWeightRowI16(short[] acc, short[] weights, int wBase)
    {
        if (UsingAvx512) AddWeightRowI16Avx512(acc, weights, wBase);
        else AddWeightRowI16Scalar(acc, weights, wBase);
    }

    public static void AddWeightRowI16Scalar(short[] acc, short[] weights, int wBase)
    {
        for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] + weights[wBase + j]);
    }

    public static void AddWeightRowI16Avx512(short[] acc, short[] weights, int wBase)
    {
        for (int j = 0; j < L1; j += 32)
        {
            var a = Vector512.LoadUnsafe(ref acc[j]);
            var w = Vector512.LoadUnsafe(ref weights[wBase + j]);
            (a + w).StoreUnsafe(ref acc[j]);
        }
    }

    /// <summary>acc[0..L1) += sign_extend_16(weights[wBase..wBase+L1)) — pesi minacce/coppie di
    /// pedoni (i8), apply_threat_features&lt;+1&gt; scalare/vettoriale in
    /// nnue_accumulator.cpp:379-430 (ramo <c>vec_convert_8_16</c> generico, non le varianti
    /// speciali NEON/LSX).</summary>
    private static void AddWeightRowI8(short[] acc, sbyte[] weights, int wBase)
    {
        if (UsingAvx512) AddWeightRowI8Avx512(acc, weights, wBase);
        else AddWeightRowI8Scalar(acc, weights, wBase);
    }

    public static void AddWeightRowI8Scalar(short[] acc, sbyte[] weights, int wBase)
    {
        for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] + weights[wBase + j]);
    }

    public static void AddWeightRowI8Avx512(short[] acc, sbyte[] weights, int wBase)
    {
        for (int j = 0; j < L1; j += 64)
        {
            var w = Vector512.LoadUnsafe(ref weights[wBase + j]);
            var wLo = Vector512.WidenLower(w);
            var wHi = Vector512.WidenUpper(w);
            var aLo = Vector512.LoadUnsafe(ref acc[j]);
            var aHi = Vector512.LoadUnsafe(ref acc[j + 32]);
            (aLo + wLo).StoreUnsafe(ref acc[j]);
            (aHi + wHi).StoreUnsafe(ref acc[j + 32]);
        }
    }

    /// <summary>acc[0..L1) -= weights[wBase..wBase+L1) — simmetrico di <see
    /// cref="AddWeightRowI16"/>, apply&lt;-1&gt; nella fonte (stessa funzione template, N9 la usa
    /// per rimuovere una feature non più attiva invece di aggiungerne una nuova).</summary>
    private static void SubtractWeightRowI16(short[] acc, short[] weights, int wBase)
    {
        if (UsingAvx512) SubtractWeightRowI16Avx512(acc, weights, wBase);
        else SubtractWeightRowI16Scalar(acc, weights, wBase);
    }

    public static void SubtractWeightRowI16Scalar(short[] acc, short[] weights, int wBase)
    {
        for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] - weights[wBase + j]);
    }

    public static void SubtractWeightRowI16Avx512(short[] acc, short[] weights, int wBase)
    {
        for (int j = 0; j < L1; j += 32)
        {
            var a = Vector512.LoadUnsafe(ref acc[j]);
            var w = Vector512.LoadUnsafe(ref weights[wBase + j]);
            (a - w).StoreUnsafe(ref acc[j]);
        }
    }

    /// <summary>acc[0..L1) -= sign_extend_16(weights[wBase..wBase+L1)) — simmetrico di <see
    /// cref="AddWeightRowI8"/>, apply_threat_features&lt;-1&gt; nella fonte.</summary>
    private static void SubtractWeightRowI8(short[] acc, sbyte[] weights, int wBase)
    {
        if (UsingAvx512) SubtractWeightRowI8Avx512(acc, weights, wBase);
        else SubtractWeightRowI8Scalar(acc, weights, wBase);
    }

    public static void SubtractWeightRowI8Scalar(short[] acc, sbyte[] weights, int wBase)
    {
        for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] - weights[wBase + j]);
    }

    public static void SubtractWeightRowI8Avx512(short[] acc, sbyte[] weights, int wBase)
    {
        for (int j = 0; j < L1; j += 64)
        {
            var w = Vector512.LoadUnsafe(ref weights[wBase + j]);
            var wLo = Vector512.WidenLower(w);
            var wHi = Vector512.WidenUpper(w);
            var aLo = Vector512.LoadUnsafe(ref acc[j]);
            var aHi = Vector512.LoadUnsafe(ref acc[j + 32]);
            (aLo - wLo).StoreUnsafe(ref acc[j]);
            (aHi - wHi).StoreUnsafe(ref acc[j + 32]);
        }
    }

    /// <summary><c>FeatureTransformer::transform</c>, nnue_feature_transformer.h:223-244 — solo la
    /// parte PSQT (colonna "Material" dell'oracolo); la parte "positional" (forward pass dei
    /// layer) è N5. Divisione intera per 2 e poi <see cref="NnueCommon.OutputScale"/>, esattamente
    /// come la fonte (troncamento verso zero, uguale in C++ e C#).</summary>
    public int MaterialPsqt(Color sideToMove, int bucket)
    {
        var stm = (byte)sideToMove;
        var other = (byte)Types.Opposite(sideToMove);
        int raw = (PsqtAccumulation[stm][bucket] - PsqtAccumulation[other][bucket]) / 2;
        return raw / NnueCommon.OutputScale;
    }
}

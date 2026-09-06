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
    public readonly short[][] Accumulation = new short[2][];
    public readonly int[][] PsqtAccumulation = new int[2][];

    // N9 — vedi AccumulatorStack. Computed[c]=vero se Accumulation[c]/PsqtAccumulation[c] sono
    // validi per la posizione che questo frame rappresenta. I tre Dirty descrivono la TRANSIZIONE
    // dal frame precedente sullo stack a questo (popolati da Position.DoMove quando questo frame
    // viene passato come argomento dirtyThreats/dirtyPiece/dirtyPawnPairs).
    public readonly bool[] Computed = new bool[2];
    public readonly DirtyPiece DirtyPiece = new();
    public readonly List<DirtyThreat> DirtyThreats = new();
    public readonly DirtyPawnPairs DirtyPawnPairs = new();

    /// <summary>Se vero, <see cref="ComputeFromScratch"/> usa il percorso AVX512 per la somma
    /// delle righe di peso (L1=1024 elementi per feature attiva) invece del ciclo scalare.</summary>
    public static bool UsingAvx512 { get; } = Avx512BW.IsSupported && Avx512F.IsSupported;

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
        var acc = (short[])net.Biases.Clone();
        var psqt = new int[PsqtBuckets];

        var psq = new List<int>();
        HalfKAv2Hm.AppendActiveIndices(perspective, pos, psq);
        foreach (int f in psq)
        {
            AddWeightRowI16(acc, net.Weights, f * L1);

            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.PsqtWeights[pBase + b];
        }

        var threats = new List<int>();
        FullThreats.AppendActiveIndices(perspective, pos, threats);
        Pp3Wide.AppendActiveIndices(perspective, pos, threats);
        foreach (int f in threats)
        {
            AddWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);

            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.ThreatAndPpPsqtWeights[pBase + b];
        }

        Accumulation[p] = acc;
        PsqtAccumulation[p] = psqt;
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

        var removedPsq = new List<int>();
        var addedPsq = new List<int>();
        HalfKAv2Hm.AppendChangedIndices(perspective, ksq, DirtyPiece, removedPsq, addedPsq);

        var removedThreat = new List<int>();
        var addedThreat = new List<int>();
        FullThreats.AppendChangedIndices(perspective, ksq, DirtyThreats, removedThreat, addedThreat);
        Pp3Wide.AppendChangedIndices(perspective, ksq, DirtyPawnPairs, removedThreat, addedThreat);

        foreach (int f in removedPsq)
        {
            SubtractWeightRowI16(acc, net.Weights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] -= net.PsqtWeights[pBase + b];
        }

        foreach (int f in addedPsq)
        {
            AddWeightRowI16(acc, net.Weights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.PsqtWeights[pBase + b];
        }

        foreach (int f in removedThreat)
        {
            SubtractWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);
            int pBase = f * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++) psqt[b] -= net.ThreatAndPpPsqtWeights[pBase + b];
        }

        foreach (int f in addedThreat)
        {
            AddWeightRowI8(acc, net.ThreatAndPpWeights, f * L1);
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

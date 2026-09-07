// Corrisponde ai rami scalari di src/nnue/nnue_feature_transformer.h (transform_perspective) e
// src/nnue/layers/{affine_transform,affine_transform_sparse_input,clipped_relu,sqr_clipped_relu}.h
// + il propagate() di src/nnue/nnue_architecture.h. Vedi ../Types.cs per la nota generale sul
// porting e docs/nnue-porting-plan.md per l'ordine di lettura.
//
// fc_0 usa AffineTransformSparseInput, fc_1/fc_2 usano AffineTransform: nel ramo scalare (nessuna
// macro USE_* definita) entrambe le classi della fonte finiscono nella STESSA funzione
// affine_transform_non_ssse3 — qui un solo metodo AffineTransform basta per tutt'e tre i layer.
//
// Percorso AVX512 (N8): qui la scelta è NON replicare il bit-trick della fonte per
// transform_perspective (packus implicito + permutazione dei pesi al caricamento,
// PackusEpi16Order — vedi nnue_feature_transformer.h) — troppo rischioso da verificare a mano.
// Stessa matematica (clamp 0..255, prodotto, /512), ma il "pack" a byte usa la Vector512.Narrow
// PORTABILE di .NET, che concatena le corsie in ordine naturale (basso poi alto) invece
// dell'ordine intrecciato dell'istruzione hardware _mm512_packus_epi16 — risultato numerico
// identico, zero permutazione di pesi da gestire. Stesso spirito per AffineTransform: niente
// trucco maddubs+madd (che richiederebbe temere la saturazione a i16 descritta in
// docs/porting-master-plan.md), si allarga tutto a int32 con Vector512.Widen prima di
// moltiplicare — più lento della fonte ma matematicamente ovvio da verificare bit-esatto.
// ClippedReLU/SqrClippedReLU restano scalari: operano su soli 32 elementi (L2/L3), il costo è
// trascurabile rispetto alle due riduzioni larghe sopra — non vale il rischio di codice SIMD in
// più per un guadagno di prestazioni ininfluente.
//
// BUFFER A COSTO ZERO (2026-09-07): ogni metodo esiste in DUE forme. Quella che scrive in uno
// `Span<T>` fornito dal chiamante è il percorso VERO, usato in produzione: replica i buffer sullo
// stack della fonte (`alignas(64) std::uint8_t output[...]`, affine_transform.h/clipped_relu.h)
// senza allocare nulla. Quella che RESTITUISCE un array è un guscio sottile che alloca una volta e
// delega — resta solo per i test di verifica bit-esatta (NnueLayersTests), che confrontano scalare
// contro AVX512 su dati casuali e non sono sul percorso caldo. Prima di questo cambio ogni
// valutazione allocava ~3 KB in ~12 array temporanei (misurato: 5.771 byte/nodo complessivi
// sull'intera ricerca, 163 GC gen0 al secondo) — vedi docs/porting-master-plan.md.
// Tutti i caricamenti SIMD sono NON allineati (`LoadUnsafe`), quindi lo stack non allineato a 64
// byte di `stackalloc` è sicuro: nessuna istruzione qui richiede l'allineamento.

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

public static class NnueLayers
{
    public static bool UsingAvx512 { get; } = Avx512BW.IsSupported && Avx512F.IsSupported;

    /// <summary><c>FeatureTransformer::transform_perspective</c>, ramo scalare,
    /// nnue_feature_transformer.h:401-413. Dimezza le 1024 componenti dell'accumulatore di UNA
    /// prospettiva in 512 byte: <c>clamp(acc[j],0,255) * clamp(acc[j+512],0,255) / 512</c>.</summary>
    public static void TransformPerspective(short[] accumulation, Span<byte> output)
    {
        if (UsingAvx512) TransformPerspectiveAvx512(accumulation, output);
        else TransformPerspectiveScalar(accumulation, output);
    }

    public static void TransformPerspectiveScalar(short[] accumulation, Span<byte> output)
    {
        for (int j = 0; j < L1 / 2; j++)
        {
            int sum0 = Math.Clamp((int)accumulation[j], 0, NnueCommon.FtMaxVal);
            int sum1 = Math.Clamp((int)accumulation[j + (L1 / 2)], 0, NnueCommon.FtMaxVal);
            output[j] = (byte)((sum0 * sum1) / 512);
        }
    }

    /// <summary>Stessa formula di <see cref="TransformPerspectiveScalar"/>, vettorizzata a 64
    /// elementi per iterazione (due <c>Vector512&lt;short&gt;</c> da 32 corsie, ricomposti con
    /// <c>Vector512.Narrow</c> in un unico <c>Vector512&lt;byte&gt;</c> in ordine naturale — vedi
    /// il commento in testa al file sul perché non si replica il packus della fonte).</summary>
    public static void TransformPerspectiveAvx512(short[] accumulation, Span<byte> output)
    {
        var zero = Vector512<short>.Zero;
        var max = Vector512.Create((short)NnueCommon.FtMaxVal);
        ref byte outRef = ref MemoryMarshal.GetReference(output);

        for (int j = 0; j < L1 / 2; j += 64)
        {
            var a0Lo = Vector512.LoadUnsafe(ref accumulation[j]);
            var a0Hi = Vector512.LoadUnsafe(ref accumulation[j + 32]);
            var a1Lo = Vector512.LoadUnsafe(ref accumulation[j + (L1 / 2)]);
            var a1Hi = Vector512.LoadUnsafe(ref accumulation[j + (L1 / 2) + 32]);

            var c0Lo = Vector512.Min(Vector512.Max(a0Lo, zero), max).AsUInt16();
            var c0Hi = Vector512.Min(Vector512.Max(a0Hi, zero), max).AsUInt16();
            var c1Lo = Vector512.Min(Vector512.Max(a1Lo, zero), max).AsUInt16();
            var c1Hi = Vector512.Min(Vector512.Max(a1Hi, zero), max).AsUInt16();

            var productLo = (c0Lo * c1Lo) >>> 9;
            var productHi = (c0Hi * c1Hi) >>> 9;

            Vector512.Narrow(productLo, productHi).StoreUnsafe(ref outRef, (nuint)j);
        }
    }

    /// <summary><c>FeatureTransformer::transform</c> (solo la parte che produce
    /// <c>transformedFeatures</c>, non il PSQT — quello è <see cref="NnueAccumulator.MaterialPsqt"/>),
    /// nnue_feature_transformer.h:223-244. Prospettiva propria prima (byte 0-511), avversaria dopo
    /// (byte 512-1023) — <c>perspectives[2] = {stm, ~stm}</c>. Le due prospettive vengono scritte
    /// DIRETTAMENTE nelle due metà del buffer del chiamante: niente array intermedi e nessuna delle
    /// due Array.Copy che servivano prima.</summary>
    public static void TransformBothPerspectives(NnueAccumulator acc, Color sideToMove, Span<byte> result)
    {
        TransformPerspective(acc.Accumulation[(byte)sideToMove], result[..(L1 / 2)]);
        TransformPerspective(acc.Accumulation[(byte)Types.Opposite(sideToMove)], result[(L1 / 2)..L1]);
    }

    /// <summary><c>affine_transform_non_ssse3</c> (ramo scalare, non-SIMD, condiviso da
    /// affine_transform.h e affine_transform_sparse_input.h), affine_transform.h:108-120.
    /// Layout dei pesi riga-per-output: <c>weights[outIdx*inputDim + inIdx]</c> — confermato
    /// dall'ordine di lettura di <c>read_parameters</c> (identità quando nessuna macro USE_* è
    /// definita, come nel nostro caso).</summary>
    public static void AffineTransform(ReadOnlySpan<byte> input, sbyte[] weights, int[] biases, int inputDim, int outputDim, Span<int> output)
    {
        if (UsingAvx512) AffineTransformAvx512(input, weights, biases, inputDim, outputDim, output);
        else AffineTransformScalar(input, weights, biases, inputDim, outputDim, output);
    }

    public static void AffineTransformScalar(ReadOnlySpan<byte> input, sbyte[] weights, int[] biases, int inputDim, int outputDim, Span<int> output)
    {
        biases.AsSpan(0, outputDim).CopyTo(output);
        for (int i = 0; i < inputDim; i++)
        {
            int inVal = input[i];
            if (inVal == 0) continue;
            for (int j = 0; j < outputDim; j++)
                output[j] += weights[i + (j * inputDim)] * inVal;
        }
    }

    /// <summary>Stesso prodotto scalare di <see cref="AffineTransformScalar"/> (riga di pesi per
    /// output, <c>weights[j*inputDim + i]</c>), a righe complete di 64 elementi per volta
    /// (<c>inputDim</c> è sempre multiplo di 64 nei tre layer: 1024/64/128 — nessun resto da
    /// gestire). Non replica il trucco <c>maddubs_epi16</c>+<c>madd_epi16</c> della fonte (i16
    /// intermedio, sicuro qui ma delicato da verificare a mano): allarga input (byte, non
    /// negativo) e pesi (sbyte) a int32 con due <c>Widen</c> in cascata prima di moltiplicare —
    /// stesso risultato aritmetico, un solo livello di rischio invece di due.</summary>
    public static void AffineTransformAvx512(ReadOnlySpan<byte> input, sbyte[] weights, int[] biases, int inputDim, int outputDim, Span<int> output)
    {
        biases.AsSpan(0, outputDim).CopyTo(output);
        ref byte inRef = ref MemoryMarshal.GetReference(input);

        for (int j = 0; j < outputDim; j++)
        {
            int wBase = j * inputDim;
            var acc = Vector512<int>.Zero;
            for (int i = 0; i < inputDim; i += 64)
            {
                var inVec = Vector512.LoadUnsafe(ref inRef, (nuint)i);
                var wVec = Vector512.LoadUnsafe(ref weights[wBase + i]);

                var inLo16 = Vector512.WidenLower(inVec);
                var inHi16 = Vector512.WidenUpper(inVec);
                var wLo16 = Vector512.WidenLower(wVec);
                var wHi16 = Vector512.WidenUpper(wVec);

                acc += Vector512.WidenLower(inLo16).AsInt32() * Vector512.WidenLower(wLo16);
                acc += Vector512.WidenUpper(inLo16).AsInt32() * Vector512.WidenUpper(wLo16);
                acc += Vector512.WidenLower(inHi16).AsInt32() * Vector512.WidenLower(wHi16);
                acc += Vector512.WidenUpper(inHi16).AsInt32() * Vector512.WidenUpper(wHi16);
            }
            output[j] += Vector512.Sum(acc);
        }
    }

    /// <summary><c>ClippedReLU::propagate</c>, ramo scalare, clipped_relu.h:167-171.</summary>
    public static void ClippedRelu(ReadOnlySpan<int> input, int weightScaleBitsLocal, Span<byte> output)
    {
        for (int i = 0; i < input.Length; i++)
            output[i] = (byte)Math.Clamp(input[i] >> weightScaleBitsLocal, 0, 127);
    }

    /// <summary><c>SqrClippedReLU::propagate</c>, ramo scalare, sqr_clipped_relu.h:233-240.</summary>
    public static void SqrClippedRelu(ReadOnlySpan<int> input, int weightScaleBitsLocal, Span<byte> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            long squared = (long)input[i] * input[i];
            output[i] = (byte)Math.Min(127L, squared >> ((2 * weightScaleBitsLocal) + 7));
        }
    }

    /// <summary><c>NetworkArchitecture::propagate</c>, nnue_architecture.h:102-148 — restituisce il
    /// valore "positional" grezzo, va ancora diviso per <see cref="NnueCommon.OutputScale"/> come
    /// fa network.cpp (colonna "Positional (Layers)" dell'oracolo). La "skip connection"
    /// (<c>fc_0_out[30] - fc_0_out[31]</c>) usa l'uscita GREZZA (i32) di fc_0, non quella
    /// attivata. Tutti i buffer intermedi sono <c>stackalloc</c> (~600 byte in totale), come gli
    /// array sullo stack della fonte: zero allocazioni sull'heap.</summary>
    public static int Propagate(NnueLayerStack stack, ReadOnlySpan<byte> transformedFeatures)
    {
        Span<int> fc0Out = stackalloc int[L2];
        Span<byte> sqr0 = stackalloc byte[L2];
        Span<byte> clip0 = stackalloc byte[L2];
        Span<byte> concat1 = stackalloc byte[L2 * 2];
        Span<int> fc1Out = stackalloc int[L3];
        Span<byte> sqr1 = stackalloc byte[L3];
        Span<byte> clip1 = stackalloc byte[L3];
        Span<byte> concat2 = stackalloc byte[(L2 * 2) + (L3 * 2)];
        Span<int> fc2Out = stackalloc int[1];

        AffineTransform(transformedFeatures, stack.Fc0Weights, stack.Fc0Biases, L1, L2, fc0Out);
        SqrClippedRelu(fc0Out, NnueCommon.WeightScaleBits + 1, sqr0);
        ClippedRelu(fc0Out, NnueCommon.WeightScaleBits + 1, clip0);

        sqr0.CopyTo(concat1[..L2]);
        clip0.CopyTo(concat1[L2..]);

        AffineTransform(concat1, stack.Fc1Weights, stack.Fc1Biases, L2 * 2, L3, fc1Out);
        SqrClippedRelu(fc1Out, NnueCommon.WeightScaleBits, sqr1);
        ClippedRelu(fc1Out, NnueCommon.WeightScaleBits, clip1);

        concat1.CopyTo(concat2[..(L2 * 2)]);
        sqr1.CopyTo(concat2.Slice(L2 * 2, L3));
        clip1.CopyTo(concat2.Slice((L2 * 2) + L3, L3));

        AffineTransform(concat2, stack.Fc2Weights, stack.Fc2Biases, (L2 * 2) + (L3 * 2), 1, fc2Out);

        int skip0 = fc0Out[L2 - 2] - fc0Out[L2 - 1];
        long fwdOut = fc2Out[0] + skip0;

        const long multiplier = 600L * NnueCommon.OutputScale;
        long denominator = (long)NnueCommon.HiddenOneVal * (1L << NnueCommon.WeightScaleBits) * 2;

        return (int)(fwdOut * multiplier / denominator);
    }

    // --- Gusci che allocano, SOLO per i test di verifica bit-esatta (NnueLayersTests) ---------
    // Non usati in produzione: il percorso caldo passa dai metodi a Span qui sopra. Restano perché
    // i test confrontano scalare contro AVX512 su dati casuali e leggere il risultato come array è
    // molto più comodo lì, dove l'allocazione non ha alcun peso.

    public static byte[] TransformPerspectiveScalar(short[] accumulation)
    {
        var output = new byte[L1 / 2];
        TransformPerspectiveScalar(accumulation, output);
        return output;
    }

    public static byte[] TransformPerspectiveAvx512(short[] accumulation)
    {
        var output = new byte[L1 / 2];
        TransformPerspectiveAvx512(accumulation, output);
        return output;
    }

    public static byte[] TransformBothPerspectives(NnueAccumulator acc, Color sideToMove)
    {
        var result = new byte[L1];
        TransformBothPerspectives(acc, sideToMove, result);
        return result;
    }

    public static int[] AffineTransformScalar(byte[] input, sbyte[] weights, int[] biases, int inputDim, int outputDim)
    {
        var output = new int[outputDim];
        AffineTransformScalar(input, weights, biases, inputDim, outputDim, output);
        return output;
    }

    public static int[] AffineTransformAvx512(byte[] input, sbyte[] weights, int[] biases, int inputDim, int outputDim)
    {
        var output = new int[outputDim];
        AffineTransformAvx512(input, weights, biases, inputDim, outputDim, output);
        return output;
    }
}

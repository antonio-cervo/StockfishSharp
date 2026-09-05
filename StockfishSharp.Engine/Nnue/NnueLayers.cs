// Corrisponde ai rami scalari di src/nnue/nnue_feature_transformer.h (transform_perspective) e
// src/nnue/layers/{affine_transform,affine_transform_sparse_input,clipped_relu,sqr_clipped_relu}.h
// + il propagate() di src/nnue/nnue_architecture.h. Vedi ../Types.cs per la nota generale sul
// porting e docs/nnue-porting-plan.md per l'ordine di lettura.
//
// fc_0 usa AffineTransformSparseInput, fc_1/fc_2 usano AffineTransform: nel ramo scalare (nessuna
// macro USE_* definita) entrambe le classi della fonte finiscono nella STESSA funzione
// affine_transform_non_ssse3 — qui un solo metodo AffineTransform basta per tutt'e tre i layer.

using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

public static class NnueLayers
{
    /// <summary><c>FeatureTransformer::transform_perspective</c>, ramo scalare,
    /// nnue_feature_transformer.h:401-413. Dimezza le 1024 componenti dell'accumulatore di UNA
    /// prospettiva in 512 byte: <c>clamp(acc[j],0,255) * clamp(acc[j+512],0,255) / 512</c>.</summary>
    public static byte[] TransformPerspective(short[] accumulation)
    {
        var output = new byte[L1 / 2];
        for (int j = 0; j < L1 / 2; j++)
        {
            int sum0 = Math.Clamp((int)accumulation[j], 0, NnueCommon.FtMaxVal);
            int sum1 = Math.Clamp((int)accumulation[j + (L1 / 2)], 0, NnueCommon.FtMaxVal);
            output[j] = (byte)((sum0 * sum1) / 512);
        }
        return output;
    }

    /// <summary><c>FeatureTransformer::transform</c> (solo la parte che produce
    /// <c>transformedFeatures</c>, non il PSQT — quello è <see cref="NnueAccumulator.MaterialPsqt"/>),
    /// nnue_feature_transformer.h:223-244. Prospettiva propria prima (byte 0-511), avversaria dopo
    /// (byte 512-1023) — <c>perspectives[2] = {stm, ~stm}</c>.</summary>
    public static byte[] TransformBothPerspectives(NnueAccumulator acc, Color sideToMove)
    {
        var result = new byte[L1];
        byte[] own = TransformPerspective(acc.Accumulation[(byte)sideToMove]);
        byte[] other = TransformPerspective(acc.Accumulation[(byte)Types.Opposite(sideToMove)]);
        Array.Copy(own, 0, result, 0, own.Length);
        Array.Copy(other, 0, result, own.Length, other.Length);
        return result;
    }

    /// <summary><c>affine_transform_non_ssse3</c> (ramo scalare, non-SIMD, condiviso da
    /// affine_transform.h e affine_transform_sparse_input.h), affine_transform.h:108-120.
    /// Layout dei pesi riga-per-output: <c>weights[outIdx*inputDim + inIdx]</c> — confermato
    /// dall'ordine di lettura di <c>read_parameters</c> (identità quando nessuna macro USE_* è
    /// definita, come nel nostro caso).</summary>
    public static int[] AffineTransform(byte[] input, sbyte[] weights, int[] biases, int inputDim, int outputDim)
    {
        var output = (int[])biases.Clone();
        for (int i = 0; i < inputDim; i++)
        {
            int inVal = input[i];
            if (inVal == 0) continue;
            for (int j = 0; j < outputDim; j++)
                output[j] += weights[i + (j * inputDim)] * inVal;
        }
        return output;
    }

    /// <summary><c>ClippedReLU::propagate</c>, ramo scalare, clipped_relu.h:167-171.</summary>
    public static byte[] ClippedRelu(int[] input, int weightScaleBitsLocal)
    {
        var output = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (byte)Math.Clamp(input[i] >> weightScaleBitsLocal, 0, 127);
        return output;
    }

    /// <summary><c>SqrClippedReLU::propagate</c>, ramo scalare, sqr_clipped_relu.h:233-240.</summary>
    public static byte[] SqrClippedRelu(int[] input, int weightScaleBitsLocal)
    {
        var output = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            long squared = (long)input[i] * input[i];
            output[i] = (byte)Math.Min(127L, squared >> ((2 * weightScaleBitsLocal) + 7));
        }
        return output;
    }

    /// <summary><c>NetworkArchitecture::propagate</c>, nnue_architecture.h:102-148 — restituisce il
    /// valore "positional" grezzo, va ancora diviso per <see cref="NnueCommon.OutputScale"/> come
    /// fa network.cpp (colonna "Positional (Layers)" dell'oracolo). La "skip connection"
    /// (<c>fc_0_out[30] - fc_0_out[31]</c>) usa l'uscita GREZZA (i32) di fc_0, non quella
    /// attivata.</summary>
    public static int Propagate(NnueLayerStack stack, byte[] transformedFeatures)
    {
        int[] fc0Out = AffineTransform(transformedFeatures, stack.Fc0Weights, stack.Fc0Biases, L1, L2);
        byte[] sqr0 = SqrClippedRelu(fc0Out, NnueCommon.WeightScaleBits + 1);
        byte[] clip0 = ClippedRelu(fc0Out, NnueCommon.WeightScaleBits + 1);

        var concat1 = new byte[L2 * 2];
        Array.Copy(sqr0, 0, concat1, 0, L2);
        Array.Copy(clip0, 0, concat1, L2, L2);

        int[] fc1Out = AffineTransform(concat1, stack.Fc1Weights, stack.Fc1Biases, L2 * 2, L3);
        byte[] sqr1 = SqrClippedRelu(fc1Out, NnueCommon.WeightScaleBits);
        byte[] clip1 = ClippedRelu(fc1Out, NnueCommon.WeightScaleBits);

        var concat2 = new byte[(L2 * 2) + (L3 * 2)];
        Array.Copy(concat1, 0, concat2, 0, concat1.Length);
        Array.Copy(sqr1, 0, concat2, concat1.Length, L3);
        Array.Copy(clip1, 0, concat2, concat1.Length + L3, L3);

        int[] fc2Out = AffineTransform(concat2, stack.Fc2Weights, stack.Fc2Biases, (L2 * 2) + (L3 * 2), 1);

        int skip0 = fc0Out[L2 - 2] - fc0Out[L2 - 1];
        long fwdOut = fc2Out[0] + skip0;

        const long multiplier = 600L * NnueCommon.OutputScale;
        long denominator = (long)NnueCommon.HiddenOneVal * (1L << NnueCommon.WeightScaleBits) * 2;

        return (int)(fwdOut * multiplier / denominator);
    }
}

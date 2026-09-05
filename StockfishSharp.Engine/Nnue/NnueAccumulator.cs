// Corrisponde alla parte "da zero" (nessuna cache/aggiornamento incrementale, rimandato a N9 —
// vedi docs/nnue-porting-plan.md) di src/nnue/nnue_accumulator.cpp
// (update_accumulator_refresh_cache) e src/nnue/nnue_feature_transformer.h (transform). Vedi
// ../Types.cs per la nota generale sul porting.

using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

/// <summary>Accumulatore per le due prospettive, ricalcolato ogni volta da zero —
/// <c>AccumulatorState::accumulation</c> + <c>psqtAccumulation</c>, nnue_accumulator.h.</summary>
public sealed class NnueAccumulator
{
    public readonly short[][] Accumulation = new short[2][];
    public readonly int[][] PsqtAccumulation = new int[2][];

    /// <summary><c>update_accumulator_refresh_cache</c> applicata a una cache vuota (nessun pezzo
    /// "rimosso", tutti i pezzi presenti sono "aggiunti") — nnue_accumulator.cpp:880-949. Somma
    /// bias + riga di peso di ogni feature attiva dei tre insiemi (PSQ, minacce, coppie di
    /// pedoni), sia per i pesi (i16/i8, per prospettiva) sia per i PSQT (i32, per bucket).</summary>
    public static NnueAccumulator ComputeFromScratch(NnueNetwork net, Position pos)
    {
        var result = new NnueAccumulator();

        foreach (var perspective in new[] { Color.White, Color.Black })
        {
            int p = (byte)perspective;
            var acc = (short[])net.Biases.Clone();
            var psqt = new int[PsqtBuckets];

            var psq = new List<int>();
            HalfKAv2Hm.AppendActiveIndices(perspective, pos, psq);
            foreach (int f in psq)
            {
                int wBase = f * L1;
                for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] + net.Weights[wBase + j]);

                int pBase = f * PsqtBuckets;
                for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.PsqtWeights[pBase + b];
            }

            var threats = new List<int>();
            FullThreats.AppendActiveIndices(perspective, pos, threats);
            Pp3Wide.AppendActiveIndices(perspective, pos, threats);
            foreach (int f in threats)
            {
                int wBase = f * L1;
                for (int j = 0; j < L1; j++) acc[j] = (short)(acc[j] + net.ThreatAndPpWeights[wBase + j]);

                int pBase = f * PsqtBuckets;
                for (int b = 0; b < PsqtBuckets; b++) psqt[b] += net.ThreatAndPpPsqtWeights[pBase + b];
            }

            result.Accumulation[p] = acc;
            result.PsqtAccumulation[p] = psqt;
        }

        return result;
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

// Corrisponde a src/nnue/network.h + .cpp (caricamento) e src/nnue/nnue_feature_transformer.h
// (layout dei pesi) della fonte upstream. Vedi ../Types.cs per la nota generale sul porting.
//
// Solo il percorso di lettura (non scrittura/esportazione) e non la variante SIMD di
// permute_weights (qui non necessaria: senza vettori l'ordine dei pesi resta quello del file,
// vedi nota in docs/nnue-porting-plan.md).

using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

/// <summary>Un singolo "layer stack" (fc_0, ac_0, fc_1, ac_1, fc_2) — <c>NetworkArchitecture</c>,
/// nnue_architecture.h:58-162. Stockfish 19 ne ha 8 copie (<see cref="LayerStacks"/>), una per
/// bucket di materiale.</summary>
public sealed class NnueLayerStack
{
    // fc_0: L1(1024) -> L2(32). Bias i32, pesi i8 — vedi affine_transform.h:417-418.
    public int[] Fc0Biases = new int[L2];
    public sbyte[] Fc0Weights = new sbyte[L2 * L1]; // [output * InputDim + input]

    // fc_1: (L2*2=64) -> L3(32).
    public int[] Fc1Biases = new int[L3];
    public sbyte[] Fc1Weights = new sbyte[L3 * (L2 * 2)];

    // fc_2: (L2*2 + L3*2 = 128) -> 1.
    public int[] Fc2Biases = new int[1];
    public sbyte[] Fc2Weights = new sbyte[1 * (L2 * 2 + L3 * 2)];

    /// <summary>Hash di <c>NetworkArchitecture::get_hash_value()</c>, nnue_architecture.h:72-86 —
    /// calcolato a mano seguendo esattamente la formula della fonte (combina i quattro hash di
    /// fc_0/ac_0/fc_1/ac_1/fc_2), non ricavato dal file. <c>Detail::read_parameters</c>
    /// (network.cpp:70-79) lo scrive come primi 4 byte prima dei dati veri di ogni layer stack.</summary>
    public const uint HashValue = 0x63337116u;

    public void ReadParameters(BinaryReader r)
    {
        uint header = r.ReadUInt32();
        if (header != HashValue)
            throw new InvalidDataException($"Hash del layer stack non combacia: atteso 0x{HashValue:X8}, trovato 0x{header:X8}.");

        ReadAffine(r, Fc0Biases, Fc0Weights, L2, L1);
        // ac_sqr_0 e ac_0 non hanno parametri (attivazioni pure) — nessuna lettura.
        ReadAffine(r, Fc1Biases, Fc1Weights, L3, L2 * 2);
        ReadAffine(r, Fc2Biases, Fc2Weights, 1, L2 * 2 + L3 * 2);
    }

    private static void ReadAffine(BinaryReader r, int[] biases, sbyte[] weights, int outDim, int inDim)
    {
        for (int i = 0; i < outDim; i++) biases[i] = r.ReadInt32();
        for (int i = 0; i < outDim * inDim; i++) weights[i] = r.ReadSByte();
    }
}

/// <summary>Il file di rete caricato — <c>FeatureTransformer</c> + gli 8
/// <see cref="NnueLayerStack"/>, network.h:41-109 + nnue_feature_transformer.h:80-428.</summary>
public sealed class NnueNetwork
{
    // Feature transformer.
    public short[] Biases = new short[L1]; // BiasType=i16, nnue_feature_transformer.h:102
    public short[] Weights = new short[L1 * PsqDimensions]; // WeightType=i16 — pesi PSQ (HalfKAv2_hm)

    // Threat + PawnPair concatenati in un solo array (stessa indice per entrambi, la fonte lo
    // richiede esplicitamente: PairFeatureSet::IndexBase == ThreatFeatureSet::Dimensions).
    public sbyte[] ThreatAndPpWeights = new sbyte[ThreatAndPpDimensions * L1]; // ThreatWeightType=i8

    public int[] PsqtWeights = new int[PsqtBuckets * PsqDimensions]; // PSQTWeightType=i32
    public int[] ThreatAndPpPsqtWeights = new int[ThreatAndPpDimensions * PsqtBuckets];

    public NnueLayerStack[] LayerStacks = new NnueLayerStack[NnueArchitecture.LayerStacks];

    /// <summary>Carica un file .nnue — <c>Network::load</c>/<c>read_header</c>/
    /// <c>read_parameters</c>, network.cpp:164 e seguenti, 300-350. Lancia se l'header non
    /// combacia o se lo stream non finisce esattamente a EOF dopo l'ultimo layer — stessa
    /// verifica della fonte (<c>stream.peek() == EOF</c>), il criterio di correttezza di N1.</summary>
    public static NnueNetwork Load(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        using var r = new BinaryReader(stream);

        uint version = r.ReadUInt32();
        if (version != NnueCommon.Version)
            throw new InvalidDataException($"Versione file .nnue non riconosciuta: 0x{version:X8} (attesa 0x{NnueCommon.Version:X8}).");

        uint hashValue = r.ReadUInt32(); // verificato più sotto, dopo aver calcolato l'hash atteso
        uint descLen = r.ReadUInt32();
        r.ReadBytes((int)descLen); // descrizione testuale, solo informativa

        var net = new NnueNetwork();
        net.ReadFeatureTransformer(r);
        for (int i = 0; i < NnueArchitecture.LayerStacks; i++)
        {
            net.LayerStacks[i] = new NnueLayerStack();
            net.LayerStacks[i].ReadParameters(r);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException(
                $"Il file .nnue non finisce a EOF dopo l'ultimo layer (letti {stream.Position} di {stream.Length} byte) — layout dei pesi probabilmente sbagliato.");

        return net;
    }

    /// <summary>Hash di <c>FeatureTransformer::get_hash_value()</c>,
    /// nnue_feature_transformer.h:144-149 — calcolato a mano (XOR degli hash dei tre insiemi di
    /// feature, poi XOR con OutputDimensions*2), verificato byte per byte contro il file scaricato
    /// (i 4 byte subito dopo l'header combaciano esattamente). Scritto da
    /// <c>Detail::read_parameters</c> (network.cpp:70-79) prima dei dati veri.</summary>
    public const uint HashValue = 0xCB685313u;

    /// <summary><c>FeatureTransformer::read_parameters</c>, nnue_feature_transformer.h:171-185.
    /// Ordine esatto della fonte: bias(LEB128), pesi threat(little-endian grezzo), PSQT
    /// threat(LEB128), pesi coppie di pedoni(little-endian grezzo), PSQT coppie(LEB128), pesi
    /// PSQ(LEB128), PSQT PSQ(LEB128).</summary>
    private void ReadFeatureTransformer(BinaryReader r)
    {
        uint header = r.ReadUInt32();
        if (header != HashValue)
            throw new InvalidDataException($"Hash del feature transformer non combacia: atteso 0x{HashValue:X8}, trovato 0x{header:X8}.");

        ReadLeb128Int16(r, Biases, L1);

        for (int i = 0; i < ThreatDimensions * L1; i++) ThreatAndPpWeights[i] = r.ReadSByte();
        ReadLeb128Int32(r, ThreatAndPpPsqtWeights, 0, ThreatDimensions * PsqtBuckets);

        for (int i = 0; i < PairDimensions * L1; i++) ThreatAndPpWeights[(ThreatDimensions * L1) + i] = r.ReadSByte();
        ReadLeb128Int32(r, ThreatAndPpPsqtWeights, ThreatDimensions * PsqtBuckets, PairDimensions * PsqtBuckets);

        ReadLeb128Int16(r, Weights, L1 * PsqDimensions);
        ReadLeb128Int32(r, PsqtWeights, 0, PsqtBuckets * PsqDimensions);
    }

    private static void ReadLeb128Int16(BinaryReader r, short[] output, int count)
    {
        var tmp = new int[count];
        NnueCommon.ReadLeb128(r, tmp, count);
        for (int i = 0; i < count; i++) output[i] = (short)tmp[i];
    }

    private static void ReadLeb128Int32(BinaryReader r, int[] output, int offset, int count)
    {
        var tmp = new int[count];
        NnueCommon.ReadLeb128(r, tmp, count);
        Array.Copy(tmp, 0, output, offset, count);
    }
}

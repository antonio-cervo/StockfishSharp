// Porting di "class TBFile" (tbprobe.cpp:240-360). Deviazione dichiarata in
// docs/syzygy-porting-plan.md: niente mmap/CreateFileMapping reale — si legge il file intero in
// un byte[] (i file oggi disponibili, fino a 5 pezzi, sono piccoli). L'aritmetica di indicizzazione
// a valle (PairsData/decompress_pairs) è comunque fedele, cambia solo il mezzo di accesso.

namespace StockfishSharp.Engine.Tablebases;

/// <summary>Lanciata al posto di <c>exit(EXIT_FAILURE)</c> della fonte quando un file .rtbw/.rtbz
/// è corrotto — la fonte C++ termina l'intero processo (undefined behavior altrimenti, essendo
/// puntatori grezzi in una regione mappata); qui invece si segnala il fallimento del probe per
/// quella singola tabella, senza abbattere l'intero motore UCI per un file dati malformato.</summary>
public sealed class TbFileCorruptedException(string message) : Exception(message);

public static class TbFile
{
    public static readonly List<string> Paths = [];

    // Magics[][4] = {{0xD7,0x66,0x0C,0xA5}, {0x71,0xE8,0x23,0x5D}} indicizzato con
    // "Magics[type == WDL]" (tbprobe.cpp:340-342): un booleano come indice intero, quindi
    // quando il tipo richiesto E' WDL l'indice vale 1 (non 0) — la coppia risulta INVERTITA
    // rispetto all'ordine "naturale" di dichiarazione. Verificato byte a byte sui file reali:
    // KQvK.rtbw inizia con 71 E8 23 5D, KQvK.rtbz con D7 66 0C A5.
    private static readonly byte[] WdlMagic = [0x71, 0xE8, 0x23, 0x5D];
    private static readonly byte[] DtzMagic = [0xD7, 0x66, 0x0C, 0xA5];

    /// <summary>Cerca <paramref name="fileName"/> nelle directory di <see cref="Paths"/>, nello
    /// stesso ordine — <c>TBFile::TBFile</c>, tbprobe.cpp:253-262.</summary>
    public static string? Find(string fileName)
    {
        foreach (string dir in Paths)
        {
            string candidate = Path.Combine(dir, fileName);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary><c>TBFile::map</c>, tbprobe.cpp:264-349 (senza la parte di mmap OS-specifica,
    /// vedi nota in cima al file) — legge l'intero file e verifica i due controlli della fonte
    /// (dimensione allineata, magic number). A differenza della fonte (che ritorna <c>data + 4</c>,
    /// saltando il magic) qui si ritorna l'array COMPLETO, magic incluso: l'allineamento a 64 byte
    /// più avanti nel parsing (<c>TbTable.Populate</c>) è definito dalla fonte rispetto all'inizio
    /// vero del file — se si "spoglia" l'array dei primi 4 byte, l'arrotondamento
    /// <c>(data+0x3F)&amp;~0x3F</c> arrotonda nella griglia SBAGLIATA (spostata di 4 byte),
    /// producendo talvolta un <c>DataOffset</c> sbagliato pur sembrando plausibile (bug reale
    /// trovato confrontando con python-chess: stesso file, DTZ atteso 1, ottenuto 3). Il chiamante
    /// fa partire il cursore di parsing a 4 (dopo il magic) invece che a 0, esattamente come la
    /// fonte fa avanzare il puntatore di 4 mantenendo lo stesso buffer.</summary>
    public static byte[] ReadTable(string path, bool isWdl)
    {
        byte[] raw = System.IO.File.ReadAllBytes(path);

        if (raw.Length % 64 != 16)
            throw new TbFileCorruptedException($"Corrupt tablebase file {path} (size % 64 != 16)");

        byte[] magic = isWdl ? WdlMagic : DtzMagic;
        for (int i = 0; i < 4; i++)
            if (raw[i] != magic[i])
                throw new TbFileCorruptedException($"Corrupted table in file {path}");

        return raw;
    }
}

// Corrisponde alla parte generale di src/nnue/nnue_common.h della fonte upstream. Vedi
// ../Types.cs per la nota generale sul porting.

namespace StockfishSharp.Engine.Nnue;

public static class NnueCommon
{
    // Versione del file di rete attesa per questo commit (Stockfish 19) — nnue_common.h:65.
    public const uint Version = 0x6A448AFAu;

    public const int OutputScale = 16;
    public const int WeightScaleBits = 6;
    public const int FtMaxVal = 255;
    public const int HiddenOneVal = 128;

    private const string Leb128MagicString = "COMPRESSED_LEB128";

    /// <summary>Decodifica LEB128 con segno — algoritmo standard (non specifico di Stockfish, vedi
    /// https://en.wikipedia.org/wiki/LEB128), usato dalla fonte per comprimere i pesi quantizzati.
    /// Porta <c>read_leb_128</c>, nnue_common.h:236-278: verifica la stringa magica, poi decodifica
    /// <paramref name="count"/> interi con segno nell'array <paramref name="output"/>.</summary>
    public static void ReadLeb128(BinaryReader reader, int[] output, int count)
    {
        byte[] magic = reader.ReadBytes(Leb128MagicString.Length);
        if (System.Text.Encoding.ASCII.GetString(magic) != Leb128MagicString)
            throw new InvalidDataException("Stringa magica LEB128 mancante o non valida.");

        uint bytesLeft = reader.ReadUInt32();
        int written = 0;
        uint result = 0;
        int shift = 0;

        while (written < count)
        {
            if (bytesLeft == 0)
                throw new InvalidDataException("Stream LEB128 esaurito prima del previsto.");

            byte b = reader.ReadByte();
            bytesLeft--;

            result |= (uint)(b & 0x7f) << (shift % 32);
            shift += 7;

            if ((b & 0x80) == 0)
            {
                int value = (shift >= 32 || (b & 0x40) == 0)
                    ? (int)result
                    : (int)(result | ~((1u << shift) - 1));
                output[written++] = value;
                result = 0;
                shift = 0;
            }
        }

        if (bytesLeft != 0)
            throw new InvalidDataException("Byte LEB128 residui dopo aver letto tutti i valori attesi.");
    }
}

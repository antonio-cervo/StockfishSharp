// Libro di aperture in formato Polyglot (.bin) — Flow D1 del piano (docs/porting-master-plan.md),
// NON un porting di Stockfish (che non ha un libro di aperture integrato: lo gestisce sempre il
// layer UCI esterno/la GUI/il bot). Adattato da ACMyChess.Engine/PolyglotBook.cs (stessa logica,
// riscritta sui tipi Position/Move/MoveGen di StockfishSharp.Engine) — a differenza di quel motore,
// qui l'arrocco non richiede alcuna reinterpretazione: sia Polyglot sia il MoveGen di questo
// porting codificano l'arrocco come "il re cattura la propria torre" (Move.ToSq = casa della
// torre, non la casa finale del re), quindi il confronto diretto from/to funziona per entrambi i
// casi (mossa normale e arrocco) senza casi speciali.

using StockfishSharp.Engine;
using File = StockfishSharp.Engine.File;

namespace StockfishSharp.Uci;

internal sealed class PolyglotBook
{
    private readonly byte[] _data;
    private readonly int _count;
    private readonly Random _rng = new();

    /// <summary>Se vero sceglie a caso pesando sui valori del libro; altrimenti la mossa col peso
    /// massimo.</summary>
    public bool VariedSelection { get; set; } = true;

    private PolyglotBook(byte[] data)
    {
        _data = data;
        _count = data.Length / 16;
    }

    public static PolyglotBook? TryLoad(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            var data = System.IO.File.ReadAllBytes(path);
            if (data.Length < 16 || data.Length % 16 != 0) return null;
            return new PolyglotBook(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Mossa di libro per la posizione corrente, oppure null se non è nel libro.</summary>
    public Move? TryGetMove(Position pos)
    {
        var entries = Lookup(PolyglotRandom.ComputeKey(pos));
        if (entries.Count == 0) return null;

        var legal = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, legal);

        var candidates = new List<(Move move, int weight)>();
        foreach (var (move16, weight) in entries)
        {
            Move? decoded = DecodeMove(move16, legal);
            if (decoded.HasValue) candidates.Add((decoded.Value, Math.Max(1, (int)weight)));
        }
        if (candidates.Count == 0) return null;

        if (!VariedSelection)
            return candidates.OrderByDescending(c => c.weight).First().move;

        int total = candidates.Sum(c => c.weight);
        int r = _rng.Next(total);
        foreach (var (move, w) in candidates)
        {
            if (r < w) return move;
            r -= w;
        }
        return candidates[0].move;
    }

    // ── Decodifica della mossa Polyglot ────────────────────────────────────────

    /// <summary>Formato Polyglot standard (16 bit): to_file(3)|to_rank(3)<<3|from_file(3)<<6|
    /// from_rank(3)<<9|promotion(3)<<12, con rank/file indicizzati "dal basso"/"da a" — la stessa
    /// convenzione di <see cref="Square"/> in questo porting (A1=0..H8=63), quindi from/to si
    /// ottengono senza alcun flip. Cerca la mossa legale corrispondente per confronto diretto
    /// (from, to, ed eventualmente il tipo di promozione).</summary>
    private static Move? DecodeMove(ushort move16, List<Move> legal)
    {
        int toFile = move16 & 7;
        int toRank = (move16 >> 3) & 7;
        int fromFile = (move16 >> 6) & 7;
        int fromRank = (move16 >> 9) & 7;
        int promoRaw = (move16 >> 12) & 7;

        Square from = Types.MakeSquare((File)fromFile, (Rank)fromRank);
        Square to = Types.MakeSquare((File)toFile, (Rank)toRank);

        PieceType promo = promoRaw switch
        {
            1 => PieceType.Knight, 2 => PieceType.Bishop, 3 => PieceType.Rook, 4 => PieceType.Queen,
            _ => PieceType.None,
        };

        foreach (var m in legal)
        {
            if (m.FromSq != from || m.ToSq != to) continue;
            if (promo != PieceType.None && (m.TypeOf != MoveType.Promotion || m.PromotionType != promo)) continue;
            return m;
        }

        return null;
    }

    // ── Lettura del file — voci ordinate per chiave, ricerca binaria ───────────

    private List<(ushort move, ushort weight)> Lookup(ulong key)
    {
        var list = new List<(ushort, ushort)>();
        int lo = 0, hi = _count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ulong k = KeyAt(mid);
            if (k == key) { found = mid; break; }
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        if (found < 0) return list;

        int i = found;
        while (i > 0 && KeyAt(i - 1) == key) i--;
        for (; i < _count && KeyAt(i) == key; i++)
            list.Add((MoveAt(i), WeightAt(i)));
        return list;
    }

    private ulong KeyAt(int i)
    {
        int o = i * 16;
        ulong k = 0;
        for (int j = 0; j < 8; j++) k = (k << 8) | _data[o + j];
        return k;
    }

    private ushort MoveAt(int i) { int o = (i * 16) + 8; return (ushort)((_data[o] << 8) | _data[o + 1]); }
    private ushort WeightAt(int i) { int o = (i * 16) + 10; return (ushort)((_data[o] << 8) | _data[o + 1]); }
}

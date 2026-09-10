// Porting di AccumulatorCaches, nnue_accumulator.h:50-88 — le "Finny Tables" (l'idea e' di Luecx,
// autore di Koivisto). Fino al 2026-09-10 questa struttura era DICHIARATAMENTE non portata, con la
// motivazione che non serve alla correttezza: un refresh completo resta corretto, solo piu' lento.
// Il profilo ha poi detto quanto costa quella lentezza — RefreshPerspective era ~10% del tempo di
// ricerca — e la fonte ce l'ha, quindi portarla AUMENTA la fedelta' invece di sacrificarla.
//
// L'IDEA: un refresh da zero somma tutte le righe di peso dei pezzi sulla scacchiera (~32 righe da
// 1024 int16). Qui invece si tiene, PER OGNI CASA DEL RE e per ogni prospettiva, un accumulatore
// gia' pronto insieme alla disposizione di pezzi che lo ha prodotto: al refresh successivo per
// quella stessa casa si applica solo la DIFFERENZA fra i pezzi memorizzati e quelli attuali, che in
// partita e' di pochissime caselle.
//
// COSA STA IN CACHE E COSA NO, esattamente come la fonte: la voce contiene solo la parte PSQ
// (pezzo-casa, HalfKAv2Hm), che dipende unicamente dalla disposizione dei pezzi. Le feature di
// minaccia e di coppia NON sono memorizzate: si ricalcolano a ogni refresh e si sommano SOPRA la
// parte PSQ mentre la si copia nell'accumulatore di destinazione. E' il motivo per cui la voce
// resta valida a lungo: dipende solo da dove stanno i pezzi.

using static StockfishSharp.Engine.Nnue.NnueArchitecture;

namespace StockfishSharp.Engine.Nnue;

/// <summary><c>AccumulatorCaches</c>, nnue_accumulator.h:56 — una cache per thread, con una voce
/// per ciascuna delle 64 case del re e per ciascuna prospettiva.</summary>
public sealed class CacheRefresh
{
    /// <summary><c>AccumulatorCaches::Entry</c>, nnue_accumulator.h:62-75.</summary>
    public sealed class Voce
    {
        /// <summary><c>accumulation</c> — la parte PSQ dell'accumulatore: bias piu' le righe dei
        /// pezzi descritti da <see cref="Pezzi"/>. Niente minacce, niente coppie.</summary>
        public readonly short[] Accumulation = new short[L1];

        /// <summary><c>psqtAccumulation</c>.</summary>
        public readonly int[] PsqtAccumulation = new int[PsqtBuckets];

        /// <summary><c>pieces</c> — la disposizione che ha prodotto <see cref="Accumulation"/>.</summary>
        public readonly Piece[] Pezzi = new Piece[Squares.Nb];

        /// <summary><c>pieceBB</c> — occupazione corrispondente a <see cref="Pezzi"/>. Ridondante
        /// con l'array, ma serve a separare in una sola AND le case "tolte" da quelle "aggiunte".</summary>
        public ulong PezziBB;

        /// <summary><c>Entry::clear</c>, nnue_accumulator.h:69-74: scacchiera VUOTA, quindi i soli
        /// bias senza alcun peso sopra. La prima volta che una casa del re viene usata, il refresh
        /// parte da qui e aggiunge tutti i pezzi — cioe' costa quanto un refresh da zero, una volta
        /// sola.</summary>
        public void Svuota(short[] biases)
        {
            biases.AsSpan(0, L1).CopyTo(Accumulation);
            Array.Clear(PsqtAccumulation);
            Array.Clear(Pezzi);            // Piece.None == 0
            PezziBB = 0;
        }
    }

    // "std::array<std::array<Entry, COLOR_NB>, SQUARE_NB>" — qui appiattito in un array 1D
    // indicizzato (casa * 2 + prospettiva): un T[,] di tipo RIFERIMENTO e' gia' costato un crash su
    // Mono/Android in un altro progetto di casa, e l'indicizzazione 1D e' anche piu' veloce.
    private readonly Voce[] _voci = new Voce[Squares.Nb * 2];

    public CacheRefresh()
    {
        for (int i = 0; i < _voci.Length; i++) _voci[i] = new Voce();
    }

    public Voce this[Square ksq, Color perspective] => _voci[((byte)ksq * 2) + (byte)perspective];

    /// <summary><c>AccumulatorCaches::clear</c>, nnue_accumulator.h:77-82 — da chiamare quando la
    /// rete cambia o a inizio partita (nella fonte: <c>Worker::clear</c>). NON va chiamata a ogni
    /// ricerca: e' proprio la persistenza fra una mossa e l'altra a rendere utile la cache.</summary>
    public void Svuota(NnueNetwork net)
    {
        foreach (var voce in _voci) voce.Svuota(net.Biases);
    }
}

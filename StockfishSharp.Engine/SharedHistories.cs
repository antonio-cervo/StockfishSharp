// Porting fedele di "struct SharedHistories" (history.h:204-257) e del "DynStats" su cui poggia
// (history.h:91-101).
//
// PERCHE' ESISTE, e perche' non basta tenerle dentro Search/MovePick come prima: la fonte divide le
// history in due gruppi ben distinti (search.h:349-357).
//   * PER THREAD (membri di Worker): mainHistory, lowPlyHistory, captureHistory,
//     continuationCorrectionHistory, ttMoveHistory.
//   * CONDIVISE (questa classe): correctionHistory, continuationHistory[2][2], pawnHistory —
//     una sola copia per nodo NUMA, usata da TUTTI i thread di quel nodo.
// Fino al 2026-09-08 questo porting le teneva tutte per thread. Due conseguenze, entrambe nella
// direzione "piu' debole a molti thread": i thread helper non si scambiavano nulla attraverso
// queste tabelle (e' il canale con cui il Lazy SMP moderno guadagna Elo oltre alla sola TT), e la
// tabella efficace era 1/N di quella della fonte, perche' le due dinamiche SCALANO col numero di
// thread: SharedHistories(next_power_of_two(threadCount)), thread.cpp:214.
//
// ACCESSO CONCORRENTE SENZA LOCK, come la fonte: pawnHistory e le correction history sono
// AtomicStats/StatsEntry<...,true> (atomiche RILASSATE, cioe' nessun ordinamento imposto), la
// continuationHistory e' Stats normale, cioe' una corsa benigna deliberata. Qui sono short[]
// normali: su .NET la lettura/scrittura di un short allineato non si spezza, quindi il peggio che
// puo' capitare e' leggere un valore vecchio o perdere un aggiornamento — esattamente il
// compromesso che fa la fonte, e che per una tabella euristica e' accettabile per costruzione.
//
// Nota su un falso allarme gia' verificato (non riaprirlo): la fonte usa UNA sola tabella di
// "CorrectionBundle" indicizzata da quattro chiavi diverse, mentre qui ci sono quattro tabelle
// separate. Non e' una differenza di comportamento — ogni tipo di correzione legge un campo diverso
// del bundle, quindi non collidono fra loro nemmeno la'. Cambia solo la disposizione in memoria.

namespace StockfishSharp.Engine;

public sealed class SharedHistories
{
    public const int PawnHistoryBaseSize = 8192;  // PAWN_HISTORY_BASE_SIZE, history.h:38
    public const int CorrHistBaseSize = 65536;    // CORRHIST_BASE_SIZE = UINT_16_HISTORY_SIZE, history.h:39-40

    /// <summary>Maschere precalcolate, come <c>sizeMinus1</c>/<c>pawnHistSizeMinus1</c> della fonte
    /// (history.h:212-213): l'indicizzazione e' un AND, quindi le dimensioni devono essere potenze
    /// di due — garantito perche' si moltiplica una base potenza di due per
    /// <c>next_power_of_two(threadCount)</c>.</summary>
    public readonly int PawnHistSizeMinus1;
    public readonly int CorrSizeMinus1;

    /// <summary>ContinuationHistoryBlock::table[2][2], history.h:198-200 — indicizzata [scacco del
    /// nodo genitore][la sua mossa era una cattura][pezzo mosso lì][casa di arrivo][pezzo di questa
    /// mossa][casa di arrivo]. NON scala col numero di thread: nella fonte e' un blocco singolo.</summary>
    /// <remarks>ARRAY PIATTO. In .NET un array multidimensionale non e' un vettore: ogni accesso
    /// paga un calcolo con controllo di limite PER DIMENSIONE, e qui le dimensioni sono SEI. Questa
    /// tabella e' letta cinque o sei volte per ogni mossa valutata, a ogni nodo: e' il punto piu'
    /// caldo del motore fuori dalla NNUE. L'indice si calcola con <see cref="IndiceContinuation"/>,
    /// nello stesso ordine di prima — [scacco del genitore][sua cattura][pezzo mosso li'][casa di
    /// arrivo][pezzo di questa mossa][casa di arrivo].</remarks>
    public readonly short[] ContinuationHistory =
        new short[2 * 2 * PieceSlots.Nb * Squares.Nb * PieceSlots.Nb * Squares.Nb];

    /// <summary>Indice piatto della continuation history, nell'ordine delle dimensioni originali.</summary>
    public static int IndiceContinuation(bool inCheck, bool captureStage, Piece pezzoPrec, Square casaPrec, Piece pc, Square to)
        => ((((((inCheck ? 1 : 0) * 2) + (captureStage ? 1 : 0)) * PieceSlots.Nb
              + (byte)pezzoPrec) * Squares.Nb
              + (byte)casaPrec) * PieceSlots.Nb
              + (byte)pc) * Squares.Nb
              + (byte)to;

    /// <summary>PawnHistory, history.h:146 — DynStats, quindi PAWN_HISTORY_BASE_SIZE per thread.</summary>
    public readonly short[] PawnHistory;

    /// <summary>Indice piatto della pawn history, nell'ordine originale [chiave dei pedoni]
    /// [pezzo][casa di arrivo]. Appiattita per lo stesso motivo della continuation history: a tre
    /// dimensioni l'accesso costa 1,47x quello su array piatto.</summary>
    public static int IndicePawn(ulong chiave, Piece pc, Square to)
        => (int)((((chiave * PieceSlots.Nb) + (byte)pc) * Squares.Nb) + (byte)to);

    /// <summary>UnifiedCorrectionHistory, history.h:189-191 — DynStats, CORRHIST_BASE_SIZE per
    /// thread. Qui divisa nei quattro campi del CorrectionBundle (vedi la nota in testa).</summary>
    public readonly short[,] PawnCorrHistory;
    public readonly short[,] MinorCorrHistory;
    public readonly short[,] NonPawnWhiteCorrHistory;
    public readonly short[,] NonPawnBlackCorrHistory;

    /// <summary>I cinque prefetch che la fonte fa DENTRO <c>do_move</c> (position.cpp:1014-1018),
    /// appena le chiavi della nuova posizione sono definitive e PRIMA di spostare il pezzo: sono le
    /// history che il nodo FIGLIO leggera' per prime, e da qui al loro uso passano tutto il resto di
    /// DoMove e l'aggiornamento dell'accumulatore NNUE — centinaia di cicli, abbastanza perche' la
    /// linea di cache arrivi.
    ///
    /// Perche' proprio queste cinque e non tutte le history: sono le uniche indicizzate da una
    /// CHIAVE, cioe' ad accesso pseudo-casuale su tabelle grandi (a un thread la pawn history da
    /// sola e' 16 MB, le quattro correction history 256 KB l'una). E' lo stesso profilo del prefetch
    /// della transposition table, che e' valso +3,8%: qui il costo e' la LATENZA di memoria, non il
    /// numero di istruzioni. Le history per thread — main, capture, continuation — restano fuori
    /// anche nella fonte, perche' si rileggono di continuo e stanno gia' in cache.</summary>
    public unsafe void PrefetchPerFiglio(Position pos, Piece pc, Square to)
    {
        if (!System.Runtime.Intrinsics.X86.Sse.IsSupported) return;

        Prefetch(ref PawnHistory[IndicePawn(pos.PawnKey & (ulong)PawnHistSizeMinus1, pc, to)]);

        Prefetch(ref PawnCorrHistory[pos.PawnKey & (ulong)CorrSizeMinus1, 0]);
        Prefetch(ref MinorCorrHistory[pos.MinorPieceKey & (ulong)CorrSizeMinus1, 0]);
        Prefetch(ref NonPawnWhiteCorrHistory[pos.NonPawnKey(Color.White) & (ulong)CorrSizeMinus1, 0]);
        Prefetch(ref NonPawnBlackCorrHistory[pos.NonPawnKey(Color.Black) & (ulong)CorrSizeMinus1, 0]);
    }

    /// <summary><c>prefetch()</c>, misc.h:130-140 — tira una linea di cache in L1 senza leggerla.</summary>
    private static unsafe void Prefetch(ref short voce)
        => System.Runtime.Intrinsics.X86.Sse.Prefetch0(
            System.Runtime.CompilerServices.Unsafe.AsPointer(ref voce));

    /// <summary><c>SharedHistories(usize threadCount)</c>, history.h:205-214 — il chiamante passa
    /// gia' <c>next_power_of_two(count)</c> (thread.cpp:214); qui l'arrotondamento e' fatto dentro
    /// per non poter essere dimenticato, ed e' l'assert della fonte reso costruzione.</summary>
    public SharedHistories(int threadCount)
    {
        int mult = NextPowerOfTwo(Math.Max(1, threadCount));
        int pawnSize = PawnHistoryBaseSize * mult;
        int corrSize = CorrHistBaseSize * mult;

        PawnHistSizeMinus1 = pawnSize - 1;
        CorrSizeMinus1 = corrSize - 1;

        PawnHistory = new short[pawnSize * PieceSlots.Nb * Squares.Nb];
        PawnCorrHistory = new short[corrSize, Colors.Nb];
        MinorCorrHistory = new short[corrSize, Colors.Nb];
        NonPawnWhiteCorrHistory = new short[corrSize, Colors.Nb];
        NonPawnBlackCorrHistory = new short[corrSize, Colors.Nb];

        Clear();
    }

    /// <summary><c>next_power_of_two</c>, misc.h — arrotonda per eccesso alla potenza di due.</summary>
    private static int NextPowerOfTwo(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    /// <summary>I tre <c>clear_range</c>/<c>fill</c> della fonte (search.cpp:696-704). La fonte li
    /// ripartisce fra i thread (ogni thread azzera la sua fetta, per toccare la memoria sul proprio
    /// nodo NUMA); qui il riempimento e' fatto una volta sola dal chiamante, il che e' equivalente
    /// nel risultato — l'unica cosa che si perde e' l'ottimizzazione NUMA di prima-toccata.</summary>
    public void Clear()
    {
        // search.cpp:699-704 — continuationHistory[inCheck][capture][...].fill(-586) per le 4 combinazioni.
        Fill(ContinuationHistory, -586);

        Fill(PawnHistory, -1338);            // clear_range(-1338, ...), search.cpp:697
        Fill(PawnCorrHistory, -5);           // clear_range(-5, ...), search.cpp:696
        Fill(MinorCorrHistory, -5);
        Fill(NonPawnWhiteCorrHistory, -5);
        Fill(NonPawnBlackCorrHistory, -5);
    }

    /// <summary>Riempimento di un array multidimensionale trattandolo come contiguo — molto piu'
    /// rapido dei cicli annidati su tabelle di decine di milioni di voci (la pawn history a 8 thread
    /// ne ha 67 milioni), e sempre corretto perche' in .NET un array multidimensionale e' un unico
    /// blocco contiguo in ordine di riga.</summary>
    private static void Fill(Array a, short value)
    {
        ref short first = ref System.Runtime.CompilerServices.Unsafe.As<byte, short>(
            ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(a));
        System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, a.Length).Fill(value);
    }
}

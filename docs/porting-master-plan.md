# Piano generale di porting — Stockfish 19 → C#

Documento di riferimento che tiene insieme tutto il progetto. I due piani di dettaglio sono
`porting-plan.md` (fasi 1-3, già eseguite) e `nnue-porting-plan.md` (fasi N1-N9).

Fonte: `../stockfish-upstream-reference/src/`, commit `edb0d9d` = **Stockfish 19**, rilasciato il
2026-09-05 (lo stesso giorno in cui è iniziato questo porting).

## Stato reale, in numeri

**Sorgente totale**: 24.849 righe (`.cpp` + `.h`, escluso `incbin/`).
**Righe lette finora**: ~7.060 (+463 `movepick.h`/`movepick.cpp`) → **28%**.

Questo numero è il punto di partenza onesto del piano: quasi tre quarti del sorgente non è
ancora stato aperto, e alcune parti già "consegnate" non sono porting veri (vedi Flusso A).

## Le tre categorie di lavoro rimanente

| Flusso | Cosa | Righe fonte | Perché |
|---|---|---|---|
| **A — Debito** | Rifare come porting fedele parti che ho scritto di mio pugno | ~6.000 | Consegnate come "fatte" ma sono codice originale, non traduzioni |
| **B — NNUE** | Valutazione neurale | ~5.700 | Mai iniziato; piano di dettaglio già scritto |
| **C — Resto** | Threading, tablebase, utilità | ~4.000 | Mai toccato |

---

## Flusso A — Il debito (parti da rifare davvero)

Queste parti **funzionano** e sono etichettate onestamente nei commenti/commit come codice
originale, ma rispetto all'obiettivo "porting reale" sono segnaposto, non lavoro finito.

### A1 — Ricerca (`search.h` 439 + `search.cpp` 2.369 = 2.808 righe) — 🟡 IN CORSO

**Fatto** (2026-09-05, vedi `docs/porting-plan.md` per il dettaglio riga per riga): negamax+PVS,
quiescenza, TT (ora con `value_to_tt`/`value_from_tt` fedeli per i punteggi di matto), mate
distance pruning, null-move, RFP, LMR base — più, appena aggiunti, **aspiration windows** (formula
di ampiezza/allargamento della finestra fedele), **Razoring** (Step 8) e **Futility pruning per
mossa figlia** (Step 9), con `improving`/`opponentWorsening` calcolati da una cronologia della
valutazione statica per ply (equivalente minimo dello `Stack` della fonte).

**Fatto anche**: `cutNode` ora tracciato attraverso tutta la ricorsione (stessa convenzione di
chiamata della fonte ai punti di ricorsione — Step 18/19/20, null-move) e **Internal Iterative
Reduction** (Step 11) sopra questa base. Verificato: stesso bestmove/punteggio di prima
(depth 6-10, 4 posizioni incluso Kiwipete), ~4% nodi in meno a depth 10 sulla posizione iniziale.

**ProbCut FATTO** (entrambi i rami — Step 12 "vero" con verifica di quiescenza + ricerca ridotta
sulle catture con SEE sopra soglia, e Step 13 "piccola idea" solo da TT, quest'ultimo attivo anche
sotto scacco). Verificato: stesso bestmove/punteggio di prima su tutte le posizioni di test
(incluse 2 posizioni tattiche nuove — una con una promozione a donna vincente, `d7c8q`, bestmove
combaciante con l'oracolo), nessuna regressione, piccola ulteriore riduzione di nodi.

**CorrectionHistory FATTA** (`correction_value`/`to_corrected_static_eval`/
`update_correction_history`, search.cpp:85-131): la valutazione statica (Step 5), il margine di
futility (Step 9) e l'aggiornamento a fine nodo (Step 23, incluso lo smussamento di bestValue
verso beta sui fail-high non decisivi, search.cpp:1558-1560, non ancora portato prima) usano ora
il vero `correctionValue` — pedoni/pezzi minori/non-pedoni bianco/nero (hash-indicizzati) più
continuation correction history a 2 livelli (ss-2, ss-4). Verificato: 62/62 test, bestmove
identico su tutte le posizioni di test prima/dopo; punteggi leggermente diversi in alcune
posizioni (atteso e corretto: la correction history esiste apposta per correggere la valutazione
statica, quindi il suo effetto sul punteggio non è un segno di regressione).

**Reduction() (formula LMR vera) e Step 15 (potatura a profondità bassa) FATTI**, ricontrollati
riga per riga contro `search.cpp:1152-1232`/`1885-1888` due volte. `reduction()` è stabile e
verificato (nessuna regressione, nodi drasticamente ridotti — es. Kiwipete depth 10: da 98585 a
~9275). Lo Step 15 aveva mostrato un caveat: su una posizione con una promozione a donna vincente
(verificata contro l'oracolo come `d7c8q`) la mossa oscillava fra profondità vicine. Ipotesi
formulata: lavora in coppia con le Singular Extensions come rete di sicurezza.

**Singular Extensions FATTE** (Step 16, search.cpp:1234-1303): ricerca di verifica sulla stessa
posizione/ply con la mossa di TT esclusa (nuovo parametro `excludedMove` su `Negamax`), estensione
se singolare, multi-cut ed estensione negativa altrimenti. Richiesto `is_shuffling()` e il riuso
della valutazione statica già calcolata quando `excludedMove` è impostata.

**Ipotesi CONFERMATA**: con le Singular Extensions, la posizione del caveat resta stabile su
`d7c8q` da depth 8 in poi con cronologia "scaldata" da ricerche precedenti sulla stessa posizione
(come avviene dentro l'iterative deepening di una singola "go depth N", che scalda da profondità
1). Resta un residuo di instabilità "a freddo" (prima "go depth N" su una posizione mai vista può
ancora dare `d7c8r` a depth 9) — non risolto, probabile conseguenza delle parti ancora mancanti
(generazione a stadi vera, resto della history in `OrderMoves`) che nella fonte concorrono tutte
alla stabilità fin dalle prime iterazioni. Verificato: 62/62 test, nessuna nuova regressione sulle
altre posizioni di test.

**Bonus "countermove" su fail-low puro FATTO** (search.cpp:1578-1609, l'ultimo ramo mancante dello
Step 23): premia la mossa del genitore quando nessuna mossa del nodo corrente batte alpha.
Miglioramento incrementale sul caveat: la posizione di prova resta stabile su `d7c8q` a depth 7-10
(prima solo 8-10), ancora non a depth 6 e 12 — residuo non risolto, atteso richiedere la
generazione a stadi vera e/o il resto della history in `OrderMoves` per chiudersi del tutto.

**Hindsight depth adjustment da priorReduction FATTO** (search.cpp:807-808,866-870, l'ultimo pezzo
mancante di Flow A1 elencato): se il genitore ha ridotto molto la profondità con LMR ma la sua
posizione non peggiora, un ply in più qui compensa; se ha ridotto un po' e le valutazioni statiche
combinate sembrano già buone, un ply in meno. **Flow A1 è ora COMPLETO** per quanto riguarda le
tecniche di search.cpp applicabili a thread singolo (resta solo Lazy SMP, Flow C). Ulteriore
miglioramento sul caveat: la posizione di prova resta stabile su `d7c8q` a depth 7-12 (prima 7-10).

**Manca ancora**: tutta la taratura fine dei margini rimasti, la struttura
`Worker`/`RootMove`/`Stack` completa della fonte (qui minimizzata a quanto serve). L'aspiration
window usa lo score dell'iterazione precedente al posto della media mobile pesata per "effort"
della fonte (richiede bookkeeping per-root-move non ancora presente). `followPV` (segue la riga
principale dell'iterazione precedente) non è portato — la condizione di IIR e dello Step 15 qui è
quindi leggermente più ampia di quella esatta della fonte.

⚠️ È il file più grande del progetto. Da solo vale più di tutto quello portato finora — ogni
tecnica va aggiunta e verificata una alla volta (nessuna regressione sui test esistenti + confronto
mosse/nodi con l'oracolo su alcune posizioni), come per N1-N8.

### A2 — Ordinamento mosse (`movepick.h` 78 + `movepick.cpp` 383 + `history.h` 261 = 722 righe) — 🟡 IN CORSO

**Scoperta**: la fonte (questa versione) ha ELIMINATO le killer move classiche — ordina solo con
history a più livelli (main+continuation+capture). Le killer restano in `MovePick.cs` come
euristica NOSTRA aggiuntiva, non della fonte — candidate alla rimozione quando arriverà la
continuation history vera.

**Fatto**: `ButterflyHistory` (main history) con l'aggiornamento "a gravità" fedele
(`StatsEntry::operator<<`, history.h:70-77, D=7183) e le formule di bonus/malus di
`update_all_stats` (search.cpp:1957-1998, solo il ramo mosse quiete — capture history non
portata). Le statistiche si aggiornano una volta a fine ciclo mosse (quando esiste una bestMove),
non più ad ogni taglio beta. Da qui corretta anche una semantica pre-esistente di `bestMove` in
`Search.cs`: si aggiorna solo quando una mossa supera davvero alpha, non ogni volta che migliora
il punteggio grezzo (un nodo fail-low puro ora lascia bestMove a null come nella fonte).

Verificato: 62/62 test, bestmove identico su tutte le posizioni di test prima/dopo (incluse le due
posizioni tattiche e Kiwipete). A differenza degli Step di Flow A1 (pruning, sempre a parità di
risultato), qui il conteggio nodi NON è garantito solo in discesa: cambiare l'ordinamento delle
mosse senza le tecniche che ne sfruttano appieno l'informazione (LMR adattivo per statScore,
continuation history) può aumentare i nodi in alcune posizioni/profondità pur restando corretto —
osservato empiricamente (depth 10 da 105924 a 141169 nodi, stesso bestmove/punteggio). Atteso
migliorare quando arriveranno i pezzi mancanti, non prima.

**ContinuationHistory PARZIALE**: aggiunta la cronologia `Stack::currentMove`/`moved_piece` per ply
in `Search.cs` (stesso schema di indice di `_staticEvalHistory`), e su questa base un solo livello
di lookback (ss-1, il peso maggiore dei 6 della fonte — 520 su {520,390,145,251,66,209}), con una
sola tabella invece delle 4 `[inCheck][captureStage]` della fonte. Usata sia per l'aggiornamento
(`UpdateStats`) sia per l'ordinamento (`OrderMoves`).

Verificato: 62/62 test, bestmove identico su tutte le posizioni di test prima/dopo. Conteggio nodi
misto (alcune posizioni leggermente su, altre giù) — atteso a questo stadio parziale, vedi nota
sopra sulla main history.

**CapturePieceToHistory FATTA** (history.h:135, D=10692): bonus alla cattura migliore, malus a
quelle scartate (search.cpp:1993-2011), usata anche come spareggio in `OrderMoves` accanto al
guadagno SEE (termine dominante) — combinazione nostra, la fonte la userebbe dentro il vero
MovePicker a stadi, non ancora portato.

**ContinuationHistory COMPLETA sui 6 livelli di lookback** (ss-1..ss-6, pesi
{520,390,145,251,66,209}, moltiplicatori `CMHCMultipliers` con "positiveCount" sequenziale, e la
selezione `[inCheck del genitore][la sua mossa era una cattura]` — 4 tabelle come `do_move`,
search.cpp:663-671, non più una sola). Richiesto: `Search.cs` traccia ora
`currentMove`/`moved_piece`/`inCheck`/`captureStage` per ply fino a 6 indietro (`StackOffset=7`,
come `stack+7` della fonte). L'ordinamento (`OrderMoves`) usa solo ss-1 dei 6 (nota già presente).

Verificato (per entrambi i commit): 62/62 test, bestmove identico su tutte le posizioni di test
prima/dopo (incluse Kiwipete e le due posizioni tattiche).

**LowPlyHistory e TTMoveHistory FATTE**: LowPlyHistory (history.h:130-132, D=7183, 5 ply) si
azzera a ogni ricerca (non a ogni partita, `ResetForSearch`, come `iterative_deepening`,
search.cpp:326) e partecipa all'ordinamento per i primi 5 ply; TTMoveHistory (history.h:196,
D=8192) è un contatore globale aggiornato ma non ancora usato in nessuna formula (la fonte la usa
in punti non ancora portati). Verificato: 62/62 test, bestmove identico su tutte le posizioni di
test prima/dopo.

**PawnHistory FATTA PARZIALMENTE** (history.h:146, D=8192, chiave = zobrist dei pedoni & 8191):
portato il punto di aggiornamento in `update_quiet_histories` (search.cpp:2056-2057); gli altri
due usi della fonte (bonus di ordinamento da differenza di valutazione statica, bonus al
"countermove" quieto su fail-low puro) restano non portati perché le tecniche a cui appartengono
non lo sono. Verificato: 62/62 test, bestmove identico su tutte le posizioni di test prima/dopo;
nodi in calo su alcune posizioni (depth 10 startpos: 98585, meglio della baseline pre-history
105924 — il sistema di history comincia a ripagare ora che è quasi completo).

**Tutte le history di `history.h` sono ora almeno parzialmente portate.**

**Generazione a stadi vera FATTA** (`movepick.h`/`movepick.cpp`, 463 righe): nuova classe
`MovePicker.cs` — `Stages` enum identico alla fonte (aritmetica su `stage` inclusa), entrambi i
costruttori (ricerca principale/quiescenza e ProbCut), `Score<CAPTURES/QUIETS/EVASIONS>` fedeli
(inclusa la formula quiete completa: main+pawn history, continuation history ai livelli 0,1,2,3,5
— il livello 4/ss-5 saltato di proposito come nella fonte —, bonus scacco con SEE, bonus/malus
"minacciato da pezzo di valore inferiore" via il nuovo `Position.AttacksBy`, bonus low-ply),
`partial_insertion_sort` (solo ramo scalare, niente `MoveSorter` AVX-512) e `select<Pred>`.
`MovePick.cs` torna a essere solo il contenitore delle history (il ruolo di `Worker` nella fonte);
le vecchie killer move (euristica nostra, non della fonte) e il vecchio `OrderMoves` eager sono
stati rimossi. `Negamax`/`Quiesce` ora consumano `mp.NextMove()` in un ciclo `while`, con
`pos.Legal(m)` inline come nella fonte (generazione pseudo-legale) — **scoperto e corretto in
questo passaggio**: il ciclo mosse non saltava mai `excludedMove` (il parametro per le Singular
Extensions), un buco di fedeltà pre-esistente mai notato prima perché la lista `GenType.Legal`
usata finora non aveva mai bisogno di quel salto esplicito. `moveCount` ora conta solo le mosse
legali/non escluse (search.cpp:1116-1137), e il caso "0 mosse" è gestito a fine ciclo
(search.cpp:1562-1566: matto/stallo, o `alpha` semplice se `excludedMove` era impostata) invece
che con un controllo prima del ciclo. Il ciclo ProbCut (Step 12) resta com'era (genera catture +
filtro SEE inline, non ancora passato a `mp`) — non necessario per la correttezza (l'ordine delle
mosse lì incide solo su quale candidata causa il taglio per prima, non sul risultato).

Verificato: 69/69 test; UCI a mano su 5 posizioni (apertura, una posizione tipo Kiwipete, una
promozione a donna vincente `d7c8q`, un finale di torri, una posizione di mediogioco) con `git
stash` prima/dopo — **stesso bestmove su tutte e 5**, inclusa `d7c8q` (punteggio di matto
identico). Nodi/sec sono PEGGIORATI in questo passaggio (es. `d7c8q`: 2,29M nodi baseline contro
5,07M nodi nuovi per una profondità raggiunta leggermente più bassa in tempo fisso) — costo delle
allocazioni `List<Move>` fresche ad ogni stadio dentro `MovePicker` (una per CAPTURE_INIT, una per
QUIET_INIT/EVASION_INIT, per ogni nodo), non ammortizzate come nella fonte (che scrive
direttamente in un buffer `moves[MAX_MOVES]` sullo stack senza allocare). Correttezza confermata,
prestazioni no — riutilizzare buffer invece di allocare è l'ottimizzazione naturale successiva.

**Ottimizzazione allocazioni FATTA**: i due array `moves`/`values` e il buffer di generazione
temporaneo di `MovePicker` (prima allocati `new` a ogni istanza, cioè a ogni nodo) sono ora passati
dal chiamante (`Search`) — un buffer per livello di profondità, riusato fra tutti i nodi allo
stesso ply (mai due nodi attivi contemporaneamente allo stesso ply: `Negamax(depth&lt;=0)` delega
sempre a `Quiesce` prima di costruire il proprio `MovePicker`). Stesso trattamento per la lista
temporanea del ciclo ProbCut (Step 12), che ora riusa il buffer del proprio ply invece di allocare.
Verificato: 69/69 test; `bench 16 1 8` con `git stash` prima/dopo — **nodi IDENTICI su tutte le 51
posizioni** (507.992 totali in entrambi i casi), quindi bestmove necessariamente identico
ovunque (l'ottimizzazione tocca solo l'allocazione di memoria, non la logica). Nota onesta: il
guadagno di nodi/secondo non è risultato misurabile in questo bench (~162-170k nodi/sec in
entrambe le versioni, differenza entro il rumore fra le esecuzioni) — il costo dominante a questa
scala è altrove (valutazione NNUE, generazione mosse pseudo-legali), non la pressione GC dei
buffer eliminati. La minore pressione sul garbage collector resta comunque un beneficio reale (meno
cicli di raccolta su run più lunghe/con hash grandi), solo non quantificabile con questo test.

**Bonus da differenza di valutazione statica FATTO** ("use static evaluation difference to improve
quiet move ordering", search.cpp:978-986): non collegato a un taglio o a `bestMove` — a ogni nodo
non sotto scacco la cui mossa del genitore non era né sotto scacco né una cattura, il segno/
ampiezza della sorpresa fra la valutazione statica di lì e quella di qui aggiorna sempre la main
history del genitore (e, se non c'è già un hit di TT qui e il pezzo/mossa del genitore non erano
un pedone/una promozione, anche la sua pawn history) — indipendentemente dall'esito della ricerca
di questo nodo. Nuovi `MovePick.ApplyEvalDiffMainBonus`/`ApplyEvalDiffPawnBonus`. **Questo era
l'ultimo uso mancante di PawnHistory**: ora tutti e tre gli usi della fonte sono portati.

Correzione a una nota precedente: `reduction()`/`ComputeStatScore` (Step 18, search.cpp:1342-1349)
in realtà usano GIÀ solo main+contHist[0,1] anche nella fonte reale — non era un gap, la nota
precedente in questo documento era imprecisa. Stesso discorso per `ComputeQuietPruningHistory`
(Step 15, search.cpp:1200-1202): cont[0]+cont[1]+pawn è la formula esatta della fonte. **Flow A2 è
quindi COMPLETO** salvo prestazioni (le allocazioni di `MovePicker`, vedi sopra).

Verificato (questo passaggio): 69/69 test, stesso bestmove su tutte le 5 posizioni di test
(inclusa `d7c8q`) prima/dopo via `git stash`.

### A3 — Gestione del tempo (`timeman.h` 70 + `timeman.cpp` 144 = 214 righe) — ✅ FATTO

`StockfishSharp.Engine/TimeManagement.cs`: porting fedele di `TimeManagement::init`
(timeman.cpp:46-142) — modello a due livelli optimum/maximum, entrambe le modalità "x basetime +
z incremento" e "x mosse in y secondi", `originalTimeAdjust` calcolato una volta per partita,
move overhead (default 10ms come la fonte), ponder (bonus +25% sul tempo). Wired in
`StockfishSharp.Uci/Program.cs`, sostituisce l'euristica ad-hoc `tempo/30+inc*0.5` di prima.

**NON portato** (deliberatamente, poco rilevanti per questo motore): `nodestime`/"nodes as time"
(nessuno lo usa in pratica), la logica di estensione dinamica del budget durante la ricerca in
base ai cambi di best-move (`Worker::check_time`, un'altra parte di Flow A4/UCI non ancora
portata) — qui il budget calcolato da `Init` è usato direttamente come limite fisso della ricerca.

Verificato: 66/66 test (4 nuovi su `TimeManagement`: optimum&le;maximum, più tempo disponibile
dà più budget, nessun orologio dà budget illimitato, modalità "x mosse in y secondi" resta entro
il tempo rimasto). Sanity check via UCI con `wtime`/`btime`/`winc` realistici: tempi per mossa
plausibili (~10s su un orologio 5+0 a centropartita).

### A4 — Livello UCI (`uci.cpp` 704 + `ucioption.cpp` 213 + `engine.cpp` + `benchmark.cpp` ≈ 1.600)
**Oggi**: ~200 righe in `Program.cs` — i comandi minimi per giocare, PIÙ una correzione pratica
importante non presente nella fonte come tale (qui il layer UCI non è comunque un porting fedele):
**`go` ora gira su un task in background invece di bloccare il ciclo principale**, con supporto
vero al comando `stop` (prima impossibile: una ricerca sincrona non poteva mai leggere "stop"
finché non finiva da sola) e a `go infinite`. Verificato: `isready` risponde subito anche durante
una ricerca attiva, `stop` interrompe `go infinite` in pratica istantaneamente, 69/69 test
(invariati, non toccano Program.cs).

**Comando `bench` FATTO** (scope ridotto): porta `UCIEngine::bench` (uci.cpp:248-312) +
`Benchmark::setup_bench` (benchmark.cpp:395-447) — la lista `Defaults` REALE (51 posizioni, incluse
le 2 Chess960, copiata identica, incluse le mosse incorporate in alcune righe) eseguita a profondità
fissa (default 13, come la fonte), con `setoption`/`ucinewgame` incorporati elaborati come nella
lista stessa, riepilogo finale identico (`Total time (ms)`/`Nodes searched`/`Nodes/second`) su
stderr come la fonte. Ridotto rispetto alla fonte: solo `limitType` `depth`/`movetime` (non
`eval`/`nodes`/`perft`), solo `fenFile` `default` (non un file esterno o `current`), `threads`
accettato per compatibilità di sintassi ma ignorato (motore sempre a thread singolo). Aggiunto
anche il supporto minimo a `UCI_Chess960` (opzione + `setoption`, usata da `HandlePosition` e
dalle 2 posizioni Chess960 della lista `Defaults`), assente prima.

**Bug scoperto e corretto testando `bench`**: a matto/stallo, `Search_` imposta `result.BestMove`
a `Move.None` (non `null` — la TT salva sempre `bestMove ?? Move.None`), ma sia `HandleGo` sia il
nuovo `HandleBench` controllavano solo `.HasValue`, quindi stampavano `bestmove a1a1` (`Move.None`
ha `from=to=A1`) invece di `bestmove 0000` — una mossa ILLEGALE che un client UCI reale (GUI,
lichess-bot) avrebbe provato a giocare a fine partita. Corretto in entrambi i punti.

**Manca ancora**: `bench 16 1 6` a mano completa le 51 posizioni senza errori (incluse le 2
Chess960), riepilogo coerente; `go` normale (non-bench) ancora corretto dopo la correzione del bug
`a1a1`.

**Comandi di debug `d`/`eval`/`flip`/`compiler`/`--help`/`help`/`--license`/`license`/`go perft N`
FATTI** (uci.cpp:147-183,224-225): `d` porta `operator<<(ostream&, const Position&)`
(position.cpp:67-103, senza la parte tablebase WDL/DTZ — Flow C2 non ancora portato) — griglia
ASCII, FEN, chiave esadecimale, checkers. `eval` mostra la valutazione statica corrente. `compiler`
non ha un vero compilatore C++ da interrogare: stampa l'equivalente runtime .NET (versione SDK, OS,
architettura). `go perft N` usa `Perft.Run` già esistente (verificato: `perft(4)` dalla posizione
iniziale = 197.281, il valore standard pubblicato).

`flip` porta `Position::flip` (position.cpp:1573-1603) in `Position.cs` — porting fedele
inconsueto: opera sulla STRINGA FEN (non sulle bitboard interne) esattamente come la fonte, con lo
stesso trucco (scrive il nuovo colore in maiuscolo apposta, sapendo che un passaggio successivo di
toggle-case su pezzi+colore+arrocco lo trasforma in minuscolo). Insieme, anche `Position.PosIsOk`
(position.cpp:1609-1668, controlli di coerenza interna per debug — qui restituisce `bool` invece di
`assert`) e `Position.MaterialKeyIsOk` (banale, ricalcolo indipendente della chiave materiale).
Questi ultimi due chiudono **A5 al 100%** (`pos_is_ok`/`material_key_is_ok`/`flip` erano l'unica
voce rimasta).

Verificato in modo indipendente (nuovo `PositionUtilTests.cs`): `PosIsOk`/`MaterialKeyIsOk` veri su
4 posizioni standard; `Flip` è involutivo (specchiare due volte torna esattamente alla FEN
originale, verificato su 4 posizioni incluso un arrocco/promozione disponibili); una posizione
specchiata ha ESATTAMENTE lo stesso numero di mosse legali dell'originale (perft prima/dopo `Flip`
identico, su 2 posizioni) — proprietà scacchistica indipendente dall'implementazione, verificata
col motore di perft già validato contro i valori pubblicati. 95/95 test totali (85 precedenti + 10
nuovi); verificato a mano via UCI: `d`/`eval`/`flip`/`compiler`/`go perft 4` tutti corretti in
sequenza sulla stessa sessione.

**Manca ancora** (A4, meno urgente — completezza di protocollo, non forza di gioco):
infrastruttura opzioni generica (`Option`/`OptionsMap`), `setoption` completo oltre a
`Hash`/`UCI_Chess960`, `MultiPV`, `UCI_LimitStrength`/`UCI_Elo`, `UCI_ShowWDL`, conversione
punteggi WDL, `Skill Level`, `export_net`, `speedtest` (`setup_benchmark`, benchmark.cpp:449-528,
un secondo comando di benchmark su partite reali per lo SPRT — non essenziale, lista
`BenchmarkPositions` enorme non copiata), `ponder`/`ponderhit` (pondering vero).

**Bug reale di correttezza trovato e corretto (2026-09-06, due partite perse dal vivo sul
bot)**: `MoveToUci`/`ParseUciMove` (scritte come codice pratico fin dal primissimo commit del
progetto, `0967bda` — questo file non è mai stato un porting, la fedeltà a `uci.cpp` è sempre
stata rimandata a questa stessa Fase A4) confrontavano le mosse sulle case grezze, ma la
rappresentazione interna dell'arrocco è "il re cattura la propria torre" (Move.ToSq=casa
della torre) mentre una GUI/bot non-Chess960 manda sempre la notazione standard (e1g1, non
e1h1) — ogni arrocco nella cronologia veniva scartato silenziosamente, disallineando la
posizione interna per il resto della partita. La lista "manca ancora" qui sopra non aveva mai
segnalato esplicitamente questo buco (nessuna verifica manuale precedente aveva mai fatto un
vero giro "GUI manda e1g8 dopo un arrocco reale"): serviva una partita vera per farlo
emergere. **Corretto** portando fedelmente `UCIEngine::move`/`UCIEngine::to_move`
(uci.cpp:611-644, verificate riga per riga): `MoveToUci` converte ora la casa di arrivo
dell'arrocco alla casa finale del re quando non Chess960; `ParseUciMove` confronta ogni mossa
legale con la stringa in arrivo convertendola PRIMA con `MoveToUci` invece di confrontare le
case grezze — la stessa tecnica esatta della fonte, non più codice pratico proprio.

### A5 — Parti non lette di `position.cpp` (~700 righe) — 🟡 IN CORSO

**Rilevazione patta/ripetizione FATTA** (`is_draw`/`is_repetition`/`has_repeated`/
`upcoming_repetition` + le tabelle cuckoo di Marcel van Kervinck, position.cpp:106-162,1496-1568):
`Position.Repetition` ora calcolato davvero in `DoMove` (prima sempre 0), le tabelle cuckoo
costruite in `Zobrist.Init()` (stesso ordine/RNG della fonte, quindi stesso layout). Wired in
`Search.cs`: Step 2 (patta immediata a inizio nodo) e il controllo "ripetizione imminente"
all'inizio di `search()` (search.cpp:736-742), entrambi mai portati prima — il motore prima
d'ora non rilevava MAI patte per ripetizione o regola delle 50 mosse durante la ricerca.

Verificato: 69/69 test (3 nuovi su `RepetitionTests`, incluso un controllo per esaustione su
tutte le mosse legali che replica l'invariante della fonte stessa per `upcoming_repetition` — deve
combaciare esattamente con "esiste una mossa dopo la quale `IsDraw` diventa vera"), nessuna
regressione sulle posizioni di test esistenti, verificato a mano che una tripla ripetizione reale
via UCI riporta un punteggio drasticamente ridotto rispetto al materiale in campo.

**`update_piece_threats`/`DirtyThreats` FATTO** (position.cpp:1189-1291, solo il ramo scalare —
niente `write_multiple_dirties` AVX-512ICL): nuovo `DirtyThreat.cs` (struct con campi diretti
invece del bit-packing a 32 bit della fonte, che lì serve solo per le istruzioni SIMD non portate)
e `Position.UpdatePieceThreats`, che calcola sia le minacce dirette che un pezzo genera/riceve da
una casa sia quelle "scoperte" da sliders la cui linea di vista passa per quella casa (via
`ProcessSliders`/`RayPass`, già presente dal porting precedente). `PutPiece`/`RemovePiece`/
`MovePiece`/`SwapPiece`/`DoCastling`/`DoMove` (già strutturati con questi helper dal porting
originale, con un commento che segnalava esplicitamente "dts aggiunto nella fase NNUE") ora
accettano tutti un `List&lt;DirtyThreat&gt;?` opzionale (default null, nessun costo per i chiamanti
esistenti — l'unica ricerca in produzione non lo passa ancora).

Verificato in modo indipendente (stesso principio già usato per SEE/perft/l'accumulatore NNUE):
nuovo `DirtyThreatsTests.cs`, una funzione "brute force" (nessun raggio/scoperto, verifica diretta
pezzo-per-pezzo con gli attacchi standard + la regola dichiarativa "chi può minacciare chi" della
feature) calcola l'insieme completo delle minacce prima e dopo ogni mossa; applicando il diff dei
`DirtyThreat` generati all'insieme "prima" si ottiene esattamente l'insieme "dopo" — su 6 posizioni
(incluse 2 con arrocco disponibile, una con promozione imminente) alla radice, più verifica
ricorsiva fino a profondità 2 su Kiwipete e sulla "Position 4" standard (arrocco+promozione+cattura
in sequenza). 77/77 test totali; `bench 16 1 8` con `git stash` prima/dopo — nodi IDENTICI (il
refactor di RemovePiece/PutPiece/MovePiece/SwapPiece per accettare il parametro opzionale non
cambia alcun comportamento quando non usato, come da progetto).

**`DirtyPiece`/`DirtyPawnPairs` FATTI** (types.h:296-306,347-350): a differenza di `DirtyThreats`
sono puramente descrittivi (nessun calcolo, solo popolare campi con valori già noti in `DoMove`) —
`DirtyPiece.cs` (classe mutabile, popolata in più punti sparsi di `DoMove`/`DoCastling` esattamente
come la fonte fa con il puntatore `dp`) e `DirtyPawnPairs.cs` (bitboard pedoni prima/dopo, per la
feature Pp3Wide). Stessi parametri opzionali (`null` di default) di `DirtyThreats`.

Verificato in modo indipendente: nuovo `DirtyPieceTests.cs` — applicare i campi di `DirtyPiece` (nel
loro significato dichiarato: rimuovi `RemovePc` da `RemoveSq`, sposta `Pc` da `From` a `To`,
aggiungi `AddPc` su `AddSq`) a una copia della board "prima" di ogni mossa legale deve produrre
esattamente la board "dopo" osservata direttamente, su 4 posizioni (mosse/catture/arrocco/en
passant/promozioni/promozione con cattura); `DirtyPawnPairs.Before/After` confrontati contro le
bitboard pedoni lette direttamente. 81/81 test totali; `bench 16 1 8` — nodi identici (nessuna
regressione).

**Tutti e 3 i meccanismi "dirty" della fonte sono ora presenti e verificati indipendentemente.**
Manca solo chi li CONSUME: N9, l'aggiornamento incrementale vero dell'accumulatore NNUE — un pezzo
a sé, che tocca `NnueAccumulator.cs` per tutte e 3 le feature (HalfKA via `DirtyPiece`, FullThreats
via `DirtyThreats`, Pp3Wide via `DirtyPawnPairs`), oggi ancora ricalcolato da zero a ogni
valutazione (corretto, verificato bit-esatto contro l'oracolo N1-N8 — solo non incrementale).

**Manca ancora** (minore, non prerequisito di nulla): `pos_is_ok`, `material_key_is_ok`, `flip`,
`dtz_is_dtm` (debug/tablebase).

---

## Flusso B — NNUE

Piano di dettaglio in `nnue-porting-plan.md` (fasi N1-N9). Riassunto:

N1 caricamento file → N2 indici feature → N3 accumulatore + **verifica colonna PSQT** → N4
quantizzazione → N5 layer → **verifica colonna Positional** → N6 involucro `evaluate()` → **verifica
Final evaluation** → N7 integrazione → N8 **AVX512ICL meno VNNI** → N9 aggiornamento incrementale.

**N1-N8 FATTI E VERIFICATI** (2026-09-05): motore gioca con la vera valutazione NNUE via UCI (non
più il placeholder materiale+PSQT), percorso AVX512 attivo su questa macchina per accumulatore e
layer, tutto confermato contro l'oracolo. N8 usa una meccanica SIMD diversa dalla fonte per
`transform_perspective`/`AffineTransform` (niente permutazione pesi/trucco packus/maddubs — vedi
`nnue-porting-plan.md` per il perché) — stesso risultato numerico, verificato bit-esatto contro lo
scalare oltre che contro l'oracolo. Resta solo N9 (aggiornamento incrementale dell'accumulatore,
prerequisito di C1 Lazy SMP).

**N9, infrastruttura FATTA** (nnue_accumulator.h+.cpp, nucleo senza due ottimizzazioni della fonte
— vedi nota in `AccumulatorStack.cs` per il perché sono rimandabili senza intaccare la
correttezza): niente Finny Tables (cache dei refresh per casa del re, solo velocità), niente
"hybrid update"/`backward_update_incremental` (ripiena i frame intermedi non ancora calcolati
quando manca un accumulatore riusabile in avanti — qui si fa sempre un refresh completo in quel
caso, corretto ma meno efficiente nei ply consecutivi senza valutazione statica, es. sotto scacco).

Nuovo `AccumulatorStack.cs`: `Push`/`Pop`/`Evaluate`/`FindLastUsableAccumulator`/
`ForwardUpdateIncremental`, fedeli a nnue_accumulator.cpp:67-193. `NnueAccumulator.cs` fonde
`Accumulator`+`Dirties` della fonte in un'unica classe (separate lì solo per un dettaglio di
ereditarietà multipla C++, irrilevante qui) e guadagna `RefreshPerspective`/
`ApplyIncrementalDelta`/`Subtract*` (simmetrici degli `Add*` già esistenti). `NnueFeatures.cs`
guadagna `AppendChangedIndices` per tutte e 3 le feature (`MakeIndex` esisteva già da N1-N8):
HalfKA da un `DirtyPiece` (half_ka_v2_hm.cpp:89-100), FullThreats iterando direttamente la lista di
`DirtyThreat` già generata da `Position.UpdatePieceThreats` (full_threats.cpp:261-285), Pp3Wide
confrontando le bitboard pedoni prima/dopo (pp_3wide.cpp:144-169, ramo scalare).

**Bug di trascrizione trovato e corretto durante la verifica**: `Pp3Wide.AppendChangedIndices`
usava il bitboard "aggiornato" ORIGINALE intatto invece della variabile di loop che si riduce
progressivamente (`u` nella fonte, dopo `pop_lsb`) — generava ogni coppia di pedoni DUE VOLTE
quando entrambi i pedoni della coppia erano "aggiornati" nella stessa mossa (es. una cattura di
pedone), corrompendo silenziosamente l'accumulatore in quei casi specifici.

Verificato in modo indipendente (nuovo `NnueIncrementalTests.cs`, stesso principio già usato per
l'accumulatore "da zero" negli oracoli N1-N8): per ogni mossa raggiunta durante una perft (3
posizioni, incluso un arrocco che esercita `RequiresRefresh`), l'accumulatore mantenuto
incrementalmente da `AccumulatorStack` è bit-esatto — non solo la valutazione finale, gli interi
stessi di `Accumulation`/`PsqtAccumulation` — contro `NnueAccumulator.ComputeFromScratch`
ricalcolato indipendentemente sulla stessa posizione.

**N9 COMPLETO — wiring in Search.cs FATTO**: `_accumulatorStack.Push()` prima di ogni
`Position.DoMove` REALE (ciclo principale, ProbCut, quiescenza) con `Pop()` dopo il corrispondente
`UndoMove`, `Reset()` a inizio di ogni `Search_()`, `Evaluate.StaticEval` ora riceve lo stack in
tutti e 3 i punti di chiamata. Il null-move (`DoNullMove`/`UndoNullMove`) resta **deliberatamente
escluso**: non sposta pezzi, quindi l'accumulatore per entrambe le prospettive resta valido così
com'è (search.cpp:674-679/686 non lo tocca nemmeno nella fonte).

Verificato: 85/85 test; `bench 16 1 8` con `git stash` prima/dopo — **nodi IDENTICI** (507.992,
bestmove `g2g3` in entrambi i casi, correttezza confermata end-to-end) E, per la prima volta in
questa serie di ottimizzazioni, un **guadagno di velocità reale e misurato**: da ~140k a ~198k
nodi/secondo (+40% circa, confermato su più esecuzioni ripetute) — il beneficio concreto che tutto
il lavoro DirtyThreats/DirtyPiece/DirtyPawnPairs/AccumulatorStack di questa sessione doveva
produrre. **Flow B (NNUE) è ora COMPLETO** salvo le due ottimizzazioni rimandate (Finny Tables,
hybrid/backward-update) — ulteriore margine di velocità non ancora sfruttato, non un debito di
correttezza.

---

## Flusso C — Il resto

### C1 — Threading (`thread.h`/`thread.cpp`, 464+ righe) — ✅ FATTO 2026-09-06

Lazy SMP portato in `SearchThreadPool.cs` (nome scelto per non collidere con
`System.Threading.ThreadPool`). Non portata l'infrastruttura NUMA/huge-page/thread nativi C++
(`OptionalThreadToNumaNodeBinder`, `idle_loop` a condition variable, allocazione allineata) — è
gestita direttamente da `System.Threading.Tasks`, non fa parte dell'algoritmo.

**Cosa condividono davvero i thread nella fonte**: solo la transposition table
(`Search::SharedState` la passa per riferimento a ogni `Worker`, thread.h:194-204) — history,
MovePick, `AccumulatorStack` restano sempre privati per thread. La TT (array di `struct`, non
riferimenti) è già sicura per costruzione: letture/scritture concorrenti su elementi diversi non
hanno problemi, e su un elemento condiviso al più producono un mismatch di chiave scartato al
prossimo probe — la stessa tolleranza "quasi lockless" della fonte (`tt.h`/`tt.cpp`).

**Bug reale trovato e corretto durante la verifica**: `tt.new_search()` (search.cpp:191-216) è
chiamato **una sola volta dal thread principale**, PRIMA di avviare gli helper
(`threads.start_searching()` arriva dopo, riga 216) — i thread non principali (righe 196-199)
saltano dritti a `iterative_deepening()` e non lo chiamano mai. Il primo porting di `Search.Search_`
lo chiamava incondizionatamente a ogni chiamata, quindi con N thread del pool ognuno incrementava
concorrentemente `_generation` (un `byte` non atomico condiviso in `TranspositionTable`) — una
corsa che confondeva l'invecchiamento della TT e la inquinava progressivamente con voci non
correttamente scadute. Sintomo osservato: un `bench 16 4 8` (51 posizioni, 4 thread) che rallentava
progressivamente fino quasi a bloccarsi (20/51 in 90s). **Diagnosi sbagliata iniziale**: sospettato
un esaurimento del `ThreadPool` .NET condiviso da troppi `Task.Run` ripetuti — la correzione a
`Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` non ha risolto nulla (stesso identico
sintomo, 21/51 in 90s), il che ha smentito quell'ipotesi. La causa vera è stata trovata rileggendo
`search.cpp` riga per riga. Fix: `Search.Search_` ha ora un parametro `callNewSearch = true` (i
chiamanti a thread singolo restano invariati); `SearchThreadPool.Search_` chiama `_tt.NewSearch()`
una volta sola (equivalente al ruolo di "thread principale" prima di `threads.start_searching()`)
e passa `callNewSearch: false` a TUTTI i worker del pool, main incluso. Con la correzione, lo stesso
bench passa da "quasi bloccato" a 2,8s totali.

**Differenza reale fra main e helper**: NON diversificano la profondità per indice di thread
(tecnica di versioni più vecchie di Stockfish, non presente in questa) — gli helper ignorano
`limits.depth` e continuano fino a `MAX_PLY` finché il tempo non scade o il thread principale
imposta lo stop (search.cpp:333-334), replicato con un `CancellationTokenSource` collegato,
cancellato non appena il thread principale (indice 0) termina.

**Semplificazione deliberata in `get_best_thread`** (thread.cpp:357-408): la fonte vota sulla
`RootMove` completa (pv/inexactLower/inexactUpper) di ogni thread — struttura non ancora presente in
questo porting (RootMoves complete sono anche prerequisito di MultiPV, Flow A4). Usato
`SearchResult.BestMove`/`.ScoreCp` come proxy di `rootMoves[0].pv[0]`/`.score` (la nostra ricerca
completa sempre l'ultima iterazione a finestra piena, quindi "IsInexact" è sempre falso per
costruzione) e `SearchResult.Depth` come proxy della lunghezza del PV per lo spareggio finale. La
formula di voto stessa (punteggio − minimo + 14, preferenza al mate più corto/lungo quando
decisivo) è portata fedele.

**Per-thread `Position`**: ogni worker riceve una propria copia via `Set(Fen(), chess960)` più
`Position.SetRootState(rootPos.State)` — quest'ultima aggiunta perché `Previous`/`PliesFromNull`/
`CapturedPiece` non sono derivabili da una stringa FEN (replica la tecnica di
`ThreadPool::start_thinking`, thread.cpp:332-346, di condividere la vera catena `StateInfo`
storica fra le `Position` clonate per thread).

**Verifica**: bit-esatto a `Threads=1` (invariato: stesso identico bench pre-C1, 507992 nodi);
`dotnet test` 99/99; bench 1/2/4/8 thread — nodi/sec cresce (227k → 337k → 549k → 861k, non
lineare: atteso, Lazy SMP non garantisce scaling lineare nemmeno nella fonte reale).

### C2 — Tablebase Syzygy (`syzygy/`, 2.053 righe) — porting VERO, non un extra — ✅ COMPLETO (TB1-TB10)

Motore di probing WDL/DTZ completo e verificato (2026-09-06), dettaglio completo in
`docs/syzygy-porting-plan.md`. `StockfishSharp.Engine/Tablebases/` — tipi/costanti/tabelle
combinatorie, file/tabelle/registro hash, decompressione Huffman "Recursive Pairing",
calcolo dell'indice di posizione, `ProbeWdl`/`ProbeDtz` pubblici. Verificato con l'oracolo
Stockfish reale su 7 materiali diversi (con/senza pedoni, con/senza pezzo unico) + una
verifica indipendente che segue `ProbeDtz` fino al matto vero. **Bug reale trovato e
corretto** durante la verifica (confrontando con python-chess, installato al volo come terzo
oracolo): un disallineamento di 4 byte nell'arrotondamento a 64 byte del `DataOffset`,
introdotto "spogliando" l'array del magic number invece di tenerlo intero e avanzare il
cursore di 4 come fa la fonte — dettagli in `docs/syzygy-porting-plan.md`. **TB9**
(`root_probe`/`root_probe_wdl`/`rank_root_moves`, ordinamento delle mosse alla radice via
DTZ/WDL) fatto e verificato con `TbRootMove` come sostituto minimo delle vere
`Search::RootMoves` non presenti in questo porting. **TB10** (Step 7 di search.cpp dentro
`Negamax` + le 4 opzioni UCI `SyzygyPath`/`SyzygyProbeDepth`/`Syzygy50MoveRule`/
`SyzygyProbeLimit`) fatto e verificato: bench senza Syzygy configurato invariato (507.992
nodi, nessun effetto quando disattivato), `tbhits` cresce coerentemente col cardinality
configurato. **Flusso C2 completo.** I file di dati fino a 5 pezzi sono già disponibili in
`../ACMyChess/Syzygy/` (vedi [[acmychess-tablebase-plan]]).

### C3 — Utilità (`misc`, `memory`, `score`, `numa`, `tune`, `universal/`, ~1.500 righe)
Portate finora solo le briciole che servivano (`PRNG` dentro `Attacks.cs`).

---

## Flusso D — Pratico, non porting (richiesto dall'utente 2026-09-06)

**Corretto 2026-09-06**: qui va SOLO ciò che la fonte Stockfish reale non ha affatto — non
Syzygy (quello è C2 sopra, porting vero). L'unico caso genuino è il libro di aperture: Stockfish
non ne ha uno, lo gestisce sempre il layer UCI esterno/la GUI/il bot — stesso spirito di
`StockfishSharp.Uci/Program.cs` (layer pratico non fedele) più che di `StockfishSharp.Engine`.

### D1 — Libro di aperture — ✅ FATTO

Adattato da `ACMyChess.Engine/PolyglotBook.cs`+`PolyglotRandom.cs` (stessa logica, riscritta sui
tipi `Position`/`Move`/`MoveGen` di `StockfishSharp.Engine`) — le 781 costanti Zobrist standard del
formato Polyglot sono dati di interoperabilità universali, copiate identiche. La chiave Polyglot è
ricalcolata da zero a ogni `go` (mai incrementale come in ACMyChess: `Position.Key` usa lo schema
Zobrist interno di Stockfish, incompatibile col formato .bin, e il libro si consulta solo nelle
primissime mosse — il costo è trascurabile). Più semplice della controparte ACMyChess su due punti:
`Square` in questo porting (A1=0..H8=63) è già la convenzione richiesta da Polyglot, niente flip di
riga; l'arrocco è codificato allo stesso modo in entrambi (`Move.ToSq` = casa della torre), niente
reinterpretazione speciale.

File `performance.bin` (asset di terze parti, gitignored) copiato da ACMyChess in
`StockfishSharp.Uci/Assets/Book/`, `CopyToOutputDirectory` nel `.csproj`. Wiring in `Program.cs`:
caricato all'avvio, consultato in `HandleGo` prima della ricerca vera se `GamePly < 24` — se
copre la posizione risponde subito con `bestmove`, altrimenti la ricerca prosegue normalmente
(stessa soglia e stesso comportamento di ACMyChess.Uci).

Verificato a mano via UCI: risposta immediata (nessun `info depth`, quindi confermato che non ha
cercato) su più posizioni in sequenza (`e2e4` dalla partenza, `c7c5` dopo 1.e4, `a1b2` — la
ricerca vera, corretta — su un finale K vs k fuori libro, `a7a6` dopo una Ruy Lopez). 85/85 test
(nessuno tocca `StockfishSharp.Uci`, il progetto compila pulito). **Flow D COMPLETO.**

---

## L'oracolo: cosa garantisce e cosa no

`stockfish-reference-binary/.../stockfish-windows-x86-64-universal.exe`, **Stockfish 19**
ufficiale. Interrogato con `compiler` su questa macchina risponde:

```
Compilation architecture : x86-64-avx512icl
Compilation settings     : 64bit AVX512ICL VNNI AVX512 BMI2 AVX2 SSE41 SSSE3 SSE2 POPCNT
```

**Conseguenza importante**: il binario "universal" rileva la CPU e sceglie il percorso migliore —
su questa macchina esegue i rami **AVX512ICL**, cioè un terzo percorso, diverso sia dallo scalare
che porteremo per primo sia dall'AVX2 che porteremo dopo.

Stockfish progetta tutti i percorsi ISA per dare risultati **identici** (le reti devono valutare
uguale su qualunque macchina, altrimenti il loro framework di test non funzionerebbe). La
permutazione dei pesi esiste proprio per questo, e ha tre ordini diversi:

| Percorso | `PackusEpi16Order` |
|---|---|
| scalare | `{0,1,2,3,4,5,6,7}` (identità) |
| AVX2 | `{0,2,1,3,4,6,5,7}` |
| AVX512 | `{0,2,4,6,1,3,5,7}` |

⚠️ **Assunzione da tenere presente, non dimostrata**: che scalare e AVX512ICL diano lo stesso
identico intero. Se durante N3/N5/N6 il mio scalare dovesse differire dall'oracolo di ±1 unità in
alcune posizioni, la **prima ipotesi da verificare non è "il mio codice è rotto"** ma "divergenza
scalare/SIMD dentro Stockfish". Come si distingue: si completa N8 e si confronta; se il percorso
SIMD combacia con l'oracolo e lo scalare no, la divergenza è nella fonte ed è un risultato
interessante da documentare, non un bug nostro.

### Bersaglio SIMD: AVX512ICL (deciso dopo verifica empirica)

Il bersaglio SIMD **non è AVX2 ma AVX512ICL**, lo stesso percorso che esegue l'oracolo. Vantaggio
non secondario: il confronto con l'oracolo diventa diretto, senza l'ambiguità
scalare-contro-SIMD descritta sopra.

Quali sottoinsiemi .NET 10 espone davvero, verificato eseguendo un programma di prova su questa
macchina (non dedotto dalla documentazione):

| Sottoinsieme | Esposto da .NET 10 | Supportato dalla CPU |
|---|---|---|
| `Avx2` | sì | sì |
| `Avx512F` / `BW` / `DQ` / `CD` | sì | sì |
| `Avx512Vbmi` | sì | sì |
| **`Avx512Vbmi2`** | **sì** | **sì** |
| `Gfni` | sì | sì |
| `AvxVnni` | sì | **no** (la CPU non ce l'ha) |
| **`Avx512Vnni`** | **il tipo non esiste in .NET 10** | — |
| `Avx512Bitalg` | il tipo non esiste in .NET 10 | — |

Due conseguenze operative:

1. ✅ **`Avx512Vbmi2` c'è**: è quello che serve per `VPCOMPRESSB`
   (`_mm512_maskz_compress_epi8`), l'istruzione su cui poggiano i rami `USE_AVX512ICL` della
   generazione indici (`half_ka_v2_hm::write_indices`, `pp_3wide`) e delle mosse
   (`movegen.cpp::splat_*`). Era il rischio tecnico principale ed è rientrato.
2. ⚠️ **`Avx512Vnni` non è raggiungibile da C#**: niente `VPDPBUSD` per i prodotti scalari int8
   dei layer. Non è un blocco — Stockfish ha già nel suo `simd.h` la variante **senza VNNI**
   (`maddubs_epi16` + `madd_epi16` + somma), che è un suo percorso legittimo. Portiamo quella.

Il bersaglio effettivo è quindi **AVX512ICL meno VNNI**.

⚠️ **Assunzione aggiuntiva da questa scelta**: l'oracolo usa VNNI, il nostro porting no. Le due
sequenze sono matematicamente equivalenti, ma `maddubs_epi16` satura a int16 mentre `VPDPBUSD`
accumula in int32 senza quella saturazione intermedia. Nel primo layer i valori sono vincolati in
modo che la saturazione non possa avvenire (per questo Stockfish si permette entrambe le
varianti), ma se emergesse una divergenza nei layer, **questa è la prima cosa da controllare**.

**Nota post-N8**: questo ragionamento su VNNI/maddubs era la pianificazione prima di scrivere
codice. In pratica N8 (`docs/nnue-porting-plan.md`) ha preso una strada diversa e più semplice per
`AffineTransform`: niente `maddubs_epi16` né `VPDPBUSD`, si allarga tutto a int32 con
`Vector512.Widen` prima di moltiplicare — l'assunzione sulla saturazione qui sopra non si applica
al nostro porting (non ha accumulo intermedio a i16), resta rilevante solo per capire l'oracolo.

---

## Ordine consigliato dei flussi

1. **B (NNUE) fino a N7** — è la cosa che cambia di più il motore: oggi la valutazione è un
   segnaposto materiale+PSQT, con NNUE diventa un motore vero. Ha un piano dettagliato e una
   verifica solida (l'oracolo a due colonne).
2. **N8 (AVX512ICL meno VNNI)** — senza, NNUE sarà corretto ma lento.
3. **A1 (ricerca)** — il pezzo singolo più grande del progetto, ma anche quello dove il divario
   fra il mio segnaposto e la fonte vera è più grande in forza di gioco.
4. **A2 (move ordering)** — strettamente legato ad A1, conviene farli vicini.
5. **A5** (`DirtyThreats`) → **N9** (accumulatore incrementale) → **C1** (Lazy SMP): sono in
   catena, in quest'ordine — ✅ FATTA tutta la catena.
6. **A3, A4** (tempo, UCI) — meno urgenti: le versioni attuali funzionano, il divario è in
   completezza di funzioni, non in forza.
7. **C2** (Syzygy, porting vero di `tbprobe.cpp`), **C3** (utilità) — alla fine.
8. **D1** (libro di aperture) — ✅ FATTO, ultimo pezzo richiesto esplicitamente dall'utente come
   traguardo finale (non porting: Stockfish non ne ha uno).

## Come si misura la fine

Il criterio di completamento del progetto non è "tutti i file portati", ma:
**a parità di posizione, profondità fissa e opzioni, StockfishSharp e l'eseguibile ufficiale
scelgono la stessa mossa e visitano lo stesso numero di nodi.** È il test che Stockfish stesso
usa fra build diverse (`bench`), ed è l'unico che dimostra che il porting è fedele davvero.

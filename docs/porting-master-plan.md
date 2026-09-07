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

**Manca ancora**: tutta la taratura fine dei margini rimasti, la struttura `Worker`/`Stack`
completa della fonte (qui minimizzata a quanto serve) — `RootMove`/`RootMoves` sono invece ORA
PORTATE con fedeltà, vedi la sezione "RootMove/RootMoves" più sotto: l'aspiration window usa la
vera media mobile pesata per "effort", non più lo score grezzo dell'iterazione precedente.
`followPV` (segue la riga principale dell'iterazione precedente) non è portato — la condizione di
IIR e dello Step 15 qui è quindi leggermente più ampia di quella esatta della fonte.

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

**Infrastruttura opzioni generica FATTA (2026-09-07)**: `StockfishSharp.Uci/OptionsMap.cs`
(scritto in una sessione precedente, mai collegato — completato ora) porta fedelmente
`class Option`/`class OptionsMap` (ucioption.h:39-103 + ucioption.cpp:41-212): tipo/min/max/
on_change, ordine di stampa per indice di inserimento (`Idx`, non l'ordine alfabetico del
dizionario). `Program.cs` registra ora le 18 opzioni reali di `Engine::Engine`
(engine.cpp:69-139) nello stesso ordine di inserimento — verificato byte per byte contro
l'oracolo (`uci` produce lo stesso identico elenco, comprese le righe senza `default` per i
bottoni). `HandleSetOption` sostituisce la catena if/else ad-hoc con l'algoritmo esatto di
`OptionsMap::setoption` (nome multi-parola letto token per token fino a "value", "value" mai
verificato letteralmente, valore opzionale per i bottoni) — corretto anche un buco pre-esistente:
prima "setoption name Clear Hash" (nessun token "value") veniva scartato silenziosamente perché
l'handler richiedeva sempre un indice "value" trovato.

Wired con on_change reale (comportamento verificato, non solo dichiarato): `Threads`, `Hash`,
`Clear Hash` (→ `Search.NewGame()`, che fonde già `tt.clear(threads)`+`threads.clear()` della
fonte — vedi il commento lì), `Move Overhead` (NUOVO: prima hardcoded a 10 in `TimeManagement.Init`,
mai letto da UCI — ora `moveOverhead` è una variabile aggiornata dall'opzione e passata davvero),
`UCI_Chess960`, tutte e 4 le opzioni Syzygy, `EvalFile` (NUOVO: prima la rete NNUE si caricava solo
all'avvio da un percorso hardcoded — ora ricaricabile a runtime, con messaggio di errore se il
file non esiste invece di un crash).

**Dichiarate per completezza di protocollo ma senza on_change (nessun effetto sul motore)**:
`Debug Log File`/`NumaPolicy` (nessun analogo utile qui — non NUMA, non un logger dedicato),
`Ponder` (nessun gestore `go ponder`/`ponderhit`, vedi nota sotto), `MultiPV` (resta a `_pvIdx=0`),
`Skill Level`/`UCI_LimitStrength`/`UCI_Elo` (nessuna classe `Skill` portata), `UCI_ShowWDL`
(nessuna conversione punteggio→WDL), `nodestime` (deliberatamente non portato, nota già in
`TimeManagement.cs`). Ognuna commentata in `Program.cs` col motivo esatto, per non essere mai
scambiata per "fatta" in futuro.

Verificato: 109/109 test; `uci` confrontato byte per byte con l'oracolo (stesso ordine, stessi
default/min/max, comprese le righe `type button` senza `default`); `setoption` a mano su bottone
(`Clear Hash` senza value), spin (`Hash`/`Move Overhead`), check (`UCI_Chess960`), un nome con
spazio inesistente (`No such option: Nonexistent Option`, formato esatto della fonte); `bench 16 1
6` completo comprese le 2 posizioni Chess960 (il toggle `UCI_Chess960` dentro `HandleBench` passa
ora per lo stesso `HandleSetOption`).

**Pondering vero FATTO (2026-09-07)**, richiesto esplicitamente dall'utente dopo la nota sotto:
`SearchManager::ponder`/`stopOnPonderhit` (search.h:307,313) ora reali. `Search.cs`: nuovo campo
`_stopOnPonderhit` (volatile, esposto da `StopOnPonderhit`) + due nuovi parametri di `Search_`
(`isPondering`, `maximumMsOverride`) — il blocco di gestione tempo adattiva (search.cpp:568-614)
ora replica esattamente la fonte: mentre si pondera, un superamento del tempo stimato non ferma
la ricerca (`break`) ma imposta `_stopOnPonderhit=true` e forza `increaseDepth=true` (si continua
a scavare, mai un no-op); un fail-low lo azzera di nuovo (search.cpp:427, prima mancante).
`maximumMsOverride` disaccoppia il vero `tm.maximum()` dal parametro `timeLimit` (che durante il
pondering diventa un tetto fittizio enorme per disattivare il `CancelAfter` interno — il vero
`check_time`, search.cpp:2122-2129, "if (ponder) return" prima di ogni controllo, sospende
INTERAMENTE anche il tetto assoluto finché si pondera).

`Program.cs`: `HandleGo` rileva il token "ponder" súbito (prima del libro, perché governa se
aspettare "ponderhit"/"stop" prima di annunciare bestmove qualunque sia la fonte della mossa) e
passa un `PonderFlag` (wrapper con campo `volatile`, C# non ha variabili locali volatili) come
`isPondering` a `Search_`. `HandlePonderhit` (nuovo case "ponderhit") replica il cuore di
`check_time` DALL'ESTERNO invece che con un check periodico interno: se `search.StopOnPonderhit`
è già vero, cancella subito (`searchCts.Cancel()`, effettivo entro i successivi ~2048 nodi, la
stessa granularità del controllo periodico già esistente); altrimenti riarma il vero tetto
massimo misurato dallo stesso istante in cui è iniziato il pondering
(`searchCts.CancelAfter(currentMaximumMs - goStopwatch.Elapsed)`, un `Stopwatch` dedicato avviato
in `HandleGo`) — il tempo già speso pondering NON è gratuito, si somma al budget della mossa
reale (il punto stesso del pondering: se si è già pensato abbastanza, la mossa reale costa
pochissimo tempo aggiuntivo). Il ciclo iterativo usa `Ply.MaxPly-1` come tetto di profondità
mentre si pondera (non il solito `maxDepth=30`), per non fermarsi a un muro arbitrario durante
una sessione di pondering lunga.

**Anche portato, applicabile a OGNI bestmove non solo al pondering**: `UCIEngine::on_bestmove`
(uci.cpp:690-694) — `bestmove X ponder Y` con la seconda mossa della PV, o
`RootMove::extract_ponder_from_tt` (search.cpp:2350-2366, nuovo `ExtractPonderFromTt` in
Program.cs — gioca il bestmove su una copia, sonda la TT condivisa via il nuovo
`SearchThreadPool.ProbeTT`, verifica la legalità) come ripiego quando la PV aveva una sola mossa.
Il busy-wait di `search()` dopo `iterative_deepening()` (search.cpp:217-229, per il caso raro
"profondità/limite raggiunto MENTRE si pondera ancora") è un `WaitWhilePondering` con poll da 1ms
invece del vero busy-spin della fonte — applicato anche al ramo libro (Flow D1, non fonte: gira
ora in background come il ramo di ricerca vera, altrimenti bloccherebbe il ciclo comandi che deve
poter ricevere "ponderhit" nel frattempo).

**Semplificazione dichiarata**: "go ponder movetime N"/"go ponder infinite" (combinazioni rare,
nessun bot/GUI reale le usa in pratica insieme al pondering) non hanno il tetto adattivo esteso
della fonte — il pondering lì si riduce a "aspetta ponderhit/stop prima di annunciare bestmove"
senza sospendere il cap di tempo fisso già esistente. Solo il ramo wtime/btime (l'unico dove
`use_time_management()` è vero anche nella fonte) ha la fedeltà completa.

Verificato: 109/109 test; bench 1 thread — nodi IDENTICI (1.235.311, bit-esatto, il percorso non
pondering è invariato). Verifica dal vivo via UCI (script Python con lettura non bloccante):
`go ponder` su una mossa di libro non annuncia bestmove finché non arriva "ponderhit" (poi
istantaneo, <20ms); `go ponder wtime/btime` su una posizione di mediogioco fuori libro, ponderhit
dopo 3s di pondering — la ricerca continua col vero tetto residuo (23649ms calcolati, 20925ms
misurati dal ponderhit contro 20859ms attesi, scarto entro il rumore della granularità di
controllo) invece di fermarsi o ripartire da zero; "stop" durante il pondering interrompe entro
30ms. **Nota dell'utente (2026-09-07): il pondering va tenuto SPENTO nel bot per ora** (il motore
lo supporta correttamente se un client lo richiede, ma la decisione di abilitarlo sul bot reale
resta rimandata — probabilmente in `lichess-bot`'s config, fuori da questo repo).

### Indagine sul tempo per mossa (2026-09-07) e tetto di sicurezza per-iterazione — PRATICO, non fonte

Richiesta esplicita dell'utente dopo la nota sul pondering: "impieghiamo troppo tempo per fare una
mossa a prescindere da quelle del libro" — riferito alla sconfitta reale a tempo scaduto
(`zEJZDl6m`, 2026-09-06 sera, già nella cronologia sopra). Riprodotta offline la posizione esatta
di una delle mosse critiche (`4rr2/pp1q1ppk/2np3p/b1pn3b/2P1PP2/1P1P4/PBN2QBP/R4R1K w - - 0 19`,
wtime/btime dal log reale) con una strumentazione temporanea per-iterazione.

**Causa isolata**: il controllo del tempo (fedele alla fonte) decide se continuare solo TRA
un'iterazione completa e la successiva, mai a metà. Su questa posizione, la profondità 16 è
arrivata a 6,97s (budget stimato ~17,3s — via libera a continuare), ma la profondità 17 **da
sola** ha impiegato altri 20,1s, mentre il budget stimato nel frattempo era rimasto stabile
(~16,3s, instabilità già decaduta a 1,077): non un rigonfiamento della formula d'instabilità, ma
un'iterazione genuinamente più costosa del previsto, che nessun meccanismo esistente può
interrompere a metà (il tetto assoluto duro è molto più alto e non era stato raggiunto).

**Confronto diretto con l'oracolo sulla stessa posizione (stessa rete NNUE)**: Stockfish 19 reale
arriva a depth 20 in 645ms con 743.777 nodi; il nostro motore (thread principale, confrontabile
1v1 con l'oracolo a 1 thread) ha impiegato ~2,3M nodi per arrivare solo a depth 17 — **~20 volte
più nodi per una profondità inferiore**, e il rapporto CRESCE con la profondità (a depth 8 è solo
~1,8x) invece di restare costante: non un costo fisso per nodo, un effetto di potatura/estensioni
meno efficaci che si amplifica ply dopo ply. Trovato un contributo concreto ma parziale: `history.h`'s
`ttMoveHistory` (D=8192) esiste già nel porting ma non è mai consultata in nessuna formula — nella
fonte compare nel margine di estensione doppia/tripla delle Singular Extensions (search.cpp:1261)
e nel multi-cut pruning (search.cpp:1279) — un termine scalare mancante in due formule, non
sufficiente da solo a spiegare un gap di 20x. La causa di fondo resta quella già scritta più volte
in questo documento: messa a punto fine diffusa, non un pezzo isolato — la stessa conclusione di
sempre, ora con un numero misurato invece che solo argomentata.

**Mitigazione applicata (dichiaratamente PRATICA, non fonte — Stockfish reale non ne ha bisogno
perché non incontra quasi mai questo caso)**: nuovo `Search.IterationCostSafetyMultiplier` (2.5) +
`previousIterationElapsedMs` in `Search_` — se l'iterazione APPENA CONCLUSA ha consumato da sola
più di 2,5 volte il budget stimato di quel momento, la deepening si ferma del tutto (niente
successiva iterazione), invece del solo freno più morbido già esistente
(`increaseDepth=false`/`searchAgainCounter`, che riduce la profondità EFFETTIVA della prossima
iterazione ma non impedisce di tentarla). Non si applica mentre si sta pondering (search.cpp:613
forza sempre `increaseDepth=true` lì, il tempo "extra" non è mai davvero a rischio).

Verificato: 109/109 test; `bench 16 1 10` — nodi IDENTICI (1.235.311, bit-esatto: il tetto si
applica solo alle ricerche a gestione tempo reale, mai a `bench`/`go depth N` a profondità fissa,
dove `optimumMs` resta `NoBound`). Sulla posizione riprodotta il comportamento fra esecuzioni
successive resta variabile (già osservato: Lazy SMP con 8 thread introduce non-determinismo reale
nell'accumulo di `bestMoveInstability` fra un'esecuzione e l'altra sulla STESSA posizione) — non
un test bit-esatto praticabile per questo caso specifico, la correttezza della logica è verificata
dai test dedicati e dal ragionamento sulla formula, non da un confronto diretto nodi/mosse come
per le tecniche di fonte.

**Nota storica per cui il pondering era stato richiesto (osservazione dal vivo, 2026-09-06)**: in
una partita reale del bot, l'avversario (bot Lichess) rispondeva quasi istantaneamente a ogni
mossa pur avendo un orologio che CRESCEVA rispetto al nostro (lui oltre 11 minuti, noi circa 2) —
comportamento coerente con un pondering reale attivo dall'altra parte.

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
DTZ/WDL) fatto e verificato — inizialmente con `TbRootMove` come sostituto minimo delle vere
`Search::RootMoves`, **ora (2026-09-06) wired sulle vere `RootMove.TbRank`/`.TbScore`** (vedi
nota sotto). **TB10** (Step 7 di search.cpp dentro `Negamax` + le 4 opzioni UCI
`SyzygyPath`/`SyzygyProbeDepth`/`Syzygy50MoveRule`/`SyzygyProbeLimit`) fatto e verificato: bench
senza Syzygy configurato invariato, `tbhits` cresce coerentemente col cardinality configurato.
**Flusso C2 completo.** I file di dati fino a 5 pezzi sono già disponibili in
`../ACMyChess/Syzygy/` (vedi [[acmychess-tablebase-plan]]).

**TB9 wired sulle vere RootMoves (2026-09-06)**: `Tablebase.RootProbe`/`RootProbeWdl`/
`RankRootMoves` operano ora su `List<RootMove>` (leggono/scrivono `m.Pv[0]`/`.TbRank`/`.TbScore`
direttamente sulla struttura reale) invece del sostituto `TbRootMove` (rimosso da `TbTypes.cs`).
`Search_` chiama `Tablebase.RankRootMoves` subito dopo aver popolato `_rootMoves` da tutte le
mosse legali, PRIMA del controllo "nessuna mossa legale" — stesso ordine di
`ThreadPool::start_thinking` (thread.cpp:323), che chiama `rank_root_moves` prima che
`start_searching` controlli `rootMoves.empty()`. Il `TbConfig` risultante (compresa
`Cardinality`, eventualmente azzerata quando DTZ ha già risolto la radice — "Probe during
search only if DTZ is not available and we are winning") diventa la config usata dal probing
"in-tree" di TB10: le due fasi ora condividono la stessa struttura, come nella fonte.

Aggiunto anche il filtro radice per gruppo di `tbRank` (search.cpp:1131-1135, `pvFirst`/`pvLast`,
nuovi campi `Search._pvFirst`/`_pvLast` ricalcolati una volta per profondità): quando la radice è
in tablebase, il ciclo mosse di `Negamax(ply=0)` ora SALTA le mosse di rango inferiore invece di
cercarle comunque — prima mancava, quindi TB9 ordinava le mosse ma la ricerca le esplorava tutte
allo stesso modo. Senza tablebase attiva (il caso comune) `tbRank` è uniforme per tutte le mosse
e il filtro non esclude mai nulla — verificato bit-esatto: bench senza Syzygy configurato,
nessuna differenza nel numero di nodi rispetto a prima di questo wiring.

Le opzioni UCI Syzygy (Program.cs) non calcolano più a mano una `Cardinality` approssimata
(`syzygyPath vuoto ? 0 : probeLimit`, un caso speciale usato PRIMA che `RankRootMoves` fosse
wired): ora passano solo i tre valori grezzi (`SetSyzygyOptions`, propagato da
`SearchThreadPool` a ogni `Search`), e `RankRootMoves` li combina da sé con
`Tablebase.MaxCardinality` (0 finché `Tablebase.Init` non ha caricato tabelle) esattamente come
fa la fonte — il caso "nessun path configurato" ora si risolve da solo, senza bisogno di un `if`
esplicito nel layer UCI.

Verificato dal vivo via UCI: KQvK (`4k3/8/4K3/8/8/8/8/4Q3 w`, la stessa posizione del test TB9)
sceglie `e1h4` con PV `e1h4 e8f8 h4h8` (matto in 2, stessa mossa dell'oracolo reale) e `tbhits=0`
(DTZ già disponibile e vincente, probing in-tree correttamente disattivato); bench 1 e 4 thread
senza eccezioni.

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

### D2 — Riscaldamento JIT all'avvio — ✅ FATTO 2026-09-07

**Indagine sul "bug della gestione del tempo"** (richiesta esplicitamente dall'utente dopo il
commit di stamattina su `searchAgainCounter`/`adjustedDepth`, che aveva lasciato aperto il dubbio
di non risolvere il caso peggiore osservato dal vivo): verificato con un test misurato (UCI reale,
`go wtime 60000 btime 60000 winc 1000` su una posizione di mediogioco) che **non esiste un vero
overrun algoritmico** — `TimeManagement.Init` combacia riga per riga con `timeman.cpp`
(verificato di nuovo qui), e il meccanismo di stop mid-ricerca (`cts.CancelAfter(timeLimit)` +
`_ct.ThrowIfCancellationRequested()` ogni 2048 nodi in `Negamax`, search.cpp:778-779/2103-2129:
`check_time` è chiamato SOLO da `search()`, mai da `qsearch()`, nella fonte — verificato, non è un
buco di fedeltà) rispetta `MaximumTime` con un margine di pochi millisecondi, esattamente come
previsto. Il caso "una mossa usa quasi tutto `MaximumTime`" resta comportamento CORRETTO e atteso
dell'algoritmo reale quando l'instabilità misurata (`bestMoveInstability`) è genuinamente alta — non
un bug.

**Trovato invece un problema pratico reale, mai proprio della fonte C++ (nessuna compilazione a
runtime lì)**: il primissimo `go` del processo paga, FUORI dallo `Stopwatch` interno di `Search_`,
il costo del tiered JIT di .NET che compila i metodi hot-path (`Negamax`/`Quiesce`/`MovePicker`)
mai eseguiti prima — misurato ~1.1-1.7s in più rispetto a `MaximumTime` sulla primissima ricerca di
un processo appena avviato (verificato isolando la causa: una `go movetime 500` di riscaldamento
prima della `go wtime/btime` reale nello stesso processo elimina quasi del tutto lo scarto). Un
rischio concreto di sforare il tempo assegnato dalla GUI/dal bot sulla prima mossa di ogni partita
(o di ogni riavvio di processo).

**Fix**: nuovo blocco in `Program.cs`, un `Task.Run` in background avviato subito dopo il caricamento
di NNUE/libro (non bloccante, non risponde a "uci"/"isready" più lentamente) che esegue una ricerca
di riscaldamento (`depth 10`, budget 800ms) su una `SearchThreadPool`/`Position` DEDICATE, mai
condivise con quelle della partita reale — niente stato residuo (history, TT) quando la partita
vera comincia. Per costruzione questo copre solo la primissima ricerca del processo: le mosse
successive nella stessa partita (stesso processo) non pagano più questo costo, essendo già JIT-ate.

Verificato: 109/109 test; misurato via UCI (stessa posizione/orologio di prima, con ~3s di attesa fra
l'avvio del processo e il primo `go` — il tempo reale di handshake di una GUI/di lichess-bot)
l'overrun scende da ~1.14s (4.8% di `MaximumTime`) a ~0.3s (1.3%) sulla primissima ricerca del
processo.

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

## RootMove/RootMoves (search.h:135-168) — ✅ FATTO 2026-09-06

Portate con fedeltà in `StockfishSharp.Engine/RootMove.cs` + il ciclo radice di `Negamax`
(`Search.cs`), sostituendo il campo interinale `_rootBestMove` (introdotto il giorno prima come
fix minimo del bug dell'arrocco/bestmove illegale sul bot dal vivo, [[stockfishsharp-porting-project]])
con la vera struttura della fonte. Motivazione: `_rootBestMove` risolveva solo "qual è la mossa
migliore", non l'intera famiglia di problemi che dipendono da `RootMoves` (PV multi-mossa,
optimism, aspiration window pesata per effort, base per MultiPV/tablebase-root-ranking futuri) —
esattamente il tipo di "impalcatura pratica mai riconciliata con la fonte" che la policy
[[feedback-stockfishsharp-no-practical-code]] vieta di lasciare in giro.

**Cosa è cambiato**:
- Una `RootMove` per ogni mossa legale della posizione radice, ricreata a ogni `Search_`
  (`ThreadPool::start_thinking`, thread.cpp:309-321, senza il filtro "searchmoves").
- Il "TT move" usato da TUTTO il nodo radice (ordinamento di MovePicker, `ttCapture`, IIR, sconti
  di riduzione, Singular Extensions, `UpdateStats`) è ora sempre `rootMoves[pvIdx].pv[0]`
  (search.cpp:820), non più una ri-sonda diretta della TT — **questo ha corretto due bug di
  fedeltà scoperti durante il porting, non solo aggiunto la struttura**:
  1. **IIR poteva attivarsi alla radice** quando la TT reale non aveva ancora un'entry utile,
     anche se la mossa radice migliore nota era perfettamente valida — la fonte non lo fa mai
     (ttData.move alla radice non è mai vuoto). Ora impossibile per costruzione.
  2. **Step 9 (futility pruning) usava una condizione più larga del dovuto**
     (`!probe.Found || probe.Data.Move==None || ttCapture` invece del semplice `!ttData.move ||
     ttCapture` della fonte) — semplificata e resa esatta insieme all'introduzione di `ttMove`.
- **Step 20 (search.cpp:1414-1420) aggiunto**: estensione a profondità 1 quando si sta per tuffarsi
  in quiescenza con la stessa mossa già vista in TT a una profondità utile — mancava del tutto
  prima d'oggi, scoperta durante l'audit riga-per-riga per introdurre `ttMove`.
- **TT write durante Singular Extensions corretta**: la fonte non scrive MAI in TT quando
  `excludedMove` è impostato (search.cpp:1621, bound calcolato escludendo una mossa non è
  rappresentativo) — il porting lo faceva incondizionatamente. Bug pre-esistente, scoperto e
  corretto nello stesso passaggio (stessa riga della fonte che introduce il gate `pvIdx`).
- PV multi-mossa reale (`ss->pv`/`PVMoves::update`, search.h:95-104) tramite un buffer per-ply
  riutilizzato (`_pvBuf`, stesso pattern di riuso di `_currentMoveHistory` ecc.) — prima
  `SearchResult` esponeva solo il bestmove singolo, ora `result.Pv` è la riga intera e
  `info depth ...` la stampa (`pv m1 m2 m3 ...`), più `seldepth`.
- Aspiration window e `optimism` (search.cpp:376-383,1901-1904) ora derivati dalla vera media
  mobile pesata per "effort" di `rootMoves[0]` (formula esponenziale, search.cpp:1446-1468), non
  più dal punteggio grezzo dell'iterazione precedente — `Evaluate.StaticEval` ha un nuovo parametro
  `optimism` per questo, passato ai 3 punti che chiamano la valutazione statica dentro `Negamax`.
- `seekMate` (Step 9/16) legge ora `rootMoves[pvIdx].score` come nella fonte, non più
  un'approssimazione dall'ultima iterazione completata.
- Guardia aggiunta per nessuna mossa legale alla radice (`start_searching`, search.cpp:207-213):
  prima avrebbe fatto un `IndexOutOfRange` su `rootMoves[0].pv[0]`, ora ritorna subito
  (`bestmove 0000`, punteggio di matto/patta) — trovato scrivendo il codice, poi confermato dal
  bench reale (2 delle 51 posizioni di default sono matto/stallo).

**Deliberatamente non fatto in questo passaggio** (dichiarato esplicitamente, non dimenticato): il
ciclo MultiPV (search.cpp:360-503, qui `multiPV` resta fissato a 1 — `_pvIdx` è sempre 0), Skill
Level, il filtro "searchmoves". `Tablebases::rank_root_moves` (TB9), `followPV`, il vero ProbCut
via `MovePicker` e la gestione tempo adattiva reale (che consuma `bestMoveChanges`/
`totBestMoveChanges`, lasciati "accumulati ma non consumati" nella nota sopra) sono stati invece
tutti fatti subito dopo, stesso giorno — vedi le note dedicate qui sotto e nel Flusso C2.

### followPV, ProbCut via MovePicker e gestione tempo adattiva reale — ✅ FATTI 2026-09-06

Tre pezzi distinti, tutti richiesti esplicitamente dall'utente nella stessa sessione dopo la
domanda "cosa resta fuori, tutto il resto è un porting fedele?":

- **`followPV`** (search.cpp:772-775): un nuovo array per-ply `_followPvHistory` traccia se un
  nodo è ancora sulla riga principale dell'iterazione PRECEDENTE (`RootMove.PreviousPv`,
  snapshottata una volta per profondità in `_lastIterationIdxPv`, search.cpp:370). Usato per
  disattivare l'Internal Iterative Reduction (Step 11) e la potatura delle mosse quiete a
  profondità bassa (Step 15) quando si sta seguendo quella riga in un nodo PV
  (`else if (!ss->followPV || !PvNode)`, search.cpp:1197) — prima quella potatura si applicava
  SEMPRE alle mosse quiete, una differenza reale dalla fonte, non solo "leggermente più ampia".
- **ProbCut vero via `MovePicker`** (Step 12): il secondo costruttore di `MovePicker`
  (movepick.cpp:181-189, stage `ProbcutTt`/`ProbcutInit`/`Probcut`) era già stato scritto
  fedelmente durante il porting di `MovePicker.cs` (generazione a stadi vera) ma MAI usato dal
  punto di chiamata in `Negamax` — Step 12 generava le catture a mano con
  `MoveGen.Generate(GenType.Captures,...)` e le provava nell'ordine di generazione grezzo invece
  di quello per MVV+capture history della fonte. Corretto: ora usa `new MovePicker(pos, hist,
  ttMove, probCutBeta-staticEval, ...)`, con l'aggiunta del controllo `pcMove == excludedMove`
  (search.cpp:1069) che mancava anche nella versione manuale.
- **Gestione tempo adattiva reale** (search.cpp:568-618): `Search_` accetta ora un parametro
  `optimumMs` (default `Search.NoBound`, equivalente di `!limits.use_time_management()`,
  search.h:182 — vero solo quando la GUI fornisce `wtime`/`btime` reali). Quando fornito, dopo
  ogni iterazione completata la ricerca calcola `fallingEval`/`timeReduction`/
  `bestMoveInstability`/`highBestMoveEffort` (usando `RootMove.Effort`, ora disponibile per
  davvero) e può fermarsi PRIMA del tetto massimo se la mossa migliore è stabile, o continuare se
  instabile — `timeLimit` (il parametro esistente) diventa il tetto ASSOLUTO
  (`TimeManagement.MaximumTime`), mai superato. Nuovi campi persistenti su `Search`
  (`bestPreviousScore`/`bestPreviousAverageScore`/`previousTimeReduction`, azzerati da `NewGame`
  come `ThreadPool::clear()`, thread.cpp:272-278) e un nuovo `Search.SetPreviousScores` che
  `SearchThreadPool` chiama sul thread principale dopo aver scelto il "bestThread" del pool
  (search.cpp:247-248: la fonte aggiorna sempre `main_manager()` con i valori del VINCITORE, anche
  se diverso dal thread principale). Program.cs passa `MaximumTime` come `timeLimit` e
  `OptimumTime` come `optimumMs` solo nel ramo `wtime`/`btime` — `movetime`/`depth`/`infinite`
  restano un budget fisso, senza gestione adattiva, come nella fonte.

  **Semplificazione dichiarata**: `totBestMoveChanges` nella fonte è la somma di
  `bestMoveChanges` di TUTTI i thread del pool divisa per il loro numero (search.cpp:562-566,586)
  — qui usa solo il valore del thread CHE STA ESEGUENDO la gestione tempo (sempre e solo il
  principale, gli helper non la consultano mai), senza sincronizzazione cross-thread: con thread
  indipendenti sulla stessa posizione, il valore di un singolo thread è già un proxy ragionevole
  della media, e la sincronizzazione aggiungerebbe complessità per un guadagno di fedeltà marginale
  (il numero non sarebbe comunque bit-esatto rispetto alla fonte, che ha timing di thread reali
  diversi). `ponder`/`stopOnPonderhit` non portati (nessun supporto ponder in Program.cs): il ramo
  "ferma subito" è sempre quello percorso.

**Verificato**: 109/109 test (nessuna regressione), bench 1 e 4 thread senza eccezioni (il numero
di nodi CAMBIA rispetto a prima di questo passaggio — atteso, followPV e il vero ProbCut alterano
davvero l'albero esplorato, non sono no-op come TB9 nel caso comune). Verifica dal vivo: un matto
in 1 con `wtime 60000` si ferma a depth 1 (score decisivo supera la soglia `mate_in(3)`, invece di
continuare a scavare su una posizione già risolta); una posizione di mediogioco non decisiva con
lo stesso orologio usa solo una piccola frazione dei 60s disponibili (coerente con `OptimumTime`
calcolato da `TimeManagement`, non con `MaximumTime`); bench multi-thread con `SetPreviousScores`
attivo, nessuna eccezione.

**Verifica**: `dotnet test` 109/109 (nessuna regressione); bench 1 thread e 4 thread (Lazy SMP) su
tutte le 51 posizioni di default, nessuna eccezione, PV plausibili su ogni posizione incluse le 2
di matto/stallo; verifica manuale UCI su posizioni fuori libro (matto in 1 con torre, endgame con
promozione, mediogioco con arrocco già avvenuto) — bestmove e PV legali e coerenti col materiale/
tattica della posizione.

### Fix: `bestMoveChanges` self-azzerato da ogni thread invece che mediato dal principale — ✅ 2026-09-06

**Bug reale trovato dal vivo lo stesso giorno**, subito dopo il deploy della gestione tempo
adattiva sopra: il thread principale calcolava `bestMoveInstability` (search.cpp:586,
`1.077 + 2.229 * totBestMoveChanges / threads.size()`) usando SOLO il proprio
`_bestMoveChanges`, mentre `Search_` faceva `totBestMoveChanges += _bestMoveChanges;
_bestMoveChanges = 0;` **incondizionatamente in OGNI thread del pool**, non solo nel principale —
quindi anche se avessi provato a sommare i valori degli altri thread, li avrei trovati già
azzerati da loro stessi. Sintomo osservato: una mossa (un arrocco, mediogioco, niente di
tatticamente urgente) ha impiegato 111 secondi/73.7M nodi/depth 21 — molto più del normale — con
l'ipotesi confermata dall'utente stesso ("ma non avevi portato fedelmente la gestione del
tempo?").

**Causa**: la fonte reale (search.cpp:562-566) fa fare SOLO al thread principale un ciclo
`for (auto&&th:threads) {totBestMoveChanges+=th->worker->bestMoveChanges;
th->worker->bestMoveChanges=0;}` che legge E azzera il contatore di OGNI thread (compreso se
stesso) — un thread helper non tocca mai il proprio. Nella prima versione qui, invece, ogni
thread (compresi gli helper, che non usano mai `optimumMs`) auto-azzerava il proprio contatore ad
ogni iterazione: il valore del thread principale non era mai una vera media su 8 thread, solo la
propria volatilità individuale — che non riduce il rumore come fa la media reale, causando picchi
occasionali di tempo eccessivo su posizioni genuinamente instabili (l'evento non era un bug di
per sé — un cambio di mossa migliore vero — ma la sua ampiezza, sì).

**Fix**: nuovo `Search.PeekAndResetBestMoveChanges()` (legge+azzera, chiamabile dall'esterno);
`Search_` non auto-azzera più incondizionatamente — lo fa solo dentro
`if (optimumMs < NoBound)`, e solo tramite un nuovo delegato opzionale
`crossThreadBestMoveChanges` (più `threadCountForInstability`). `SearchThreadPool` costruisce
questo delegato (`SumAndResetBestMoveChangesAcrossPool`, chiama `PeekAndResetBestMoveChanges` su
OGNI `Search` del pool, se stesso incluso) e lo passa SOLO al thread principale — gli helper non
auto-azzerano mai il proprio contatore, accumulano finché il principale non li legge, esattamente
come la fonte. La divisione per `threads.size()` è applicata al momento dell'accumulo (matematicamente
equivalente ad applicarla al momento dell'uso, dato che il numero di thread è costante per tutta la
ricerca). Il caso standalone (nessun pool, delegato non fornito) resta equivalente al caso limite
`threads.size()==1` della fonte.

**Verificato**: 109/109 test; bench a 8 thread (51 posizioni) e `go wtime/btime` a 8 thread su una
posizione fuori libro, nessuna eccezione/deadlock nella lettura cross-thread. Non riprodotta la
stessa identica posizione volatile della partita dal vivo (avrebbe richiesto fermare il bot a
metà di una partita che stava vincendo) — la correttezza qui è verificata per costruzione/
equivalenza matematica con la fonte, non per confronto diretto di nodi come altrove nel progetto.

## Indagine sull'efficienza contro l'oracolo (2026-09-07) — dove va davvero il tempo

Richiesta esplicita dell'utente ("da fuori percepisco che il compilato C# sia quasi 100 volte più
inefficiente di quello C++"). Misurato con `bench 16 1 13` — stesse 51 posizioni, stessa
profondità, 1 thread, 16MB hash su entrambi i motori.

**Il divario si scompone in due fattori indipendenti**, ed è importante non confonderli:

| | Nodi | Tempo | Nodi/sec |
|---|---|---|---|
| Stockfish 19 (C++) | 2.497.913 | 1,56s | 1.599.176 |
| StockfishSharp, PRIMA | 12.226.331 | 46,12s | 265.109 |
| StockfishSharp, DOPO | 12.226.331 | 29,31s | 417.181 |

Totale prima: **29,5x** = **4,9x più nodi** (potatura/ordinamento, questione algoritmica) ×
**6,0x più lento per nodo** (implementazione). Dopo il lavoro sotto: **18,8x** = 4,9x × **3,8x**.

### Cosa NON era il problema (misurato, non ipotizzato)

- **SIMD**: tutto attivo e verificato a runtime (`Avx512F/BW/DQ/Vbmi`, BMI2, POPCNT disponibili;
  `NnueAccumulator.UsingAvx512`, `NnueLayers.UsingAvx512`, `Attacks.UsingAvx2` tutti veri).
- **Scacchiera/movegen/do-undo**: `perft(5)` sulla stessa posizione — 74,6M nodi/sec contro
  131,2M dell'oracolo, cioè solo **1,76x**, un rapporto C#/C++ del tutto normale. Entrambi i
  perft usano bulk counting all'ultimo ply, quindi il confronto è equo.
- **Allocazioni**: erano reali e vistose (**5.771 byte/nodo**, 163 GC gen0 al secondo) e sono
  state ridotte a 3.715 byte/nodo portando i buffer del forward pass NNUE sullo stack — ma il
  guadagno di velocità è stato del **2%, dentro il rumore**. Ipotesi plausibile, misurata e
  smentita: il GC gen0 di .NET è già molto economico per oggetti a vita brevissima. Restano da
  convertire `new StateInfo()` per mossa (360 byte in 6 allocazioni, perché i 5 campi array di una
  classe C# sono oggetti separati mentre in C++ sono inline nella struct) e i due `new List<Move>()`
  per nodo: corretti da fare, ma l'evidenza dice che valgono pochi punti percentuali.

### Cosa ERA il problema: fc_0 portato dal ramo di fallback non-SIMD

Misurato che la valutazione NNUE era **~92% del tempo per nodo**, e dentro di essa dominava il
layer fc_0 (1024→32). Causa: per fc_0 la fonte usa `AffineTransformSparseInput`, mentre qui era
stato portato `affine_transform_non_ssse3`, cioè **il ramo di fallback per macchine senza SIMD**,
che elabora tutti i 1024 input. Misurato sui dati veri: **il 76,6% degli input di fc_0 è ZERO**
(media su 5 posizioni, dall'apertura al finale) — si faceva ~4x il lavoro necessario sul layer più
costoso del motore.

La scelta di non portare la versione sparsa era stata presa in una sessione precedente per evitare
due "bit-trick" giudicati troppo rischiosi da verificare a mano (la permutazione dei pesi e il
prodotto scalare `VPDPBUSD`). L'utente ha giustamente contestato quella prudenza: contraddiceva la
policy [[feedback-porting-completeness-over-stability]], e il progetto ha già l'attrezzo che rende
sicuro affrontarli — i test bit-esatti scalare-contro-SIMD su dati casuali.

**Portato fedelmente (2026-09-07)**:
- `NnueLayerStack.BuildFc0ScrambledWeights` — `get_weight_index_scrambled`
  (affine_transform_sparse_input.h:79-82) con `ChunkSize=4`. Sostituendo l'indice del file
  (`j*L1 + inIdx`) nella formula della fonte si semplifica in `(inIdx/4)*(L2*4) + j*4 + inIdx%4`:
  256 blocchi da 128 byte, uno per gruppo di 4 input consecutivi, esattamente due `Vector512` per
  blocco. Applicata una volta al caricamento della rete (la fonte la applica mentre legge i pesi;
  qui si legge prima nel layout naturale, che serve comunque allo scalare e ai test).
- `NnueLayers.AffineTransformFc0SparseAvx512` — il ciclo di
  affine_transform_sparse_input.h:236-243: input letto come 256 chunk da 4 byte, chunk nulli
  saltati, chunk non nullo replicato su 16 corsie (`vec_set_32`) e moltiplicato per i due
  `Vector512` di pesi del blocco.
- `vec_add_dpbusd_32` nella variante **senza VNNI** che la fonte stessa ha in `simd.h`
  (`maddubs_epi16` + `madd_epi16` + somma), esposta da .NET come
  `Avx512BW.MultiplyAddAdjacent` — `Avx512Vnni` non esiste in .NET 10, ma questo è un percorso
  legittimo della fonte, non un ripiego nostro. La saturazione a i16 di `maddubs` non può
  avvenire: gli input sono 0..127 e i pesi -128..127, la somma di due prodotti adiacenti sta
  sempre entro 32767 — è il motivo per cui la fonte si permette entrambe le varianti.

**Misurato con metodologia identica sulle due implementazioni** (50.000 iterazioni di warmup,
migliore di 5 ripetizioni da 300.000 — una prima misura con warmup insufficiente aveva gonfiato
i valori assoluti, corretta): fc_0 da **2,210 µs a 0,119 µs = 18,5x**. Più del 4,3x della sola
sparsità perché il guadagno è doppio: si saltano i tre quarti degli input E si usa il prodotto
scalare u8×i8 invece di dodici `Widen` a int32.

**Verificato**: 110/110 test (nuovo `Fc0SparseMatchesScalarBitExactOnRandomInputs`: 30 prove con
sparsità realistica al 77% più i casi limite "tutto zero" e "nessuno zero", confronto bit-esatto
contro il percorso scalare che non salta nulla); i test NNUE preesistenti contro l'oracolo reale
continuano a passare; `bench 16 1 13` con **nodi IDENTICI** (12.226.331) e tempo da 46,19s a
29,31s — **il motore intero è 1,57x più veloce senza cambiare un solo bit di ciò che calcola**.

**Cosa resta sul lato velocità**: il 3,8x per nodo ancora presente non è più dominato da un
singolo punto noto; i candidati successivi sono le allocazioni residue (StateInfo/List, poche
percentuali), l'accumulatore incrementale senza Finny Tables/hybrid update (già annotato in Flow
B) e il codegen JIT contro nativo, che è incomprimibile. Il fattore **4,9x sui nodi** resta la
metà più grande del divario ed è lavoro algoritmico, non di implementazione.

## Lavoro algoritmico: Step 10 (null move) era ancora un segnaposto (2026-09-07)

Ripreso il fattore "4,9x più nodi" (la metà algoritmica del divario). Diagnostica: confronto
nodi/profondità contro l'oracolo su posizioni singole, poi **posizione per posizione su tutte le 51
del bench**. Risultati: il divario NON è uniforme (a depth 11: totale 2,8x, ma singole posizioni da
22x fino a posizioni dove siamo MIGLIORI dell'oracolo, 0,3x) e CRESCE con la profondità (2,8x a
depth 11, 4,9x a depth 13). Su un finale il rapporto esplodeva fra depth 6 e 8 (da 0,9x a 14,8x).

Verificati fedeli riga per riga, senza trovare errori: `reduction()` e la sua tabella
(`2872/128.0*log(i)`), tutte le correzioni per-mossa di Step 18 (3023/1004/885/816/940/697/65/
26310/4026/933/1079/264/1095/1138/2179...), Singular Extensions (Step 16, gate `depth >= 6 + ttPv`,
margini doppio/triplo, `depth++`, estensione negativa — e `ttMoveHistory` È consultata, la nota
precedente in questo documento era sbagliata), Step 15 (potatura a profondità bassa).

**Trovato invece che Step 10 (null move) non era mai stato portato**: era ancora il segnaposto
scritto nel primissimo commit del progetto (`0967bda`, dichiarato all'epoca "nucleo ISPIRATO a
search.cpp"), e differiva dalla fonte (search.cpp:1009-1043) su ogni singolo punto:

| | Fonte | Segnaposto |
|---|---|---|
| Quando | solo `cutNode`, con `staticEval >= beta - 13*depth - 47*improving + 365`, `!excludedMove`, `beta >= -2000`, `ply >= nmpMinPly` | qualunque nodo non-PV, nessuna condizione sulla valutazione |
| Riduzione | `R = 7 + depth/3 + max((staticEval-beta)/256, 0)` | fissa, `R = 4` |
| Verifica | ricerca di verifica a depth ≥ 16 con `nmpMinPly` | assente |
| Ritorno | `nullValue` | `beta` |

Il difetto più costoso era provare il null move su OGNI nodo non-PV invece che solo sui cutNode:
nei nodi "all", che per definizione non falliscono alto, non taglia quasi mai — lavoro sprecato a
ogni nodo, che compone con la profondità. Portato fedelmente, incluso il campo `nmpMinPly`
(azzerato a ogni ricerca) e `improving |= staticEval >= beta` (search.cpp:1046), anch'esso
mancante: influenza ProbCut, la formula di riduzione LMR e la soglia di potatura delle mosse
tardive.

Rimosse anche quattro costanti morte, impronta digitale del vecchio segnaposto
(`NullMoveMinDepth`, `NullMoveReduction`, `ReverseFutilityMaxDepth`, `ReverseFutilityMarginPerDepth`
— queste ultime due non erano nemmeno più usate da nessuna parte).

**Verificato**: 110/110 test; `bench 16 1 13` — nodi da **12.226.331 a 10.996.307 (-10,1%)**,
tempo da 29,31s a 27,76s. Rapporto nodi contro l'oracolo da **4,89x a 4,40x**.

**Metodo da riusare**: le costanti inventate che nella fonte non esistono (qui `NullMoveMinDepth`
ecc.) sono l'impronta digitale affidabile del codice segnaposto mai riconciliato con la fonte —
`grep "private const"` è un buon punto di partenza per trovarne altri.

## Gestione del tempo: verifica sistematica contro l'oracolo (2026-09-07)

Chiusura dell'indagine sul tempo, con la domanda posta bene: non "una partita e' andata male", ma
"quanto consumiamo dell'orologio residuo, in tutte le situazioni?". Banco di prova: 4 posizioni
(apertura, tattica, finale, complessa) x 8 stati di orologio (da 600s pieno fino a 2s disperato,
rapid e blitz), stessa identica prova eseguita su ENTRAMBI i motori.

**Risultato**: siamo ora piu' prudenti della fonte.

| | StockfishSharp | Stockfish 19 |
|---|---|---|
| Peggiore assoluto (una mossa) | **53,1%** dell'orologio | **81,0%** |
| rapid a corto (60s), posizione complessa | 31,7% | 81,0% |
| crisi (15s), apertura | 52,9% | 80,9% |
| blitz a corto (20s), apertura | 36,9% | 80,9% |

L'80% dell'oracolo non e' un'anomalia: e' esattamente il suo tetto di progetto
(`0,8097 * orologio residuo`, timeman.cpp). Quindi la formula portata non era il problema — il
problema erano i moltiplicatori che la spingevano verso quel tetto molto piu' spesso di quanto
accada alla fonte (la nostra ricerca cambia idea piu' spesso), piu' l'impossibilita' di fermare
un'iterazione gia' avviata. Entrambi corretti (vedi la sezione precedente).

Nota: restare piu' prudenti della fonte e' la scelta giusta PER NOI, non un difetto — la fonte puo'
permettersi l'80% perche' a quel punto ha gia' cercato a fondo; noi, con ~4,4x nodi in piu' per
profondita', arriviamo meno lontano nello stesso tempo e abbiamo piu' da perdere da una singola
mossa lunga.

**Reso permanente**: nuovo test `RealSearchNeverEatsADangerousShareOfTheClock` (3 posizioni, orologio
di 3 secondi, ricerca VERA non solo la formula) che fallisce se una mossa consuma piu' del 60%
dell'orologio. Soglia volutamente generosa: intercetta una regressione catastrofica (una mossa che
divora l'orologio, come accadeva prima), non tara i margini. Copre insieme i tre meccanismi
corretti: budget limitato, scadenza a meta' iterazione, tetto assoluto.

### Verifica del tempo a 2 e 8 thread (2026-09-07)

Richiesta dall'utente prima di passare al lavoro algoritmico: le mitigazioni sul tempo reggono anche
in multi-thread? Il dubbio era legittimo, perche' il multi-thread tocca proprio il termine che
gonfiava il budget (`bestMoveChanges` viene letto e mediato su tutti i thread del pool) e la
scadenza morbida la imposta solo il thread principale.

Prima misura (stress test con `ucinewgame` prima di OGNI posizione): 1 thread 53,1%, 2 thread 54,0%,
8 thread 58,7% — sembrava un degrado progressivo. **Era un artefatto del banco di prova**: misurato
a parte, il sovraccarico vero per RICERCA del multi-thread e' di soli **~27ms** (misurato con
`movetime` fisso, mediana di 6 ripetizioni, posizione fuori libro), mentre `ucinewgame` costa
**~250ms in piu' con 8 thread**, perche' `NewGame()` azzera le tabelle history di OGNI thread del
pool. In una partita vera `ucinewgame` arriva una volta sola, non a ogni mossa.

Rifatta la prova in condizioni realistiche (`ucinewgame` una volta sola) sugli scenari di orologio
critici (15s / 5s / 2s / blitz a 20s):

| Thread | Peggior caso su una mossa |
|---|---|
| 1 | 53,6% |
| 2 | 53,2% |
| 8 | 54,3% |

**Il numero di thread non incide sulla sicurezza del tempo.** Resta valida la scelta di
`Threads: 1` sul bot, ma per l'altra ragione gia' misurata (il Lazy SMP non migliora il
tempo-per-profondita'), non per motivi di gestione del tempo.

### Il Lazy SMP non era rotto: era il GC .NET (2026-09-07)

L'utente ha contestato la conclusione "il Lazy SMP non aiuta, mettiamo Threads=1": *"non capisco
perche' il multithread sull'oracolo funziona e sul nostro porting no, direi che il porting e'
sbagliato"*. Aveva ragione che qualcosa non andava, ma la causa non era nel porting dell'algoritmo.

**Primo chiarimento — la metrica era sbagliata.** "Tempo per raggiungere una profondita' fissa" non
misura il Lazy SMP: misurato che ANCHE L'ORACOLO peggiora con 8 thread su quel metro (tattica
0,96s→2,20s = 0,44x; complessa 1,83s→2,62s = 0,70x), e che a tempo fisso anche l'oracolo PERDE
profondita' nominale con 8 thread (complessa: d26 a 1 thread, d24 a 8). E' normale: il guadagno del
Lazy SMP si vede in Elo su molte partite, non in profondita' su una posizione.

**La differenza vera era nella scalabilita' grezza**: a 6s fissi l'oracolo passava da 6,6M a 34,2M
nodi (5,2x su 8 thread), noi solo da 1,5M a 4,3M (2,9x). Li' il porting stava davvero lasciando per
strada meta' del parallelismo.

**Causa trovata**: il GC di .NET era in modalita' **Workstation** (verificato a runtime,
`GCSettings.IsServerGC == false`), che usa un unico heap e ferma tutti i thread a ogni raccolta.
Con la ricerca che alloca ancora ~3,7 KB/nodo (vedi sopra), a N thread la pressione sul GC si
moltiplica per N e la raccolta diventa un punto di serializzazione proprio dove serve parallelismo.
Da notare: le allocazioni erano state misurate come **irrilevanti a thread singolo** (+2% dopo
averne tolte il 36%) — ed e' vero, ma diventano un collo di bottiglia REALE in parallelo. Le due
cose non sono in contraddizione.

**Fix**: `<ServerGarbageCollection>true</ServerGarbageCollection>` in StockfishSharp.Uci.csproj (un
heap e un thread di raccolta per core).

| | Prima (Workstation) | Dopo (Server) |
|---|---|---|
| Scalabilita' nodi, 8 thread, tattica | 2,9x | **4,8x** |
| Scalabilita' nodi, 8 thread, complessa | 3,1x | **6,4x** |

Ora siamo allineati o migliori dell'oracolo (5,2x). Costo: **-5,5% a thread singolo** (374k contro
396k nodi/sec, nodi identici) — il compromesso tipico del Server GC.

**Conseguenza sulla configurazione del bot**: rifatta la misura di profondita' a 6s fissi su 4
posizioni, con Server GC attivo — totale **70 (1 thread) / 73 (2) / 71 (4) / 72 (8)**. Il
multi-thread torna a guadagnare e **2 thread e' il migliore**, il che coincide con quanto l'utente
aveva gia' osservato su ACMyChess. `config-stockfishsharp.yml` portato da `Threads: 1` a
`Threads: 2`. Sicurezza sul tempo riverificata con Server GC e invariata: peggior caso 53,2% / 53,8%
/ 54,5% dell'orologio con 1 / 2 / 8 thread.

**Lezione**: prima di concludere che un porting e' sbagliato, separare l'algoritmo dalla
configurazione del runtime che lo ospita — e prima ancora, verificare che la metrica usata misuri
davvero la cosa giusta (qui "tempo per profondita' fissa" mostrava un problema anche sull'oracolo).

### La quiescenza era senza transposition table (2026-09-07) — il buco piu' costoso

Continuando la caccia ai segnaposto dopo il null move, controllata la QUIESCENZA, che nella fonte
ha dieci passi propri (search.cpp:1653-1883) e che in un motore di scacchi produce tipicamente la
maggioranza dei nodi. La nostra era l'abbozzo del primissimo commit:

| | Fonte (qsearch) | Nostro abbozzo |
|---|---|---|
| Transposition table | probe + taglio anticipato nei non-PV + scrittura finale | **assente del tutto** |
| Valutazione statica | correction history, eval da TT, ttValue come stima migliore | eval diretta |
| Stand pat | fail-soft, miscela `(441*best + 583*beta)/1024`, scrittura in TT | `return beta` secco |
| Potatura (Step 6) | futility (`futilityBase = staticEval + 306`), `moveCount > 2`, SEE contro `alpha - futilityBase`, SEE `>= -74` | solo `SeeGe(m) >= 0` |
| Ripetizione imminente | `upcoming_repetition` all'ingresso | assente |
| Ritorno | fail-soft (`bestValue`) | fail-hard (`alpha`/`beta`) |

Non avere la TT in quiescenza significava **ricalcolare da zero ogni trasposizione** nella parte
piu' popolosa dell'albero, e non alimentare la TT con i risultati di quiescenza per le visite
successive.

Portata fedelmente tutta (i dieci passi), inclusa la continuation history a UN SOLO livello
(search.cpp:1763, non i sei del ciclo principale) e il controllo di stallo nelle condizioni
ristrette della fonte.

**Verificato**: 113/113 test; `bench 16 1 13` — nodi da **10.996.307 a 4.945.987 (-55%)**, tempo da
29,4s a **14,1s (-52%)**. Rapporto nodi contro l'oracolo da **4,40x a 1,98x**. I nodi/sec calano
dell'11% (350k contro 396k) perche' ogni nodo di quiescenza ora fa anche probe di TT e controlli di
futility — ma se ne fanno meno della meta'. A tempo fisso la profondita' resta sostanzialmente pari
(71 contro 70 su 4 posizioni, misura rumorosa), con 3 mosse su 4 coincidenti con l'oracolo.

**Bilancio della caccia ai segnaposto**: due pezzi mai portati (null move, quiescenza) valevano
insieme un fattore ~2,2x sui nodi. Il metodo che li ha trovati: enumerare i passi numerati della
fonte e verificarli uno per uno, invece di fidarsi di cosa il piano dichiarava "fatto".

### Allocazioni dinamiche dove la fonte ha buffer fissi (2026-09-07)

Segnalazione dell'utente: *"stai attento anche ad allocazioni dinamiche che potresti incontrare
mentre sull'oracolo sono buffer fissi"*. Cercate una per una e convertite tutte quelle sul percorso
caldo. Nella fonte sono oggetti sullo stack o membri fissi delle struct; qui erano allocazioni per
nodo o per mossa.

| Punto | Fonte | Prima | Costo |
|---|---|---|---|
| `NnueAccumulator.Accumulation/PsqtAccumulation` | membri fissi della struct Accumulator | **sostituiti** a ogni refresh, **clonati** (`Clone()`, 2 KB per prospettiva) a ogni aggiornamento incrementale | il piu' pesante di tutti |
| `RefreshPerspective` | riempimento sul posto | `net.Biases.Clone()` + `new int[]` + 2 `new List<int>()` | ~2,2 KB per refresh |
| `ApplyIncrementalDelta` | `IndexList removed[2], added[2]` (capacita' fissa) | 4 `new List<int>()` per chiamata, per prospettiva | ~0,5 KB/nodo |
| `StateInfo st` | oggetto sullo stack | `new StateInfo()` per MOSSA (classe con 5 campi array = 6 allocazioni) | 360 B x mossa |
| `quietsSearched`/`capturesSearched` | `ValueList<Move,32>` sullo stack | 2 `new List<Move>()` per nodo | |
| `contHist[]` | array sullo stack | `new ContinuationRef[6]` per nodo | |

Tutte convertite a buffer preallocati (per ply nella ricerca, per accumulatore in NNUE) o a copie
sul posto (`CopyTo` invece di `Clone`).

**Bug trovato e corretto durante la conversione**: indicizzare `quietsSearched`/`capturesSearched`
per ply NON basta — la ricerca di verifica delle Singular Extensions richiama `Negamax` allo STESSO
ply, e la chiamata annidata azzerava le liste del nodo esterno (nella fonte non succede perche' sono
variabili locali sullo stack di ogni invocazione). Se ne era accorto il conteggio nodi, cambiato da
157.224 a 174.987: **una conversione di soli buffer deve lasciare i nodi IDENTICI**, e quella
verifica ha intercettato subito il problema. Risolto con due slot per ply (il ramo con
`excludedMove` non puo' rientrare nelle Singular Extensions, quindi non esiste un terzo livello).

**Verificato**: 113/113 test (compresi quelli bit-esatti NNUE contro l'oracolo); nodi IDENTICI a
ogni passaggio; allocazioni della ricerca da **5.771 a 210 byte/nodo (-96%)** — con la valutazione
NNUE disattivata restano 137 byte/nodo, cioe' il percorso di ricerca e' ormai quasi allocation-free.
`bench 16 1 13`: da 350.953 a **439.096 nodi/sec (+25%)**.

**Correzione a una conclusione precedente di questa stessa sessione**: le allocazioni erano state
giudicate "irrilevanti" perche' toglierne il 36% dava +2%. Era una misura corretta ma una
conclusione sbagliata: togliendone il 96% si guadagna il 25%. Il costo non e' il GC in se' (gen0 e'
economico) ma l'azzeramento della memoria e l'inquinamento continuo della cache, che diventano
significativi solo quando il volume scende sotto una certa soglia.

## Caccia ai passi numerati mai ispezionati: 13 discrepanze, parita' di nodi (2026-09-07)

Continuando il metodo che aveva gia' pagato (enumerare i `// Step N` della fonte e verificarli UNO
PER UNO invece di fidarsi di cosa il piano dichiarava "fatto"), ispezionati i passi 1, 3, 4, 6, 17,
18, 19, 21, 22, 24 della ricerca principale. Trovate **13 discrepanze reali**:

| Passo | Discrepanza |
|---|---|
| **22** | `depth -= 3` dopo un miglioramento di alpha (search.cpp:1533-1535) - **mai portato**. Agisce fra profondita' 4 e 11, dove vive la maggior parte dell'albero: la singola correzione piu' pesante di tutte |
| **22** | `inc` (promozione delle mosse a pari punteggio, search.cpp:1508-1510) non portato; `cutoffCnt` incrementato sempre invece che su `extension < 2` oppure `PvNode` |
| **6** | Era un abbozzo di 5 righe contro 50 della fonte: mancavano ENTRAMBE le guardie (`ttData.depth > depth - (ttData.value <= beta)` e `cutNode == (ttData.value >= beta)` oppure `depth > 4`), gli aggiornamenti di history sul taglio, la verifica del taglio a `depth>=7` giocando la mossa di TT, il rimedio `rule50 < 96`, e il `penalize` della entry inutile |
| **6** | Stava anche nel posto sbagliato: prima dello Step 5 e dell'hindsight adjustment, quindi tutte le sue soglie leggevano una `depth` non aggiustata |
| **18** | Ri-ricerca dopo LMR a finestra PIENA (`-beta`) invece che nulla (`-(alpha+1)`) - nei nodi PV la stessa ricerca veniva poi rifatta identica dallo Step 20 |
| **18** | `newDepth` non veniva mutato (variabile locale separata), quindi lo Step 20 ripartiva dalla profondita' non aggiustata buttando via il verdetto della ricerca ridotta |
| **18** | "Post LMR continuation history updates" (bonus 1334, search.cpp:1389-1390) mai portato |
| **18** | Un `is_valid` di troppo sul termine 885: nella fonte `VALUE_NONE == 32002 > alpha` e' VERO, quindi il termine si applica anche su TT miss |
| **3** | Mate distance pruning con bound sbagliato (`mate_in(ply)` invece di `mate_in(ply+1)`) **e applicato anche alla radice**, dove la fonte lo tiene dentro `if (!rootNode)` |
| **1** | `ss->moveCount = 0` e `ss->statScore = 0` (search.cpp:778 e 809) mai portati: un nodo che esce prima del ciclo mosse lasciava allo stesso ply i valori stantii di un fratello gia' cercato - e sono proprio i valori letti dai due rami che guardano "com'e' andato il genitore" |
| **4** | `ttCapture` usava `capture` invece di `capture_stage`; `ss->ttPv` non ereditata durante la ricerca di verifica delle Singular Extensions |
| **23** | `priorCapture` dal nostro flag `captureStage` invece che da `pos.captured_piece()` (differiscono su una promozione a donna senza cattura) |
| **Lazy SMP** | `threadIdx` non esisteva: tutti i thread partivano dalla STESSA finestra di aspirazione invece di `5 + threadIdx % 8` (search.cpp:376) - una delle poche fonti di diversita' del Lazy SMP, quindi i thread duplicavano lavoro |

**Come sono state trovate**: non leggendo il codice a caso, ma seguendo una misura. Il confronto
posizione-per-posizione contro l'oracolo sulle 51 posizioni di bench ha mostrato che il divario
**non era uniforme**: su Kiwipete eravamo persino MEGLIO (97.542 contro 119.236), ma esplodeva su
poche posizioni ad altissimo fattore di ramificazione - la #40 (`K7/8/8/BNQNQNB1/...`) a **57x**, la
#10 a 29x. Quel profilo indica una potatura per numero di mossa mancante, ed e' esattamente il
`depth -= 3` dello Step 22.

**Falsa pista utile, da ricordare**: le prime correzioni (Step 6 fedele) hanno FATTO SALIRE i nodi
del 27%, e l'istinto era scartarle. La misura giusta ha evitato l'errore: instrumentando il tasso di
tagli sulla prima mossa si e' visto che l'ordinamento MIGLIORAVA (85,87% -> 86,47%), quindi i nodi in
piu' non venivano da li' ma da riduzioni che si accorciavano. Erano correzioni giuste a cui mancava
ancora la loro contropartita (il `depth -= 3`). Stessa lezione gia' registrata due volte su questo
progetto: non scartare una tecnica trascritta fedelmente prima di aver cercato la tecnica-compagna.

**Risultato**: `bench 16 1 13` da 4.945.987 a **2.511.612 nodi (-49%)**, tempo da 11.264 a **5.643 ms
(-50%)**. L'oracolo sulla stessa bench fa 2.497.913 nodi: **rapporto 1,005x, parita' di nodi con
Stockfish vero** (era 1,98x stamattina e 4,40x prima della quiescenza fedele). Le posizioni
patologiche: #40 da 57x a **1,15x**, #39 da 9x a **0,69x** (meglio dell'oracolo), #43 da 8,5x a 1,25x.
Sul confronto posizione-per-posizione a TT fredda l'aggregato passa da 2,47x a **1,21x**, e la #10
ora sceglie la STESSA mossa dell'oracolo. 113/113 test.

**Cosa resta del divario di velocita'**: solo il rapporto grezzo nodi/secondo, ~3,6x (442.418 contro
~1,6M dell'oracolo) - cioe' ormai solo C# contro C++, non piu' un buco di porting. Il divario totale
misurato a inizio giornata era 29,5x.

## Stato dopo la parita' di nodi: thread, tempo, e una questione APERTA (2026-09-07)

**Sicurezza sul tempo, molto migliorata**: con orologio reale (4 posizioni x 4 scenari di orologio,
a 1/2/4/8 thread) il caso peggiore usa ora il **13,9% dell'orologio residuo**, contro il 53-54%
misurato stamattina e contro la soglia di 60% del test di regressione automatico
(`RealSearchNeverEatsADangerousShareOfTheClock`). Conseguenza diretta della parita' di nodi: si
arriva alla stessa profondita' in meta' tempo, quindi la gestione tempo adattiva si ferma prima.

**Numero di thread: il vantaggio dei 2 thread e' sparito.** Profondita' raggiunta con `go movetime
6000` su 3 posizioni reali: 1 thread 55, 2 thread 54, 4 thread 54, 8 thread 55 — piatto, tutto
dentro il rumore. Prima delle correzioni di oggi la stessa misura dava un vantaggio ai 2 thread
(70/73/71/72 su 4 posizioni). Raddoppiata l'efficienza single-thread, il numero di thread non fa
piu' differenza misurabile su questa macchina. `Threads: 2` lasciato invariato nel config del bot:
cambiarlo di nuovo su una differenza dentro il rumore sarebbe solo churn.

Scalabilita' grezza (nodi/secondo, bench): 1 thread 439.246, 2 thread 830.744 (1,89x) — la
scalabilita' c'e', ma a profondita' fissa il Lazy SMP moltiplica i nodi (2,6x a 2 thread) quindi
non si traduce in tempo-a-profondita'.

### QUESTIONE APERTA: blocco intermittente del bench multi-thread

Osservato **2 volte** (entrambe nei primi due bench multi-thread eseguiti dopo le correzioni): il
`bench 16 4 13` si e' fermato sulla posizione 27 (`6k1/6p1/P6p/r1N5/5p2/7P/1b3PP1/4R1K1 w`)
bruciando CPU per oltre 20 minuti invece dei ~9 secondi normali. **Non piu' riproducibile**: 15
esecuzioni successive tutte pulite (8 a 4 thread, 5 a 8 thread, 2 a 4 thread). Quindi e' una race,
non un bug deterministico.

Cosa si e' verificato:
- La posizione **in isolamento** e' sana a 1/2/4/8 thread (1,8-2,0s, nodi coerenti). Il blocco
  richiede lo stato accumulato (TT + history) delle 26 posizioni precedenti — il bench fa
  `ucinewgame` una volta sola all'inizio, come la fonte.
- La propagazione dello stop nel pool, riletta, e' corretta: il thread principale chiama
  `stopCts.Cancel()` appena finisce, e gli helper controllano il token ogni 2048 nodi.
- **L'esposizione in partita e' molto minore che nel bench**: il bench passa
  `timeLimit = TimeSpan.FromHours(1)` (gli helper vanno fino a `Ply.MaxPly`), mentre in partita il
  limite e' la scadenza vera. La verifica con orologio reale sopra (16 combinazioni x 4 conteggi di
  thread) non ha mai superato il 13,9% del budget.

Da tenere d'occhio. Se si ripresenta, il primo sospetto da controllare e' un helper che non vede la
cancellazione fra un'iterazione di iterative deepening e la successiva.

## Due divergenze trovate partendo da una domanda dell'utente (2026-09-07 sera)

L'utente ha chiesto: "la dimensione della TT e' uguale a quella dell'oracolo?". Il valore `Hash`
passato ai due motori nei confronti era lo stesso, ma la domanda ha fatto emergere due divergenze
reali, entrambe corrette.

### 1. Dimensione del cluster della transposition table

- **Fonte** (tt.cpp:170-184): `struct Cluster { TTEntry entry[3]; char padding[2]; }` con
  `static_assert(sizeof(Cluster) == 32)`, e `clusterCount = mbSize*1MB / sizeof(Cluster)`.
- **Nostro**: nessun cluster, un `TTEntry[]` piatto e `clusterCount = mbSize*1MB / (3*10)`, cioe'
  diviso 30 invece di 32.

A parita' di `Hash` dichiarato allocavamo il **6,67% di entry in piu'** della fonte (con Hash 16:
559.240 cluster contro 524.288), quindi ogni confronto di nodi contro l'oracolo era leggermente a
nostro favore. E quei 2 byte di padding non sono spreco: portano il cluster a mezza cache line, cosi'
le tre entry sondate insieme non scavalcano mai due linee — e la probe della TT e' l'accesso casuale
piu' frequente del motore. Corretto con un vero `Cluster` da 32 byte. `bench 16 1 13` passa da
2.511.612 a **2.535.248 nodi**: contro i 2.497.913 dell'oracolo il rapporto e' 1,015x — parita'
confermata, ma ora misurata onestamente.

### 2. Il punteggio UCI non era normalizzato (ne' esisteva "score mate N")

Cercando la causa di una divergenza di punteggio nei finali e' emerso che a **profondita' 1** —
dove sotto la radice c'e' solo la quiescenza — il nostro punteggio era sistematicamente ~3x quello
dell'oracolo: **40 posizioni su 49** divergevano di oltre 80cp, con rapporto costante 3,0-3,4.

Non era un bug di ricerca: da Stockfish 16 il punteggio mostrato NON e' il valore interno del
motore. `UCIEngine::to_cp` (uci.cpp:585) lo divide per il parametro `a` del modello WDL
(`win_rate_params`, uci.cpp:537-553), calibrato perche' "+1.00" significhi davvero un pedone di
vantaggio in termini di probabilita' di vittoria; il fattore vale ~3,0-3,4 e dipende dal materiale
sulla scacchiera. Questo porting stampava il valore interno grezzo. Seconda meta' dello stesso buco:
si stampava **sempre** `score cp`, quindi i matti uscivano come centipawn enormi e nessuna GUI poteva
mostrare l'annuncio di matto (idem per i punteggi da tablebase, che nella fonte hanno la codifica
convenzionale +/-20000 meno la distanza).

Portati in `StockfishSharp.Uci/UciScore.cs`: `win_rate_params`, `win_rate_model`, `to_cp` e
`format_score` + la classificazione di `score.cpp`. **Verifica**: a profondita' 1 il nostro
punteggio ora combacia ESATTAMENTE con quello dell'oracolo su tutte le posizioni provate (cp 143 /
cp 829 / cp 155 / cp 0), e i matti escono come `mate 1` / `mate 0` identici. Nodi invariati (e' un
cambio di sola presentazione), 113/113 test.

**Conseguenza pratica**: fino ad ora ogni valutazione pubblicata dal bot su lichess era gonfiata di
~3,3 volte, e nessun matto veniva annunciato come tale.

**Nota di metodo, da ricordare**: prima di trovare la causa avevo interpretato quel 3x come "il
nostro punteggio alla radice crolla mentre quello dell'oracolo resta stabile" e ne avevo tratto
conclusioni sulla qualita' della ricerca. Erano conclusioni costruite su un confronto fra UNITA'
DIVERSE. Quando due motori divergono di un fattore quasi costante su molte posizioni, sospettare
prima le unita' di misura e solo dopo l'algoritmo.

## Terza divergenza della stessa famiglia: il CONTEGGIO DEI NODI (2026-09-07 sera)

L'utente ha insistito sul principio: "anche il fail-low puo' essere solo una divergenza nel
porting". Applicandolo, cercando l'origine delle tempeste di fail-low, e' emerso che a profondita' 1
sulla stessa posizione facevamo 34 "nodi" contro i 17 dell'oracolo — con punteggio IDENTICO. Il
doppio esatto.

**La fonte incrementa il contatore in UN SOLO punto**, dentro `Search::Worker::do_move`
(search.cpp:658): conta le **mosse giocate**, non le invocazioni di `search()`. Questo porting
faceva `_nodes++` in cima a `Negamax` E a `Quiesce`, quindi contava tre cose in piu':
- ogni invocazione invece di ogni mossa (un nodo a `depth<=0` che passa subito in quiescenza veniva
  contato DUE volte);
- il nodo radice, che la fonte non conta;
- i figli del **null move**, che la fonte non conta affatto (`do_null_move` non tocca il contatore).

Non era solo cosmetico: `info nodes`/`nps` pubblicati alla GUI erano gonfiati, e sia `value_draw()`
sia `inc` dello Step 22 LEGGONO il contatore, quindi anche il comportamento della ricerca era
leggermente diverso da quello della fonte. Aggiunto un contatore separato `_visits` per la sola
cadenza dei controlli di tempo/cancellazione (ogni 2048 invocazioni), ruolo che prima svolgeva
`_nodes`.

**Verifica**: a profondita' 1 e 2 il conteggio ora combacia ESATTAMENTE con l'oracolo (17 e 34 nodi).

### Il quadro corretto, dopo le tre divergenze

Tutte le misure di nodi contro l'oracolo fatte prima di questa correzione erano fra grandezze
diverse. Rifatte:

| | nostro | oracolo | rapporto |
|---|---|---|---|
| nodi `bench 16 1 13` | **2.135.982** | 2.497.913 | **0,86x** |
| tempo | 6.078 ms | 1.561 ms | 3,89x |
| nodi/secondo | 351.428 | 1.600.200 | 4,55x |

Cioe': l'albero che esploriamo e' ora **piu' piccolo del 14%** di quello di Stockfish vero a parita'
di profondita' — non "parita' di nodi" come riportato prima. Tutto il divario residuo e' velocita'
grezza, C# contro C++.

**Resta aperto** il comportamento nei finali: sul finale di pedoni `8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8`
i punteggi combaciano ESATTAMENTE fino a profondita' 4 (143, 143, 33/34, 124 in unita' normalizzate)
e divergono da 5 in poi, con tempeste di fail-low a profondita' 13-14 (otto ri-ricerche consecutive,
437.000 nodi per una sola iterazione). La meccanica dell'aspiration window e la formula della media
mobile che centra la finestra sono state verificate fedeli riga per riga; la causa a monte della
divergenza a profondita' 5 non e' ancora isolata.

**Lezione, ormai tre volte in una sera**: quando i due motori divergono di un fattore quasi costante
o di un multiplo intero pulito, sospettare le CONVENZIONI (unita' di misura, semantica di un
contatore, dimensione di una struttura) prima dell'algoritmo. Tre divergenze trovate cosi' nella
stessa sessione: dimensione del cluster TT, normalizzazione del punteggio UCI, semantica del
contatore nodi.

### Caccia alla divergenza di punteggio: cosa e' stato ESCLUSO (2026-09-07 sera)

Con i punteggi finalmente nelle stesse unita' e i nodi nella stessa semantica, la domanda residua e'
diventata: a che profondita' iniziamo a divergere dall'oracolo, e perche'? Misurato su 6 posizioni:

| posizione | prima divergenza > 25cp |
|---|---|
| tattica (`r1bbk1nr/...`) | d2 |
| finale di torre | d3 |
| finale di pedoni (`8/2p5/...`) | d5 |
| mediogioco (`4r1k1/...`) | d8 (esatti fino a d7) |
| kiwipete | d9 (esatti fino a d6) |
| finale di pedoni 2 | nessuna fino a d10 |

**A profondita' 1 il punteggio combacia ESATTAMENTE su tutte** (829/829, -139/-139, 81/81, 49/49,
143/143), il che convalida valutazione e quiescenza. Non c'e' una soglia unica: l'inizio della
divergenza dipende dalla posizione, il che indica micro-divergenza accumulata piuttosto che una
singola tecnica sbagliata.

**Ipotesi verificate ed ESCLUSE** (tutte con misura, non a occhio):
- **Valutazione**: identica (stessa rete, `eval` combacia).
- **Quiescenza**: esclusa dai punteggi esatti a profondita' 1.
- **Ordine di generazione delle mosse**: portato `perft` con la ripartizione per mossa ("divide",
  perft.h:44-54, che questo porting non aveva) e confrontato l'elenco completo su 3 posizioni:
  **stesso ordine e stessi conteggi**, mossa per mossa (48, 14 e 40 mosse).
- **Ogni singola tecnica di ricerca**: ablazione una alla volta (Step 6 intero, la sola guardia
  `depth > 4`, Step 22 `depth -= 3`, ProbCut, razoring, futility, null move, bonus post-LMR, `inc`).
  Nessuna, disattivata, riporta i punteggi a combaciare — quindi nessuna e' "la" colpevole.
- **Soglie di `partial_insertion_sort`**: identiche (`int.MinValue` e `-3560 * depth`).
- **Inizializzazione dello stack prima della radice**: `_staticEvalHistory[0..6] = Values.None` come
  la fonte (se fossero rimaste a 0, `improving` sarebbe stato sbagliato a ply 0 e 1).

Il reproducer piu' piccolo disponibile e' la posizione "tattica", che diverge gia' a **profondita'
2** (649 contro 605) pur combaciando esattamente a profondita' 1: a quella profondita' l'albero e'
abbastanza piccolo da poter essere confrontato nodo per nodo. E' il punto da cui ripartire.

## Audit sistematico: cosa NON avevamo mai portato (2026-09-07 sera)

Richiesta dell'utente: "controlla tutto quello che rimane togliendo quello che hai controllato,
oppure se c'e' qualcosa sui sorgenti dell'oracolo che noi non abbiamo mai portato". Fatto con due
metodi complementari.

### Metodo 1 — dal profilo del sintomo

I punteggi combaciavano ESATTAMENTE a profondita' 1 e divergevano sempre di piu' salendo: il profilo
di qualcosa che a inizio ricerca e' vuoto e si riempie strada facendo. Ha portato a due pezzi mai
portati:

- **`do_null_move` non aggiornava lo Stack** (search.cpp:674-679). La fonte non si limita a fare la
  mossa nulla: imposta `ss->currentMove = Move::null()` e punta le due continuation history alle
  caselle `NO_PIECE`. Qui si faceva solo `DoNullMove` e si ricorreva, quindi il figlio del null move
  leggeva come "mossa che ha portato qui" quella lasciata da un fratello gia' cercato allo stesso
  ply — dato stantio di un sottoalbero estraneo. Lo leggono la continuation history del figlio, il
  bonus differenza-di-valutazione dello Step 5, il bonus countermove dello Step 23 e la correction
  history.
- **Le LETTURE di continuation history erano filtrate come le scritture.** Nella fonte il puntatore
  `(ss-i)->continuationHistory` e' SEMPRE valido: senza una mossa reale punta a
  `continuationHistory[0][0][NO_PIECE][SQ_A1]`, che vale **-586** (valore di inizializzazione, mai
  riscritto perche' le scritture sono filtrate da `currentMove.is_ok()`); le letture non lo sono
  mai. Noi restituivamo 0. Lo scarto entra in `statScore` (riduzione LMR) e nelle soglie dello
  Step 15, dove conta il valore assoluto.

Effetto misurato: la profondita' alla quale il punteggio inizia a divergere dall'oracolo si sposta
piu' in fondo — posizione "tattica" da **d2 a d8** (ora esatta fino a d5: 605/605, 478/478, 478/478,
478/478), finale di torre da d3 a d4, kiwipete da d9 a d10.

### Metodo 2 — audit meccanico delle costanti

Ogni costante numerica del codice della fonte deve comparire nel porting; una assente e' logica
mancante. Su `search.cpp` restavano 6 costanti non presenti, di cui **3 erano vera logica**:

- **`729`** (search.cpp:328-330): a ogni nuova RICERCA — cioe' a ogni mossa della partita, non a
  ogni nuova partita — la main history viene fatta **decadere di 729/1024**. Mai portato: si
  accumulava per l'intera partita senza mai smorzarsi. Un bench a profondita' fissa da processo
  fresco non puo' accorgersene; in partita l'ordinamento peggiora mossa dopo mossa.
- **`713`** (search.cpp:2000-2003): un intero ramo dello Step 23, *"extra penalty for a quiet early
  move that was not a TT move in previous ply when it gets refuted"*. Richiede `(ss-1)->ttHit`,
  aggiunto come array per ply.
- **`statScore / 28`** (search.cpp:1976): il bonus dello Step 23 e'
  `min(133*depth-81, 1487) + 364*(bestMove==ttMove) + (ss-1)->statScore/28` — mancava l'ultimo
  termine. L'audit non l'aveva pescato (28 e' a due cifre): trovato leggendo la riga accanto a una
  delle costanti mancanti.

Le altre 3 sono Skill Level / UCI_Elo, dichiarati inerti. Rifatto l'audit includendo anche le
costanti a due cifre usate in moltiplicazioni e divisioni: su 50, **ne resta una sola** assente, ed
e' un falso positivo (da noi e' scritta `100000UL`, il suffisso confonde il riconoscitore).
`movepick.cpp`, `history.h`, `timeman.cpp` ed `evaluate.cpp`: **zero costanti mancanti**.

### Metodo 2b — rilettura integrale delle funzioni attorno ai buchi

Trovati due buchi in `update_all_stats`, ho riletto per intero quella funzione e il suo chiamante
(Step 23). E' emerso un altro pezzo mai portato: **la propagazione di `ttPv` sul fail-low**
(search.cpp:1614-1617, *"If no good move is found and the previous position was ttPv..."*). Non e'
cosmetico: quel flag finisce nella entry di TT e da li' governa la riduzione LMR di chiunque
ritrovi la posizione (Step 18, `if (ttPv) r -= 3023 + ...`, oltre tre ply di riduzione in meno).

### Ipotesi ESCLUSE con misura (non a occhio)

- **Valutazione**: identica (stessa rete, `eval` combacia, punteggi esatti a profondita' 1).
- **Quiescenza**: esclusa dagli stessi punteggi esatti a profondita' 1.
- **Ordine di generazione delle mosse**: portato `perft` con la ripartizione per mossa ("divide") e
  confrontato l'elenco completo su 3 posizioni — **stesso ordine e stessi conteggi**, mossa per
  mossa (48, 14 e 40 mosse).
- **Ogni singola tecnica di ricerca**, ablata una alla volta (Step 6 intero, la sola guardia
  `depth > 4`, Step 22 `depth -= 3`, ProbCut, razoring, futility, null move, bonus post-LMR, `inc`):
  nessuna, disattivata, riporta i punteggi a combaciare.
- **Soglie di `partial_insertion_sort`**: identiche.
- **Inizializzazione dello stack prima della radice**: `_staticEvalHistory[0..6] = Values.None`, come
  la fonte.
- **Correction history**: tutte e 5 le tabelle presenti, formula e costanti identiche.

### Stato dei nodi dopo tutto questo

`bench 16 1 13`: **2.515.241** nodi contro i **2.497.913** dell'oracolo = **1,007x**. Le correzioni
di fedelta' hanno fatto SALIRE il conteggio (da 2.135.982) — il "vantaggio" precedente veniva dalle
divergenze stesse, che ci facevano potare piu' del dovuto.

**Cosa resta non spiegato**: la divergenza di punteggio a profondita' medie su alcune posizioni
(finale di pedoni ancora a d5) e le tempeste di fail-low che ne conseguono. Non e' riconducibile a
una tecnica mancante fra quelle verificate. Il gap piu' grosso rimasto nel porting e'
`nnue_accumulator.cpp` (953 righe contro le nostre 411: Finny Tables e aggiornamento ibrido), ma
sono ottimizzazioni di velocita' che producono gli STESSI valori, quindi non possono spiegare una
divergenza di punteggio.

## Le parti non portate di nnue_accumulator.cpp nascondono incorrettezze? (2026-09-07 notte)

Domanda dell'utente, dopo che avevo liquidato le 542 righe non portate come "ottimizzazioni di
velocita' che producono gli stessi valori". Era un'**assunzione**, non un fatto verificato. Verificata
su tre livelli.

### 1. La policy di refresh e' fedele

`requires_refresh` nella fonte e' `diff.pc == make_piece(perspective, KING)`
(half_ka_v2_hm.cpp:102-104) — identica alla nostra. E usa lo STESSO feature set:
`find_last_usable_accumulator` invoca `PSQFeatureSet::requires_refresh`, e
`PSQFeatureSet = Features::HalfKAv2_hm` (nnue_architecture.h:42), che e' esattamente quello che
chiamiamo noi. Il commento della fonte spiega anche perche' basta: *"Threat feature set refreshes
require a king move across the center, i.e., a subset of halfka refreshes"* — la condizione HalfKA
(qualunque mossa di re) e' la piu' larga delle tre, quindi copre anche gli altri due feature set.
`find_last_usable_accumulator` combacia riga per riga.

### 2. I pezzi mancanti sono percorsi ALTERNATIVI, non logica diversa

| non portato | cosa fa | perche' e' neutro sui valori |
|---|---|---|
| `backward_update_incremental` | dopo un refresh dell'ultimo frame, riempie all'INDIETRO i frame intermedi marcandoli "computed" | e' cache: quei frame verrebbero comunque ricalcolati alla prossima valutazione |
| `update_accumulator_refresh_cache` | calcola il refresh partendo da uno stato in cache (Finny table) invece che da zero | stesso accumulatore, per differenza invece che da capo |
| `update_accumulator_hybrid` | scorciatoia per le mosse di re nella stessa meta' di scacchiera | percorso alternativo allo stesso risultato |
| `forward_update_incremental_both` | aggiorna le due prospettive insieme | solo fusione dei due cicli |

Al loro posto facciamo sempre il refresh completo: piu' lavoro, stessi valori.

### 3. Ma il test che avrebbe dovuto dimostrarlo aveva un buco reale

`NnueIncrementalTests` chiamava `Evaluate` a **ogni** nodo: l'accumulatore era quindi sempre gia'
aggiornato e il recupero risaliva al massimo di UN frame. Nella ricerca vera non e' cosi' — un nodo
sotto scacco non valuta affatto, e un taglio da transposition table esce prima dello Step 5 — quindi
si accumulano piu' ply senza valutazione e il successivo `Evaluate` deve risalire fino all'ultimo
accumulatore utilizzabile e riapplicare in avanti TUTTI i delta intermedi
(`FindLastUsableAccumulator` + `ForwardUpdateIncremental`). **Quel percorso non era coperto da
nulla**, ed e' esattamente dove un errore resterebbe silenzioso: nessun crash, solo valutazioni
sbagliate.

Nuovo `NnueIncrementalGapsTests`: partite casuali lunghe (semi fissi, rigiocate finche' non si sono
raccolte almeno 40 valutazioni per seme), valutando solo **una volta su quattro** cosi' da creare
buchi di lunghezza variabile, con confronto bit-per-bit contro `ComputeFromScratch` a ogni
valutazione. Con guardie esplicite che verificano che il test stia davvero coprendo qualcosa: numero
minimo di valutazioni, buco massimo di almeno 3 frame, almeno una mossa di re (che forza il refresh).
Verde su 4 posizioni x 4 semi; 117/117 test totali.

La catena di prova regge perche' `ComputeFromScratch` era gia' verificato bit-esatto contro l'oracolo
(N1-N8): da-zero == oracolo, incrementale == da-zero, quindi incrementale == oracolo.

**Lezione di metodo** (vedi la regola generale dell'utente): un test che esiste non e' una prova
finche' non si e' guardato COSA copre davvero. Qui copriva solo il caso facile.

## Come si misura la fine

Il criterio di completamento del progetto non è "tutti i file portati", ma:
**a parità di posizione, profondità fissa e opzioni, StockfishSharp e l'eseguibile ufficiale
scelgono la stessa mossa e visitano lo stesso numero di nodi.** È il test che Stockfish stesso
usa fra build diverse (`bench`), ed è l'unico che dimostra che il porting è fedele davvero.

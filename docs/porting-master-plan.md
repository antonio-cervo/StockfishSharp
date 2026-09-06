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
prestazioni no — riutilizzare buffer invece di allocare è l'ottimizzazione naturale successiva, non
fatta qui per restare dentro lo scopo di questo Step (generazione a stadi, non le sue prestazioni).

Manca ancora in Flow A2: i due usi mancanti di PawnHistory (bonus da differenza di valutazione
statica, bonus al countermove — legati a tecniche non ancora portate), e `reduction()`/`statScore`
che oggi usano solo main+contHist[0,1] invece di sfruttare tutta l'informazione ora disponibile in
`MovePicker`.

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

**Manca**: infrastruttura opzioni generica, `setoption` completo, `bench`, `MultiPV`,
`UCI_LimitStrength`/`UCI_Elo`, `UCI_ShowWDL`, conversione punteggi WDL, `Skill Level`, `d`,
`flip`, `compiler`, `export_net`.

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

**Manca ancora**: `pos_is_ok`, `material_key_is_ok`, `flip`, `dtz_is_dtm` (debug/tablebase), tutta
la macchina `update_piece_threats`/`DirtyThreats` — quest'ultima è **prerequisito di N9**.

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

---

## Flusso C — Il resto

### C1 — Threading (`thread.h`/`thread.cpp`, 464+ righe)
Lazy SMP. Prerequisito: N9 (accumulatore per-thread). Oggi il motore è a thread singolo.

### C2 — Tablebase Syzygy (`syzygy/`, 2.053 righe)
Mai aperto. I file di dati sono già disponibili in `../ACMyChess/Syzygy/`.

### C3 — Utilità (`misc`, `memory`, `score`, `numa`, `tune`, `universal/`, ~1.500 righe)
Portate finora solo le briciole che servivano (`PRNG` dentro `Attacks.cs`).

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
   catena, in quest'ordine.
6. **A3, A4** (tempo, UCI) — meno urgenti: le versioni attuali funzionano, il divario è in
   completezza di funzioni, non in forza.
7. **C2** (Syzygy), **C3** (utilità) — alla fine.

## Come si misura la fine

Il criterio di completamento del progetto non è "tutti i file portati", ma:
**a parità di posizione, profondità fissa e opzioni, StockfishSharp e l'eseguibile ufficiale
scelgono la stessa mossa e visitano lo stesso numero di nodi.** È il test che Stockfish stesso
usa fra build diverse (`bench`), ed è l'unico che dimostra che il porting è fedele davvero.

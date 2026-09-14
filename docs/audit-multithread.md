# Audit del percorso MULTI-THREAD

Iniziato il 2026-09-11 su richiesta dell'utente. È la parte meno ispezionata del porting, ed è il
motivo per cui il bot gira a `Threads: 1` dall'8 settembre.

**Perché si riapre adesso.** Il 10 settembre abbiamo battuto eigenmann-chess (2697) a 15+10: un C++
scritto da zero che dichiara **+69 Elo dal solo secondo thread di ricerca**. È un vantaggio che gli
avversari di quella fascia hanno e noi no. Il costo misurato del restare a 1 thread è **~1,5 ply di
profondità media** (18,33 contro 19,85 a 8 thread). *(Rimisurato il 2026-09-14 e NON riprodotto:
vedi "Come è finita" in fondo. La riga resta com'era scritta allora.)*

**Perché eravamo tornati a 1.** Non per un bug dimostrato, ma per una mossa persa e *non
riproducibile*: partita z9HiBxcS contro pawn_git (2802), mossa 58, `Bd5` che regala un alfiere in un
finale vinto. Quindici ripetizioni offline a 8 thread sulla stessa posizione danno sempre `Qc7+` o
`Qg7+`, mai `Bd5`. A un thread il motore è **deterministico**, quindi qualunque errore si riproduce
e si analizza; a più thread no. Questa resta la ragione di fondo, ed è buona.

---

## Quello che è GIÀ FEDELE (verificato riga per riga contro la fonte)

| cosa | fonte | noi |
|---|---|---|
| voto fra i thread | `ThreadPool::get_best_thread`, thread.cpp:357-408 | `GetBestResult`, comprese le tre condizioni di "decisivo" e lo spareggio sulla lunghezza della PV |
| differenziatore Lazy SMP | `delta = 5 + threadIdx % 8 + ...`, search.cpp:376 | `Search.cs:1030`, identico |
| limite di profondità solo al principale | search.cpp:334 | gli helper ricevono `Ply.MaxPly`, il principale `maxDepth` |
| gestione tempo solo al principale | `if (!mainThread) continue;`, search.cpp:554 | gli helper ricevono `optimumMs = NoBound`, che salta il blocco |
| controllo dell'orologio solo al principale | `if (is_mainthread())`, search.cpp | `if (_threadIdx == 0) CheckTime();`, Search.cs:1565 |
| righe PV solo dal principale | search.cpp:495 | `CostruisciInfoPv` esce subito se `_threadIdx != 0` |
| somma di `bestMoveChanges` e azzeramento | search.cpp:561-566 | `SumAndResetBestMoveChangesAcrossPool` |
| stato per thread contro condiviso | `SharedHistories` condivise, il resto per `Worker` | TT e `SharedHistories` condivisi; `MovePick` e accumulatore NNUE per thread |
| `nodes`/`tbHits` del risultato finale | `accumulate(...)`, thread.cpp:152-153 | `results.Sum(...)` in `GetBestResult` |

---

## DIVARI TROVATI

### 1. `increaseDepth` è LOCALE invece che condiviso — divario di fedeltà

Nella fonte è un membro di `ThreadPool`: `std::atomic_bool stop, increaseDepth;` (thread.h:157).
Lo scrive **solo il thread principale** (search.cpp:613, dentro il blocco a lui riservato) e lo
leggono **tutti** (search.cpp:356):

```cpp
if (!threads.increaseDepth)
    searchAgainCounter++;
```

Da noi è una **variabile locale** di ogni `Search_` (`Search.cs:964`). Conseguenza: gli helper la
trovano sempre `true` e **non incrementano mai `searchAgainCounter`**, quindi cercano sempre a
profondità piena anche quando il principale ha deciso che il tempo è stretto. Nella fonte, quando il
tempo stringe, gli helper fanno ricerche più economiche che riempiono la TT — è parte di come il
Lazy SMP guadagna.

Invisibile a `Threads=1`, dove scrittore e lettore sono lo stesso thread.

### 2. Se il thread principale esce per eccezione, gli helper girano PER SEMPRE — difetto di robustezza

Gli helper hanno **una sola uscita**: `_segnaleStop.Alzato`. Il loro
`cts.CancelAfter(timeLimit)` esiste ma **nessuno lo guarda mai**, perché `CheckTime()` — l'unico
punto in cui si legge il token — è chiamato solo dal principale (fedele alla fonte).

E `_segnaleStop.Alza()` sta **fuori** dal corpo del task principale, subito dopo la chiamata:

```csharp
results[idx] = _searches[idx].Search_(...);
_segnaleStop.Alza();   // non viene eseguito se Search_ lancia
stopCts.Cancel();
```

Se `Search_` del principale lancia, il flag non si alza, gli helper non hanno scadenze e
`Task.WaitAll` **non torna mai**. Processo vivo, motore muto: è **la stessa firma** del
"blocco intermittente del bench multi-thread, visto 2 volte e mai più riprodotto in 15 prove"
annotato in memoria, e la stessa dei piantamenti in partita già risolti.

Correzione proposta, piccola e a rischio nullo: `try/finally` attorno al corpo del principale, così
`Alza()` e `Cancel()` avvengono comunque.

### 3. `nodes` e `tbhits` nelle righe info sono del solo worker — divario già noto

`Search.cs:1317` e `1398` riportano `_nodes` del thread corrente; la fonte usa
`threads.nodes_searched()`, cioè **tutto il pool** (`output_pv`). Il risultato FINALE è corretto
(`GetBestResult` somma), sbagliate sono le righe `info` emesse durante la ricerca. A `Threads=1`
coincidono, quindi oggi non morde.

---

## Da guardare ancora (non ancora ispezionato)

- **La TT sotto concorrenza**: la fonte accetta corse benigne su `TTEntry` (scritture non atomiche,
  10 byte). Verificare che la nostra struttura abbia la stessa larghezza e le stesse conseguenze, e
  che `generation8` non venga corrotto da scritture concorrenti.
- **Visibilità dei contatori fra thread**: `PeekAndResetBestMoveChanges` legge i contatori degli
  altri thread mentre quelli scrivono. In C++ è una corsa benigna dichiarata; in C# un `ulong` su
  x64 non si spezza, ma va confermato il tipo del campo.
- **Riproducibilità**: capire se si può avere un modo *deterministico* a più thread (seme fisso,
  ordine di partenza fisso) da usare nei test, per non perdere la proprietà che ci ha fatto tornare
  a 1 thread.

---

## Ordine dei lavori proposto

1. Correggere il **punto 2** (robustezza): è piccolo, non cambia un nodo, e toglie di mezzo un
   piantamento possibile prima ancora di misurare.
2. Correggere il **punto 1** (fedeltà): `increaseDepth` condiviso come nella fonte.
3. Solo dopo, **misurare**: nodi/secondo e profondità a 1/2/4/8 thread, e il conteggio dei nodi a
   `Threads=1` che deve restare **2.520.660** dopo entrambe le correzioni.
4. Estendere il banco degli invarianti al caso multi-thread prima di rimettere il bot a più di 1.

---

# COME E' FINITA — 2026-09-14

L'utente ha dato il via. Eseguito l'ordine dei lavori qui sopra, per intero.

## Le correzioni (commit 4dad89a)

**Divario 2, robustezza — FATTO.** Il corpo del task del thread principale e' in `try/finally`, cosi'
`AlzaStop()` e `Cancel()` avvengono anche se `Search_` esce per eccezione. Non cambia un nodo.

**Divario 1, fedelta' — FATTO.** `SegnaleStop` e' diventato `SegnaliPool` e tiene i due flag come la
fonte li dichiara, sulla stessa riga (`std::atomic_bool stop, increaseDepth;`, thread.h:157),
azzerandoli insieme come `start_thinking` (thread.cpp:304-307). Ora `increaseDepth` lo scrive il solo
principale (search.cpp:613, dentro il blocco a lui riservato — da noi il blocco `optimumMs < NoBound`,
che gli helper saltano) e lo leggono tutti (search.cpp:356).

**Divario 3, contatori — FATTO anche questo**, che era dato per "oggi non morde": le righe `info`
riportano ora `nodes`/`tbhits` di tutto il pool (`output_pv`, search.cpp:2278 e 2282), agganciati al
solo thread principale che e' l'unico che stampa. `nodesEffort` resta sul contatore proprio, come
search.cpp:571.

**Rete di sicurezza**: `bench 16 1 13` = **2.520.660**, identico al nodo dopo tutte e tre. 151/151 test.

## Le misure (tools/misura_thread.py, nuovo)

Scalabilita' grezza, `bench 16 N 13`: **600k / 1,10M / 2,01M / 3,60M nodi al secondo** con 1/2/4/8
thread, cioe' **6,0x a otto thread**. Il parallelismo grezzo c'e' e sta dove ce lo aspettavamo.

Profondita' raggiunta a **orologio vero 10+5** (serve l'orologio vero: a `go movetime` la gestione
tempo e' spenta e `increaseDepth` resterebbe sempre `true`), 8 posizioni di mediogioco, colonne
appaiate posizione per posizione e ordine invertito a posizioni alterne:

| thread | profondita' media | tempo medio | direzione contro 1 thread |
|---|---|---|---|
| 1 | 30,75 | 35,7 s | — |
| 2 | 29,12 | 34,4 s | meglio in 1 su 8, peggio in 5 |
| 4 | 29,12 | 28,5 s | meglio in 0 su 8, peggio in 5 |
| 8 | 29,75 | 34,6 s | meglio in 1 su 8, peggio in 5 |

**Il "~1,5 ply di costo dal restare a 1 thread" scritto in cima a questo documento NON si riproduce.**
Va considerato non dimostrato finche' non si ritrova il metodo con cui era stato ottenuto, che qui
non era annotato — e quella riga era anche una delle due ragioni per cui si riapriva la questione.

**Ma la misura non basta nemmeno a dire il contrario**, ed e' la cosa piu' importante di questa
sezione: lo stesso identico binario a 8 thread, sulle stesse 8 posizioni e lo stesso orologio, ha
dato **29,75** in un giro e **30,62** in quello dopo. Uno scarto di 0,9 ply fra due ripetizioni
identiche e' grande quanto l'effetto di cui si discute: **con 8 posizioni e un campione ciascuna,
questa metrica non risolve ne' 1,5 ply ne' mezzo ply, in nessuna delle due direzioni.** Serve piu'
campione, oppure — meglio — lo scontro diretto.

E la profondita' comunque non e' la metrica in cui il Lazy SMP guadagna: era gia' scritto in memoria
il 2026-09-07 ("tempo per raggiungere una profondita' fissa NON misura il Lazy SMP", misurato anche
sull'oracolo). Qui si e' girata la metrica (orologio fisso, si guarda dove si arriva) ma resta un
proxy: il Lazy SMP paga soprattutto in QUALITA' della mossa a pari profondita'.

## Prima contro dopo, appaiato, a 8 thread

Le stesse 8 posizioni, binario corretto contro una worktree su 428e0f0, colonne appaiate:

- profondita' media **30,62 contro 30,25**, meglio in 4 posizioni su 8 e peggio in 3: **nessuna
  differenza misurabile**. Le due correzioni sono fedelta' e robustezza, non forza — e questo e'
  esattamente cio' che ci si aspettava dal divario 1, che morde solo quando il tempo stringe.
- nodi al secondo riportati nelle righe `info`: **2,04M contro 281k**. Non e' un guadagno di
  velocita': e' la **prova diretta che il divario 3 c'era davvero** e ora e' chiuso. Il binario
  vecchio riportava i nodi del solo worker principale, cioe' circa un settimo del vero.

## Il banco degli invarianti, esteso (punto 4)

`tools/invarianti_orologio.py` ripete ora orologi limite, "stop" a meta' ricerca e pondering **a 1 e
a 8 thread**; le partite simulate girano a 1, 4 e 8. Prima quei tre percorsi giravano sempre a un
thread solo — ed erano proprio quelli dove vive il rischio, perche' li' la ricerca deve fermarsi su
comando e gli helper hanno come unica uscita il flag condiviso.

**Esito: nessuna violazione**, a 1 e a 8 thread. A otto thread lo "stop" a meta' ricerca produce il
bestmove in 0,01 s tutte e tre le volte, e le tre sequenze di pondering (ricerca esaurita +
ponderhit, ponder 3 s + ponderhit, ponder 2 s + stop) rispondono come a un thread. Le tre partite
simulate girano a 1, 4 e 8 thread.

Trovato e corretto strada facendo un difetto **del banco**, non del motore: `prova_stop` leggeva il
`bestmove` con `readline()` diretta sulla pipe mentre il lettore di stdout gira in un suo thread —
due lettori sulla stessa pipe, esattamente la trappola descritta in testa alla classe `Motore`. A un
thread passava lo stesso; a otto avrebbe prodotto un finto piantamento.

## Cosa resta prima di rimettere il bot sopra 1 thread

1. **Lo scontro diretto 1 thread contro 2** e' l'unica prova che decide. Il punteggio di Lichess non
   separa i motori ([[lichess-punteggio-non-separa-i-motori]]), quindi va giocato offline.
2. La **riproducibilita'** resta la ragione seria per stare a 1 thread, e non e' toccata da niente di
   quanto sopra: a piu' thread un errore non si riproduce, e la mossa persa contro pawn_git (z9HiBxcS,
   mossa 58) non e' mai stata spiegata.
3. Ancora non ispezionati: la TT sotto concorrenza (larghezza di `TTEntry`, `generation8`) e un modo
   deterministico a piu' thread da usare nei test.

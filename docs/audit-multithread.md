# Audit del percorso MULTI-THREAD

Iniziato il 2026-09-11 su richiesta dell'utente. È la parte meno ispezionata del porting, ed è il
motivo per cui il bot gira a `Threads: 1` dall'8 settembre.

**Perché si riapre adesso.** Il 10 settembre abbiamo battuto eigenmann-chess (2697) a 15+10: un C++
scritto da zero che dichiara **+69 Elo dal solo secondo thread di ricerca**. È un vantaggio che gli
avversari di quella fascia hanno e noi no. Il costo misurato del restare a 1 thread è **~1,5 ply di
profondità media** (18,33 contro 19,85 a 8 thread).

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

# Audit di fedelta' totale: sorgente dell'oracolo contro questo porting

**Stato: APERTO — lavoro pluri-sessione. Questo documento e' il punto di ripresa.**

## Perche' esiste

Il 2026-09-07 sono emerse, una dopo l'altra, **dieci** discrepanze reali in codice che il piano
dichiarava "portato fedelmente". Tre erano di CONVENZIONE (dimensione del cluster TT,
normalizzazione del punteggio UCI, semantica del contatore nodi), sei erano pezzi di logica **mai
portati**, e una era una riga presente ma nel **punto sbagliato**.

L'ultima trovata — `depth = std::min(depth, MAX_PLY - 1);` (search.cpp:733) — e' quella che ha
motivato questo audit: non contiene nessuna costante a piu' cifre, quindi l'audit basato sulle
costanti non poteva vederla. Da qui la richiesta dell'utente: **ispezione totale da zero, riga per
riga, dei due codici**.

Principio guida, posto dall'utente: *"a correttezza di codice dovremmo comportarci come l'oracolo,
al limite piu' lenti o meno forti"*. Una differenza QUALITATIVA (un blocco contro 1,8 secondi) non
si spiega con "siamo piu' lenti": e' un difetto.

## Metodo

Tre livelli, in ordine di costo crescente. Nessuno da solo basta — l'hanno dimostrato i fatti.

1. **Costanti** (`tools/audit_fedelta.py` in una versione precedente): ogni costante numerica della
   fonte deve comparire nel porting. Ha trovato 3 pezzi. **Cieco** alle istruzioni senza costanti.
2. **Istruzioni** (`tools/audit_fedelta.py`, attuale): per ogni riga di logica della fonte si
   costruisce una firma di token e si cerca la riga nostra piu' simile. Produce una checklist di
   candidati, con molti falsi positivi: **e' una lista da vagliare, non un verdetto**.
3. **ORDINE**: presenza non basta. Lo Step 6 era completo ma collocato prima dello Step 5 invece che
   dopo, e leggeva quindi una `depth` non ancora aggiustata dall'hindsight. Lo strumento segnala i
   candidati "in disordine"; vanno confrontati a mano con la sequenza della fonte.

Regola operativa: **ogni riga vagliata va annotata qui sotto**, con l'esito, cosi' le sessioni
successive non la riesaminino da capo. Un audit che si ripete da zero ogni volta non converge.

## Uso dello strumento

    python tools/audit_fedelta.py search.cpp
    python tools/audit_fedelta.py --tutti

L'intervallo di righe esaminato per file sta in `INTERVALLI` dentro lo script (per `search.cpp` e'
attualmente il corpo di `search()`, righe 715-1660). La tabella `RINOMINA` traduce gli
identificatori C++ nei nostri: **va arricchita man mano**, perche' ogni voce mancante genera falsi
positivi.

## Gia' VERIFICATO — non ricontrollare

Tutto quanto segue e' stato confrontato riga per riga contro la fonte, con misura a supporto dove
indicato. Elenco cumulativo: aggiungere qui, non rifare.

### Verificato fedele
- **Valutazione**: stessa rete, `eval` combacia; e i punteggi a profondita' 1 combaciano
  ESATTAMENTE con l'oracolo su tutte le posizioni provate (829/829, -139/-139, 81/81, 49/49,
  143/143). Questo convalida insieme valutazione e quiescenza.
- **Quiescenza**: idem (portata fedelmente il 2026-09-07, 10 passi).
- **Generazione mosse**: portato `perft` con la ripartizione per mossa ("divide") e confrontato
  l'elenco COMPLETO su 3 posizioni: stesso ordine e stessi conteggi, mossa per mossa (48, 14, 40).
- **Step 1, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24**:
  ispezionati riga per riga il 2026-09-07 (le discrepanze trovate sono state corrette, vedi sotto).
- **Step 16 in particolare**: tutti i margini, i termini `ply > rootDepth`, `ttMoveHistory`,
  `depth++`, `is_shuffling`, l'intera condizione d'ingresso e la chiamata ricorsiva.
- **Ciclo di aspirazione** (search.cpp:375-441): fedele, incluso `failedHighCnt`, `adjustedDepth`,
  la crescita di `delta` e l'ordine degli aggiornamenti di finestra.
- **`update_all_stats`** e il suo chiamante (Step 23): riletti per intero, ora completi.
- **`correction_value`**: tutte e 5 le tabelle, formula e costanti identiche.
- **Codifica delle profondita' in TT**: `DEPTH_NONE = -3` in entrambi, stesse formule di save/read.
- **Soglie di `partial_insertion_sort`**: `int.MinValue` e `-3560 * depth`.
- **Inizializzazione dello stack pre-radice**: `_staticEvalHistory[0..6] = Values.None`.
- **`requires_refresh` NNUE** e `find_last_usable_accumulator`: fedeli, e sullo stesso feature set
  (`PSQFeatureSet = Features::HalfKAv2_hm`).
- **Audit delle costanti**: su `search.cpp` resta una sola costante non trovata ed e' un falso
  positivo (`100000UL`). `movepick.cpp`, `history.h`, `timeman.cpp`, `evaluate.cpp`: zero mancanti.

### Discrepanze TROVATE E CORRETTE il 2026-09-07
| dove | cosa | commit |
|---|---|---|
| tt.cpp:170-184 | cluster da 32 byte (3 entry + 2 di padding), non 30 | 6bcca3b |
| uci.cpp:585 | `to_cp`: punteggio UCI non normalizzato, e nessun `score mate N` | 6bcca3b |
| search.cpp:658 | contatore nodi: conta le MOSSE, non le invocazioni | f68e504 |
| search.cpp:1533 | Step 22, `depth -= 3` dopo un miglioramento di alpha | 03a92fc |
| search.cpp:872 | Step 6 era un abbozzo di 5 righe contro 50, **e nel posto sbagliato** | 03a92fc |
| search.cpp:1386 | Step 18, ri-ricerca dopo LMR a finestra piena invece che nulla | 03a92fc |
| search.cpp:1383 | Step 18, `newDepth` non mutato | 03a92fc |
| search.cpp:1390 | Step 18, bonus 1334 "post LMR continuation history" | 03a92fc |
| search.cpp:797 | Step 3, mate distance pruning: bound sbagliato e applicato alla radice | 03a92fc |
| search.cpp:778,809 | Step 1, `ss->moveCount` e `ss->statScore` mai azzerati | 03a92fc |
| search.cpp:824 | Step 4, `ttCapture` da `capture` invece che `capture_stage` | 03a92fc |
| search.cpp:822 | Step 4, `ss->ttPv` non ereditata con `excludedMove` | 03a92fc |
| search.cpp:376 | Lazy SMP: `threadIdx` inesistente, nessuna diversita' fra thread | 03a92fc |
| search.cpp:674-679 | `do_null_move` non aggiornava lo Stack | d5a2fd6 |
| history (letture) | contHist filtrate come le scritture: 0 invece di -586 | d5a2fd6 |
| search.cpp:328-330 | decadimento 729/1024 della main history a ogni ricerca | 1c4f323 |
| search.cpp:2000 | Step 23, malus "quiet early move refuted" | 1c4f323 |
| search.cpp:1976 | Step 23, `+ (ss-1)->statScore / 28` nel bonus | 1c4f323 |
| search.cpp:1614 | propagazione di `ttPv` sul fail-low | efc715e |
| search.cpp:733 | `depth = min(depth, MAX_PLY - 1)` | d1792ff |

## APERTO — da riprendere qui

### 1. Blocco del bench multi-thread — RISOLTO il 2026-09-08 (commit 844c691)

**Causa**: `ComputeStatScore` leggeva la posizione GIA' MOSSA. Vedi in fondo a questa sezione.
Dieci esecuzioni su dieci di `bench 16 8 13` completano ora in 8-9 secondi. Quanto segue e' la
cronaca dell'indagine, tenuta perche' il METODO e' riusabile.

#### Cronaca (storica)

**Sintomo**: `bench 16 8 13` non termina. L'oracolo fa lo stesso bench in **1,8 s / 17,6M nodi**.
Riproducibile: **9 blocchi su 12** a 8 thread; **mai** a thread singolo (decine di prove).

**Stabilito con misure**:
- E' nello **Step 16**: disattivandolo, 6/6 completano in 4-5 s contro 12-15 s.
- La ricorsione arriva a **ply 244-246** (il muro di `MaxPly`), guidata dalle estensioni triple:
  a 1 thread `plyMax=44`, a 8 thread bloccato `plyMax=244`, `ext3` da 23mila a 716mila.
- Il thread principale resta dentro l'iterazione a profondita' 13 con i nodi che salgono oltre 27
  milioni (una posizione di bench ne vuole ~50mila).
- Gli helper NON sono profondissimi (13-17): l'ipotesi "TT inquinata da helper a profondita' 40-50"
  e' **smentita**.
- Portare `depth = min(depth, MAX_PLY - 1)` **non** lo risolve (3/6 -> 5/8, dentro il rumore).
- **Non tocca le partite**: sotto controllo di tempo il `seldepth` e' normale (43/45/31/35 a 8
  thread, contro 60/57/22/43 dell'oracolo), 54 posizioni x 8 thread x 2 repliche senza un blocco,
  e il caso peggiore usa il 13,9% dell'orologio.

**Indagine del 2026-09-07 notte — la fuga NON e' della profondita', e' del PLY.**

Misurato con contatori interni (variabile d'ambiente `SFS_DIAG_CHAIN=1`, stampa periodica durante
lo stallo):

| | 1 thread | 8 thread, bloccato |
|---|---|---|
| `plyMax` (ricorsione di Negamax) | 44 | **132-154** |
| `depthMax` | 27 | **28-34** |
| ply alla profondita' massima | 17 | 21-23 |

**La profondita' non scappa affatto**: resta a ~30 in entrambi i casi. E' il PLY ad arrivare a 132.
Per chiamare `Negamax` a ply 132 serve che `depth` resti >= 1 per 132 ply partendo da 13, cioe' che
venga ripristinata di continuo. Contati i quattro meccanismi che possono farlo, **sopra ply 60**:

| meccanismo | 1 thread | 8 thread bloccato |
|---|---|---|
| `Math.Max(1, ...)` dello Step 18 (LMR) | 0 | **1.813.388 e in crescita** |
| `depth++` dell'hindsight | 0 | 2.331 |
| `Math.Max(newDepth, 1)` dello Step 20 | 0 | 0 |
| estensioni | 0 | 0 |

**E' il clamp dell'LMR**: `d = std::max(1, std::min(newDepth - r / 1024, newDepth + 2)) + PvNode`
impedisce alla ricerca ridotta di scendere a zero e la forza a profondita' 1 invece di mandarla in
quiescenza. Milioni di volte, contro ZERO a thread singolo.

Ambiente in cui accade, sempre misurato sopra ply 60:

    nodi visitati = 167.560.575
    di cui patte  =  87.640.122  (52%)
    con r50 > 99  =  84.849.809  (51%)
    Repetition!=0 =   7.027.344  (4%)

Cioe': il motore costruisce un albero da centinaia di milioni di nodi in una regione dove META' delle
posizioni e' gia' patta per la regola delle 50 mosse. Le foglie ritornano subito, ma sono tantissime.
La `r50` osservata sui nodi campionati a ply 101 era 54-56: linee di puro rimescolamento.

**Verificato fedele (non e' qui il bug)**: il clamp dello Step 18, `Position::is_draw`,
`Position::is_repetition`, il calcolo di `st->repetition` in `do_move` (risalita a due ply per volta
fino a `min(rule50, pliesFromNull)`), `is_shuffling`, e l'intero Step 16.

**PROSSIMO PASSO**: la regione problematica si forma fra ply ~23 (dove la profondita' tocca il
massimo) e ply 60 (dove iniziano i contatori attuali). **Ripetere la stessa misura con soglia
`ply > 30` invece di `ply > 60`** per vedere quale meccanismo alimenta la catena in quel tratto: se
di nuovo il clamp dell'LMR, allora la domanda diventa perche' `r / 1024` sia cosi' grande da portare
`newDepth - r/1024` sotto 1 in modo sistematico solo con piu' thread — e li' l'unico ingresso
condiviso e' la transposition table (via `ttPv`, `probe.Data.Depth`, `ttCapture`, `cutoffCnt`, che
entrano tutti nella formula di `r`).

#### LA CAUSA (trovata il 2026-09-08)

Scomponendo `r` nei suoi termini sopra ply 30:

| | 1 thread | 8 thread bloccato |
|---|---|---|
| `r` medio | **+3567** | **-2684** |
| `r` base (`Reduction(...)`) | 2405 | 2162 |
| **`statScore` medio** | **5.425** | **40.091** |
| `ttPv` | 10% | 0% |

`r` NEGATIVO ribalta il significato dell'LMR: in `max(1, min(newDepth - r/1024, newDepth + 2))`, con
`r/1024 = -3` il minimo diventa `newDepth + 2`, quindi la ricerca "ridotta" **estende di 2 invece di
ridurre**. Da qui il ply a 132-154 e l'istogramma che CRESCE con la profondita'.

E a spingere `r` sotto zero e' `statScore` (`r -= statScore * 439 / 4096`: con 40.091 fa -4.297).

**Perche' `statScore` era gonfiato**: `ComputeStatScore` e' chiamata allo Step 18, cioe' DOPO
`DoMove` (come nella fonte), ma leggeva tutto dalla posizione gia' mossa. La fonte usa valori
catturati PRIMA — `movedPiece` (Step 14), `us` (Step 1), `pos.captured_piece()` (dallo StateInfo).
Tre valori, tutti e tre sbagliati:

- **pezzo catturato**: `PieceOn(m.ToSq)` dopo la mossa contiene il pezzo che si e' MOSSO. Per una
  cattura fatta di donna dava `873*2538/128 = 17.309` invece del valore del pezzo preso.
- **pezzo mosso**: `MovedPiece(m)` legge `PieceOn(m.FromSq)`, dopo la mossa VUOTA: restituiva
  `Piece.None`, con cui si indicizzavano capture history e continuation history.
- **colore**: `pos.SideToMove` dopo la mossa e' l'AVVERSARIO: la main history veniva letta dal lato
  sbagliato.

**Metodo che ha funzionato, da riusare**: contatori interni letti DURANTE lo stallo (stampa
periodica su stderr da un task di sfondo, attivata da variabile d'ambiente), con una domanda
diversa a ogni giro — prima l'istogramma dei ply, poi il valore medio di `r`, infine la
scomposizione di `r` nei suoi termini. Ogni misura ha smentito l'ipotesi precedente: prima "sono gli
helper troppo profondi" (falso: sono a 13-17), poi "e' la profondita' che scappa" (falso: resta a
~30, e' il PLY a scappare), infine il dato giusto. Tre ipotesi plausibili scartate da tre misure.

**Nota a margine trovata durante l'indagine**: `Position.IsDraw` alloca un `new List<Move>()` a ogni
chiamata con `rule50 > 99` e re sotto scacco. Su questo percorso e' caldissimo (84 milioni di nodi
con r50 > 99 in una singola esecuzione). Non e' la causa del blocco, ma va convertito a buffer.

### 2. Divergenza di punteggio a profondita' medie

I punteggi combaciano ESATTAMENTE a profondita' 1 e divergono a profondita' variabile secondo la
posizione (d2, d3, d5, d8, d9, o mai). Dopo le correzioni del 2026-09-07 la soglia si e' spostata
piu' in fondo (posizione "tattica" da d2 a d9, esatta fino a d5). Reproducer piu' piccolo:
`r1bbk1nr/pp3p1p/2n5/1N4p1/2Np1B2/8/PPP2PPP/2KR1B1R w kq - 0 13`, che diverge gia' a **profondita'
2** pur essendo esatto a profondita' 1: a quella profondita' l'albero e' abbastanza piccolo da
confrontarlo nodo per nodo.

### 3. Checklist dello strumento, da vagliare

`python tools/audit_fedelta.py search.cpp` produce ~81 candidati sul corpo di `search()`. Molti sono
falsi positivi (codice presente ma scritto diversamente). **Vagliarli in blocchi e annotare qui
l'esito di ciascuno**, arricchendo `RINOMINA` man mano per abbassare il rumore.

Da estendere poi a: `movepick.cpp`, `history.h`, `position.cpp`, `tt.cpp`, `movegen.cpp`,
`timeman.cpp`, `nnue/*`.

### 4. Pezzi noti non portati (scelte dichiarate, non dimenticanze)

- `nnue_accumulator.cpp`: 953 righe contro le nostre 411 — Finny Tables, aggiornamento ibrido,
  backward update. **Verificato neutro sui valori** (vedi porting-master-plan.md), sono percorsi
  alternativi allo stesso accumulatore.
- MultiPV, Skill Level, UCI_Elo, filtro `searchmoves`: dichiarati inerti.
- Infrastruttura NUMA, huge pages, thread nativi: gestita dal runtime .NET.

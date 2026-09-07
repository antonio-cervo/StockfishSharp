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

### 1. Blocco del bench multi-thread (priorita' alta)

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

**Prossimo passo suggerito**: registrare, quando la fuga parte, i valori esatti che entrano nello
Step 16 lungo la catena (`ttScore`, `probe.Data.Depth`, `depth`, `singularBeta`, `singularScore`) e
confrontarli con gli stessi a thread singolo, per isolare quale ingresso assume un valore che a
thread singolo non assume mai. L'unico stato condiviso e' la transposition table.

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

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

4. **SEMANTICA TEMPORALE** — *quando* un valore viene letto rispetto alle mutazioni di stato. E' la
   classe che l'audit testuale NON puo' vedere: la riga esiste e combacia con la fonte, sbaglia solo
   il momento. Ha prodotto due bug su due tentativi:
   - `ComputeStatScore` leggeva pezzo catturato, pezzo mosso e colore DOPO `DoMove` (statScore
     gonfiato 7 volte, riduzione LMR negativa, bench multi-thread bloccato);
   - il bonus post-LMR passava `pos.MovedPiece(m)` dopo la mossa, ottenendo `Piece.None` e scrivendo
     nel piano `NO_PIECE` della continuation history — quello che va solo LETTO come valore di
     riserva.

   **Come si applica**: marcare ogni riga come PRE o POST rispetto a `DoMove`/`DoNullMove`, poi
   verificare che ogni chiamata POST riceva i valori pre-mossa come PARAMETRI (nella fonte sono
   `movedPiece`, `us`, `pos.captured_piece()`, catturati agli Step 1 e 14). Fatto su `Search.cs`:
   restano solo due chiamate POST, entrambe ora corrette. **Da rifare su ogni file che muta stato.**

5. **ASSUNZIONI DICHIARATE NEI COMMENTI** — ogni "sempre / mai / per costruzione / semplificazione /
   non serve" e' una deviazione auto-dichiarata, quindi un candidato. E i commenti INVECCHIANO: due
   ne ho trovati che affermavano il falso perche' il codice era cambiato dopo.

   Ha prodotto il bug delle guardie del voto: un commento sosteneva che `IsInexact` fosse "sempre
   falso per costruzione". Vero per il thread principale, **falso per gli helper**, cancellati a
   meta' iterazione — cioe' esattamente il caso che il commento della FONTE descrive ("Aborted (d1)
   searches may lead to inexact win (or loss) scores").

   **Come si applica**: `grep` delle formule assolute nei commenti, poi verifica di ciascuna contro
   il codice ATTUALE. Sono 55 in tutto il repo; vagliate finora quelle di `SearchThreadPool.cs`,
   `RootMove.cs` e la testata di `Search.cs`.

Regola operativa: **ogni riga vagliata va annotata qui sotto**, con l'esito, cosi' le sessioni
successive non la riesaminino da capo. Un audit che si ripete da zero ogni volta non converge.

## RISOLTA: il segnaposto della valutazione in TT usava il sentinella sbagliato

**Caso di partenza**: `8/3k4/8/8/8/4B3/4KB2/2B5 w - - 0 1` a profondita' 2, 134 nodi contro 135.

**Catena, ricostruita con i due motori strumentati e affiancati** — e' il modello di come si procede:
1. le tracce di radice combaciano per 52 righe su 53; prima divergenza alla mossa #11 `e3g5`, con
   `alpha`, `beta`, `r` e `newDepth` IDENTICI ma punteggio 922 contro 909;
2. scendendo a ply 2, al nodo dopo `e3g5 d7d6` la nostra prima mossa e' `e2d2`, quella dell'oracolo
   `g5f4` (che da' scacco), **senza mossa di TT da entrambe le parti**: e' ordinamento;
3. confrontati i punteggi di TUTTE le mosse quiete di quel nodo: **358 righe, 3 diverse, in un solo
   campo** — la pawn history (`main`, continuation e bonus scacco combaciano);
4. tracciate le SCRITTURE su quella voce: **noi 12, l'oracolo 10**, con i bonus uguali uno a uno
   fino al settimo;
5. tracciate le condizioni d'ingresso del bonus in eccesso: **82 righe, una sola davvero diversa**,
   ed era `ttHit=0` da noi contro `ttHit=1` da loro.

**CAUSA** (search.cpp:853): l'entry con la sola valutazione statica va scritta con
`DEPTH_UNSEARCHED` (-2), noi usavamo `DepthNone` (-3). I due sentinella non sono intercambiabili e
types.h:235-241 lo dice: DEPTH_NONE serve al **controllo di occupazione**, DEPTH_UNSEARCHED alle
entry scritte senza aver cercato. Con DepthNone il campo `depth8` diventa 0, cioe'
`is_occupied() == false`: **l'entry c'e' ma ogni sonda successiva la manca.**

Due conseguenze, una di correttezza e una di velocita':
- la valutazione statica salvata non veniva mai riusata (rete NNUE rivalutata da capo ogni volta);
- il bonus di pawn history protetto da `!ttHit` veniva applicato una volta di troppo, cambiando
  l'ordinamento delle mosse quiete e quindi l'albero.

**EFFETTO**

| | prima | dopo |
|---|---|---|
| bench, nodi | 2.211.774 | **2.335.349** (oracolo 2.497.913) |
| **nodi/secondo** | 374.000 | **474.567 (+27%)** |
| stesso n. di nodi a profondita' 2 | 45/51 | **47/51** |
| stesso n. di nodi a profondita' 3 | 27/51 | **40/51** |
| stessa mossa a profondita' 3 | 94,1% | **100,0% (51/51)** |
| scarto mediano di punteggio a d6 | 9 cp | **1 cp** |

Due riproduttori storici sono ora **bit-identici all'oracolo** a profondita' 2, 3 e 6:
`8/3k4/8/8/8/4B3/4KB2/2B5 w` (135/272/1198) e `8/8/1P6/5pr1/8/1R6/7k/2K5 b` (378/466/1455).

**Verificati fedeli lungo questo percorso, NON ricontrollare**: i tre siti che scrivono la pawn
history con le loro costanti; il `bonusScale` del countermove; `update_quiet_histories`;
`set_check_info`; `score<QUIETS>` col bonus scacco; `TranspositionTable::probe`; `Zobrist::noPawns`;
il `bonusScale` di search.cpp:1580-1592.

## RISOLTA anche la seconda: il ProbCut non registrava la mossa nello stack

**Caso**: `1r3k2/4q3/2Pp3b/3Bp3/2Q2p2/1p1P2P1/1P2KP2/3N4 w - - 0 1` a profondita' 3, 342 nodi
contro 343 — **un solo nodo**.

**Catena**: 296 righe di traccia, **UNA sola diversa** — l'oracolo cerca in quiescenza la cattura
`f2e3`, noi no. Ingresso in quiescenza IDENTICO in tutto (`alpha=818 beta=819 best=-33 statico=-33
fbase=273 ttHit=0`), quindi la differenza e' nel ciclo mosse. Tracciata la generazione: **la mossa
c'e' in entrambi, ma il nostro `prevSq` vale g3 dove la fonte ha e3**. `f2e3` e' una RIPRESA su e3,
e con `prevSq` corretto la fonte la esenta dalla potatura di futility (search.cpp:1791,
`move.to_sq() != prevSq`); col nostro sbagliato la potavamo.

**CAUSA**: search.cpp:1074 usa `do_move(pos, move, st, ss)`, il do_move del **Worker**, che oltre a
muovere REGISTRA la mossa (`ss->currentMove` e i puntatori di continuation history scelti da
`[inCheck][capture][pezzo][casa]`, search.cpp:655-671). Il nostro ProbCut chiamava il `DoMove` nudo
di `Position`: **tutto il suo sottoalbero girava col contesto rimasto dalla mossa precedente.**

**Corretto nello stesso passaggio un errore d'ORDINE**: tuffo in quiescenza, tetto alla profondita'
e controllo di ripetizione imminente vanno PRIMA di Step 1/2/3 (search.cpp:729-742). Con l'ordine
sbagliato lo Step 3 restringeva alpha/beta e alla quiescenza arrivava una finestra piu' stretta
(beta 31998 invece di 32001 gia' al primo nodo).

**RISULTATO: parita' di nodi PIENA a profondita' 2 e 3** (49/49; le due che il tabulato segna sono
posizioni di matto/stallo dove l'oracolo non stampa la riga info — artefatto dello strumento).

| stesso numero di nodi | prima | dopo |
|---|---|---|
| profondita' 2 | 47/51 | **49/51 (= tutte)** |
| profondita' 3 | 40/51 | **49/51 (= tutte)** |

**Frontiera successiva: profondita' 4**, dove restano 5 divergenze vere su 51. La piu' piccola e'
`4k2r/1pb2ppp/1p2p3/1R1p4/3P4/2r1PN2/P4PPP/1R4K1 b - - 3 22` (591 contro 594); la piu' anomala e'
`4k3/3q1r2/1N2r1b1/3ppN2/2nPP3/1B1R2n1/2R1Q3/3K4 w - - 5 1` (1.821 contro 1.050, l'unica dove ne
usiamo molti di piu').

## RISOLTA: lo slot `currentMove` sovrascritto dalla ricerca singolare

Trovata subito dopo `PvNode`, sulla divergenza a profondita' 4 successiva per taglia:
`1r6/1P4bk/3qr1p1/N6p/3pp2P/6R1/3Q1PP1/1R4K1 w - - 1 42`, **1.093 nodi contro 1.105**.

Nella fonte i campi di Stack che descrivono la mossa appena giocata li scrive
`Search::Worker::do_move` (search.cpp:663-671), cioe' allo **Step 17**:

```cpp
ss->currentMove         = move;
ss->continuationHistory = &continuationHistory[ss->inCheck][capture][dirtyPiece.pc][move.to_sq()];
```

Da noi stavano in cima al ciclo mosse, **prima dello Step 16** (Singular Extensions). E la ricerca
singolare gira sullo STESSO ply — `search<NonPV>(pos, ss, singularBeta - 1, singularBeta, ...)`,
search.cpp:1254 — quindi il suo ciclo mosse riscriveva quegli slot con le proprie mosse candidate, e
nessuno li ripristinava. Il figlio della mossa vera leggeva quindi come `(ss-1)->currentMove`
l'ultima candidata della ricerca singolare invece della mossa appena giocata, sbagliando: main
history (search.cpp:982 e 1597), continuation history e continuation correction history.

**Come si e' visto**: la traccia MH stampa ogni scrittura di main history col sito della fonte. Allo
stesso identico nodo (`nodi=499 ply=2 d=2 se=-205 se1=243`, bonus identico) la fonte scriveva sulla
casella di `d6a6` e noi su quella di `d6c7`. Nessuna inferenza: la riga nomina la mossa.

Correzione: le quattro assegnazioni si sono spostate dove la fonte le fa, subito prima di `DoMove`
(verificato che nessuno le legge fra la cima del ciclo e li').

## RISOLTA nello stesso caso: `ss->ttPv` trattato come variabile locale

Con i nodi ormai identici (1.105 = 1.105) restava **1 cp** di scarto. La traccia PLY mostrava un
solo campo diverso: `r`, di **esattamente 3023** — la costante di search.cpp:1317,
`if (ss->ttPv) r -= 3023 + ...`. Cioe' la fonte applicava quel blocco e noi no.

`ss->ttPv` e' un campo dello **Stack**, non una variabile locale, e DUE ricerche girano sullo stesso
`ss` — la singolare (search.cpp:1254) e la verifica del null move (search.cpp:1037) — entrambe in
grado di riscriverlo tramite search.cpp:1617 (`if (value <= alpha) ss->ttPv |= (ss-1)->ttPv`). Da noi
era una copia locale: la scrittura delle ricerche annidate finiva nell'array ma il nodo esterno
continuava a leggere il valore vecchio, e lo Step 18 saltava una riduzione di oltre tre ply.

Correzione: `ref bool ttPv = ref _ttPvHistory[ply + StackOffset];` — la variabile **e'** la cella.

**Classe di errore, la stessa nei due casi**: un campo di `Stack` reso variabile locale. Vale la pena
cercarne altri: ogni volta che la fonte scrive `ss->qualcosa` e noi teniamo una copia, una ricerca
annidata sullo stesso ply puo' divergere in silenzio.

## RISOLTA, la piu' grossa finora: `PvNode` era DEDOTTO dalla finestra invece che propagato

Trovata il 2026-09-08 sera partendo dalla divergenza a profondita' 4 piu' piccola che esistesse:
`4k2r/1pb2ppp/1p2p3/1R1p4/3P4/2r1PN2/P4PPP/1R4K1 b - - 3 22`, **591 nodi contro 594**.

Nella fonte il tipo di nodo e' un **parametro di template**:

```cpp
template<NodeType nodeType>
Value Search::Worker::search(...) {
    constexpr bool PvNode   = nodeType != NonPV;   // search.cpp:706
    constexpr bool rootNode = nodeType == Root;
```

e si propaga per STRUTTURA: `search<Root>` alla radice, `search<PV>` allo Step 20, `search<NonPV>`
in ogni ricerca a finestra nulla. Da noi era invece **dedotto dalla finestra**:

```csharp
bool isPvNode = beta - alpha > 1;   // <-- l'errore
```

Le due cose coincidono quasi sempre, ma **non alla radice quando la finestra di aspiration ha
larghezza 1** — situazione del tutto ordinaria: dopo un fail-high la fonte ricerca con
`alpha = max(beta - delta, alpha)`, e nel caso in esame la radice cercava con `alpha=257 beta=258`.
Li' la fonte resta in `search<Root>`, cioe' PV; noi diventavamo non-PV, e con noi **tutto il
sottoalbero**. Conseguenza diretta: spariva lo **Step 20** (search.cpp:1406-1423, "For PV nodes
only, do a full PV search on the first move or after a fail high"), e con esso la discesa PV che lo
accompagna. Nelle tracce affiancate si vedeva a occhio nudo: dentro `PLY0 d=3 mc=2 e8e7` l'oracolo
spendeva 27 nodi e noi 22, e i 5 nodi mancanti erano esattamente una catena
`PLY1 d=1 -> PLY2 d=1 -> PLY3 d=1` a finestra nulla che noi non facevamo mai.

`isPvNode` non serve solo allo Step 20: entra nel raffinamento LMR (`+ (isPvNode ? 1 : 0)`), in
`allNode`, nelle condizioni di taglio da TT, nel razoring, nella scelta fra `qsearch<PV>` e
`qsearch<NonPV>`. Era quindi una divergenza di comportamento diffusa, non un dettaglio di stampa.

**Correzione**: `isPvNode` e' ora un PARAMETRO di `Negamax`, passato esplicitamente in tutti e 9 i
punti di chiamata, ciascuno annotato con l'istanza di template della fonte che riproduce
(search.cpp:395, 1020, 1037, 1081, 1254, 1372, 1387, 1402, 1422). Nessuna deduzione dalla finestra
resta nel motore (verificato con `grep "beta - alpha > 1"`).

**Effetto misurato**: il caso riproduttore passa a 594 = 594; le divergenze reali di conteggio nodi a
profondita' 4 scendono da 5 a 3 su 49; bench 2.145.601 -> 2.117.244 nodi. 141/141 test verdi.

**Lezione, la stessa di sempre**: "presenza non e' fedelta'". Lo Step 20 c'era ed era trascritto
riga per riga; era la CONDIZIONE che lo governava a essere ricostruita per conto nostro invece che
trascritta. Un parametro di template non e' un dettaglio di linguaggio da tradurre "in modo
equivalente": e' un valore, e va fatto viaggiare come tale.

## RISOLTA: non emettevamo alcuna riga "info" per iterazione

Scoperta mentre si cercava a quale ITERAZIONE nascesse la divergenza sopra: il nostro motore
stampava **una sola** riga `info` alla fine dell'intera ricerca, mentre la fonte ne emette una per
ogni iterazione completata (`SearchManager::output_pv`, search.cpp:2273-2343, chiamata da
search.cpp:497). Due danni distinti:

- **di protocollo**: una GUI e lichess-bot non vedono alcun avanzamento durante una ricerca lunga;
- **di diagnosi**: senza le righe intermedie non si puo' stabilire a quale iterazione nasce una
  divergenza, che e' il primo passo da fare PRIMA di aprire le tracce riga per riga.

Portato fedelmente, insieme a `uciPvSent` (search.cpp:324/342/498/542) che governa se la riga
finale va ristampata, e al fatto che `extract_ponder_from_tt` ALLUNGA la PV e quindi obbliga a
ristamparla. Verifica: su
`4k2r/1pb2ppp/1p2p3/1R1p4/3P4/2r1PN2/P4PPP/1R4K1 b - - 3 22` a profondita' 4 le cinque righe emesse
(d1, d2, d3, d4, d4 col ponder dalla TT) coincidono ora **una per una** con quelle dell'oracolo.

Strumento nuovo: `tools/iterazioni.py "FEN" D` affianca le righe per iterazione. **E' il primo passo
del procedimento, prima di `confronta_traccia.py`.**

NON portato, assenza dichiarata: `syzygy_extend_pv` (search.cpp:2303), che allunga la PV mostrata
quando la radice e' in tablebase con punteggio decisivo — incide su quanto e' lunga la PV stampata,
mai sulla mossa scelta. Il `bench` non installa la callback (nessuna riga per iterazione li'),
differenza deliberata per non cambiare l'output che gli strumenti di misura analizzano.

## PUNTO DI RIPRESA per la prossima sessione

**Piano concordato con l'utente: instrumentare l'oracolo per scovare le cause delle divergenze
residue.** Lo strumento e' pronto e versionato: `tools/oracolo-traccia.patch` (istruzioni di build,
validazione obbligatoria e trappole in testa al file).

**Procedimento che ha funzionato, da ripetere in QUEST'ORDINE**:
1. `tools/nodi_bassa_profondita.py N` per trovare le posizioni che divergono alla profondita' piu'
   bassa possibile (il conteggio nodi e' il segnale piu' severo);
2. `tools/iterazioni.py FEN D` per stabilire a quale ITERAZIONE nasce la divergenza. Le iterazioni
   precedenti identiche in mossa, punteggio E nodi escludono meta' delle ipotesi in un colpo solo, e
   costano una riga di output invece di centinaia;
3. `tools/confronta_traccia.py FEN D PREFISSO` per affiancare le tracce interne, scendendo di
   livello (PLY -> QS -> QIN -> QGEN) finche' resta UNA riga diversa;
4. quella riga nomina il campo colpevole: si va alla riga corrispondente della fonte e si confronta.
   Non serve piu' formulare ipotesi.

**Stato al 2026-09-08 sera**, dopo `PvNode` + slot `currentMove` + `ss->ttPv` (51 posizioni; 2 sono
matto/stallo alla radice, dove l'oracolo non emette alcun conteggio: escluse dal denominatore, che
diventa 49 — prima erano contate come divergenze e facevano apparire un 96,1% dove la parita' era
piena):

| profondita' | conteggio nodi identico |
|---|---|
| 2 | 49/49 |
| 3 | 49/49 |
| 4 | 49/49 |
| 5 | 49/49 |
| 6 | 49/49 |
| 7 | 48/49 |
| 8 | 48/49 |
| 9 | 45/49 |

Bench: 2.145.601 -> 2.117.244 -> **2.304.916** nodi (oracolo 2.497.913). Il bench SALE avvicinandosi
alla fonte: e' il segno che l'albero si sta conformando, non che il motore peggiora.

**Da dove ripartire, in ordine di taglia**:
- `5rk1/q6p/2p3bR/1pPp1rP1/1P1Pp3/P3B1Q1/1K3P2/R7 w - - 93 90` — l'UNICA divergenza a profondita' 7
  (7.448 contro 7.237) e la piu' esplosiva a 8 e 9 (21.865 contro 9.538; 38.069 contro 13.350).
  **Ha `rule50 = 93`**: e' territorio di `adjust_key50` e di `value_from_tt(..., r50c)`. Da
  attaccare per prima, ed e' quasi certamente una causa unica.
- profondita' 9, le altre tre: `4r1k1/r1q2ppp/ppp2n2/4P3/5Rb1/1N1BQ3/PPP3PP/R5K1 w - - 1 17`
  (-131), `8/8/1P6/5pr1/8/4R3/7k/2K5 w - - 0 1` (-5.117),
  `8/R7/2q5/8/6k1/8/1P5p/K6R w - - 0 124` (+280).

Altri fili aperti, indipendenti: il finale `8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11`, dove il
rapporto di nodi esplode a 12-13x fra profondita' 11 e 13 per poi rientrare a 1,1x; e i 3 nodi di
quiescenza del punto 5 in fondo a questo documento.

**Ordine di priorita'**: sempre la divergenza piu' piccola alla profondita' piu' bassa. Ogni causa
trovata li' ne elimina molte a profondita' alta — la maschera sui bound, trovata su un caso da 57
nodi, porto' l'accordo a profondita' 3 dall'88,2% al 94,1%; `PvNode`, trovato su un caso da 594
nodi, ha chiuso l'intera profondita' 3 e due terzi della 4.

## LO STRUMENTO DECISIVO: compilare l'oracolo (2026-09-08 sera)

Sulla macchina c'e' **g++ 16.1.0 (MinGW-W64)**. L'oracolo si compila dal sorgente di riferimento e
si puo' STRUMENTARE. Questo chiude la stagione delle ipotesi: qualunque grandezza interna si puo'
stampare da entrambe le parti e confrontare.

    cd ProgettiVS && mkdir oracolo-build
    cp -r stockfish-upstream-reference/src stockfish-upstream-reference/scripts oracolo-build/
    cp StockfishSharp/nnue-networks/nn-1a298aa575a0.nnue oracolo-build/src/
    cd oracolo-build/src
    PATH=".../mingw64/bin:$PATH" mingw32-make -j8 build ARCH=x86-64-avx2 COMP=mingw

**Serve la cartella `scripts/`** accanto a `src/` (il Makefile chiama `../scripts/net.sh`).

**VALIDAZIONE OBBLIGATORIA, gia' fatta**: il binario compilato e' risultato bit-identico a quello
ufficiale (stessa mossa, punteggio e numero di nodi su 6 confronti a profondita' 2 e 6). E la
versione coincide: sorgente al tag `sf_19` (commit edb0d9d), binario che si annuncia "Stockfish 19".
Quindi tutti i confronti fatti finora erano validi.

**Trappola nel pilotare i due motori**: se la traccia va su `stderr` con `subprocess.PIPE` e si legge
solo `stdout`, il buffer di stderr si riempie e il processo figlio si BLOCCA — il `bestmove` non
arriva mai e il confronto resta appeso. Scrivere stderr su FILE.

**Trappola nell'instrumentare il nostro motore**: inserire una riga prima di un `return` che sta
sotto un `if` senza graffe rende il `return` INCONDIZIONATO. Usare invece un metodo
`Esci(etichetta, valore)` che registra e restituisce: e' sicuro qualunque sia la struttura.

**Trappola nel ricostruire**: `dotnet build` fallisce con `MSB3021` se un processo del motore tiene
il DLL. Filtrare gli errori con `grep "error CS"` NON lo vede e si finisce per misurare il binario
vecchio. Filtrare su `error|Errori`.

### Il primo bug trovato cosi': eval da TT con "==" invece della maschera

Strumentati entrambi i motori per stampare i componenti di `r` alla radice sulla stessa posizione,
e confrontati voce per voce: **tutto combaciava** (base, improving, delta, rootDelta, ttPv, cutNode,
ttCapture, allNode, correctionValue) **tranne `eval`: 35 da noi, 176 nell'oracolo**. I conti tornano
esattamente: `3 * clamp(176 - 35, -64, 96) = 288`, che era lo scarto misurato su `r`.

Causa: search.cpp:842-845 usa una MASCHERA (`ttData.bound & ...`), noi usavamo `==`. `BOUND_EXACT`
vale 3 = `UPPER|LOWER`, quindi con la maschera un'entry esatta soddisfa entrambi i casi e con
l'uguaglianza nessuno — e alla radice l'entry e' SEMPRE esatta.

Controllati poi tutti e 7 i punti in cui la fonte usa `bound &`: hanno tutti il corrispondente
corretto da noi. E i 5 punti in cui la fonte usa `==` sono `==` anche da noi.

Effetto: il riproduttore minimo `8/pp2r1k1/2p1p3/3pP2p/1P1P1P1P/P5KR/8/8 w - - 0 1` passa da 22 a
**57 nodi, identico all'oracolo**, a d2 e a d3. Stesso numero di nodi dell'oracolo a profondita' 2
su 51 posizioni: **da 36/51 a 45/51**.

### Metrica nuova: `tools/nodi_bassa_profondita.py`

Confronta i CONTEGGI NODI posizione per posizione a profondita' bassa. E' il segnale piu' severo:
due ricerche possono azzeccare la stessa mossa per caso, ma lo stesso numero di nodi significa quasi
certamente lo stesso albero. A profondita' 1 siamo a **51/51**; a profondita' 2 a 45/51 (era 36).

## Grado di fedelta' DI GIOCO (misure del 2026-09-08 sera, CORRETTE)

Non e' la fedelta' del codice: e' quanto il motore SCEGLIE le stesse mosse dell'oracolo, a parita'
di profondita' e a 1 thread per entrambi.

### PRIMA: tre difetti del MISURATORE, non del motore

I numeri di una prima versione di questa sezione erano tutti sbagliati. Le cause, tutte trovate
perche' l'utente ha fatto notare che a parita' di profondita' e senza fattore tempo ci si aspetta
coincidenza ESATTA:

1. **Il libro di aperture.** Sulle posizioni coperte il motore risponde senza cercare: si misurava
   il libro. La posizione iniziale risultava un disaccordo a ogni profondita' solo per questo.
   Rimedio: opzione UCI `OwnBook` (nostra, non della fonte) spenta nei test.
2. **Il dialogo UCI si desincronizzava** riusando un solo processo per tutte le posizioni: lo stesso
   confronto dava 50/53, 51/53 e 52/53 in tre esecuzioni. Verificato PRIMA che non fossero i motori:
   entrambi ripetono esattamente mossa, punteggio e nodi su 3 esecuzioni a profondita' 12. Rimedio:
   un processo nuovo per posizione.
3. **Le due posizioni Chess960** della lista bench venivano testate senza `UCI_Chess960`, quindi con
   diritti di arrocco interpretati diversamente. Escluse.

Piu' la normalizzazione `(none)` == `0000` (matto/stallo: formato del layer UCI, non gioco).

### La curva, dopo le correzioni (51 posizioni, 1 thread, libro spento, ripetibile)

| profondita' | stessa mossa | scarto di punteggio (mediana / peggiore) |
|---|---|---|
| 1 | **51/51 = 100,0%** | **0 cp / 0 cp** |
| 3 | **51/51 = 100,0%** | **0 cp / 0 cp** |
| 6 | 45/51 = 88,2% | 0 cp / 77 cp |
| 9 | 36/51 = 70,6% | 16 cp / 117 cp |
| 12 | 42/51 = 82,4% | 14 cp / 129 cp |

**A profondita' 1 siamo IDENTICI all'oracolo**: stessa mossa su tutte le 51 posizioni e scarto di
punteggio zero. Convalida insieme valutazione NNUE, quiescenza e generazione mosse. Da profondita' 3
in su compaiono divergenze vere: **e' li' che va concentrato l'audit.**

### A parita' di TEMPO (quello che conta in partita)

| | accordo | profondita' media |
|---|---|---|
| oracolo, 2 s | 49/51 = 96,1% | 32,5 |
| noi, 2 s | 41-42/51 = 80-82% | 19,3 |

### Perche' il divario a tempo e' maggiore di quello a profondita'

**Non e' il fattore di ramificazione**: a profondita' 13-16 nei mediogiochi usiamo un numero di nodi
COMPARABILE o INFERIORE all'oracolo (rapporto 0,46-0,66); sul bench a profondita' 13 siamo a 2,24 M
contro 2,50 M. E' la **velocita' grezza: 398.000 nodi/s contro 1.600.000, cioe' 4,0x** — C# contro
C++ con intrinseche AVX2, non un difetto di fedelta'.

Anomalia annotata: nel finale `8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11` il rapporto di nodi
esplode a 12-13x a profondita' 11-13, per rientrare a 1,1x da d14. Non spiegata.

### IL RIPRODUTTORE MINIMO, da attaccare per primo

Ottenuto per divide successivi dal disaccordo a profondita' 3 su
`8/8/1P6/5pr1/8/4R3/7k/2K5 w - - 0 1` (identico all'oracolo fino a d2, 283 nodi):

> **`8/8/1P6/5pr1/8/1R6/7k/2K5 b - - 1 1`**
> profondita' 1: IDENTICO (13 nodi, cp -13)
> profondita' 2: **384 nodi contro 378, cp -30 contro -31** — stessa mossa (g5g8) e stessa PV

E' il caso piu' piccolo mai isolato. **Tutti e 12 i figli sono identici** all'oracolo se cercati da
soli a profondita' 1 (punteggio E nodi): la differenza sta unicamente in come la radice li combina
a profondita' 2.

Gia' verificati fedeli su questo percorso, da NON ricontrollare: `update_all_stats` e
`update_quiet_histories` riga per riga; la sequenza di aspirazione a profondita' 2, tracciata e
ricalcolata a mano (8 fail-low, finestre -49 -> -54 -> -72 -> -80 -> -90 -> -104 -> -122 -> -145,
con `delta += 47*delta/128`: coincide esattamente con la formula della fonte).

Nota per chi instrumenta la radice: **il riscaldamento JIT dell'avvio esegue una propria ricerca**
(8 thread, profondita' 10 su startpos) e i suoi nodi di radice finiscono nel trace. Vanno
riconosciuti e scartati, altrimenti sembrano iterazioni assurde del caso in esame.

## Strumenti di misura (tools/)

- `audit_fedelta.py` — audit istruzione per istruzione fonte/porting (vedi sotto).
- `bench_cmp.py` — confronto posizione-per-posizione del bench contro l'oracolo (mossa finale e
  punteggio a profondita' 13).
- `onset.py` — per una FEN, confronta punteggio, mossa e NUMERO DI NODI a ogni profondita' contro
  l'oracolo: dice esattamente a quale profondita' comincia la divergenza. Fa una ricerca SEPARATA
  per profondita', perche' il nostro motore stampa una sola riga `info` a fine ricerca mentre la
  fonte ne stampa una per iterazione.
- `divide.py` — "divide" sulla ricerca: per ogni mossa di radice cerca il FIGLIO a profondita' d-1
  con entrambi i motori e confronta. E' il metodo che ha ristretto la divergenza di punteggio da un
  albero di milioni di nodi a un caso da 200 (su Kiwipete, 47 figli su 48 combaciavano ESATTAMENTE).
- `truth_build.py` + `truth.json` — verita' di riferimento CONGELATA per la metrica di qualita'.
- `quality.py` — qualita' della mossa a tempo fisso contro quella verita'.

### La metrica di qualita' e perche' la verita' e' congelata

`python tools/quality.py 2000 1 "etichetta"` -> percentuale di posizioni (51, dalla lista del bench)
in cui il motore, in 2 secondi, sceglie la mossa dell'oracolo.

**Vizio corretto il 2026-09-08**: la verita' veniva rigenerata a ogni esecuzione con l'oracolo a
**8 thread**. Il Lazy SMP non e' deterministico, quindi il riferimento cambiava e i numeri di
esecuzioni diverse NON erano confrontabili — misurato: il controllo "oracolo a 1 thread" e' passato
da 86,1% a 88,9% fra due esecuzioni senza che nulla dell'oracolo fosse cambiato, facendo sembrare
una regressione quello che era solo un bersaglio mobile. Ora la verita' e' l'oracolo a **1 thread e
profondita' fissa 20** (deterministica) ed e' congelata in `tools/truth.json`: va rigenerata solo se
si cambia la lista di posizioni.

**Misure di riferimento** (2 s a mossa, 1 thread, stessa verita' congelata, 3 repliche per ramo):

| | accordo |
|---|---|
| oracolo, stesso budget (soffitto) | 49/51 = 96,1% |
| **HEAD, 2026-09-08 sera** | 44 / 43 / 43 su 51 = **84,3-86,3%** |
| 844c691, prima della sessione del 2026-09-08 | 42 / 42 / 42 su 51 = 82,4% |

Il ramo vecchio e' stabilissimo a 42, il nuovo sta sempre sopra: il lavoro della giornata vale
circa +1,3 posizioni su 51, piccolo ma consistente in tutte le repliche.

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

### Vagliato il 2026-09-08 (secondo giro)

- **`movepick.cpp`**: entrambi i costruttori di `MovePicker` fedeli, `pseudo_legal(ttm)` incluso e
  `capture_stage` per il ProbCut. `score<QUIETS>` fedele in tutti i termini: `threatByLesser`, il
  bonus 16384 per gli scacchi, il termine +/-20 per casa minacciata da pezzo minore, la low-ply
  history. `score<EVASIONS>` fedele (`1 << 28`). Il resto dei candidati segnalati dallo strumento e'
  il `MoveSorter` AVX-512, non portato per scelta dichiarata.
- **Semantica temporale su `Search.cs`** (metodo 4): tutte le chiamate a `_movePick.*` classificate
  PRE/POST rispetto alle mutazioni. Solo due sono POST, ed erano i due bug gia' corretti. Pulito.
- **Assunzioni dichiarate** (metodo 5): 55 censite. Vagliate quelle di `SearchThreadPool.cs`,
  `RootMove.cs` e la testata di `Search.cs`. Trovati un errore vero (spareggio del voto sulla
  profondita' invece che sulla lunghezza della PV) e due commenti OBSOLETI che affermavano il falso.
  **Restano ~50 da vagliare.**
- **Allocazioni sul percorso caldo**: tre trovate e convertite a buffer riusabili
  (`threatByLesser` in `Score(QUIETS)`, e le due `List<Move>` in `Position.PseudoLegal` e
  `Position.IsDraw` — quest'ultima chiamata su decine di milioni di nodi con `rule50 > 99`).
  Nodi identici, quindi cambio neutro sul comportamento.

### Buco di COPERTURA colmato: le chiavi incrementali (2026-09-08)

Ragionando su *cosa i test esistenti non possono vedere*: il perft e' la verifica principale di
`Position`/`MoveGen`, ma conta solo nodi — **non tocca nessuna chiave Zobrist**. Una deriva sarebbe
quindi rimasta invisibile, e non e' innocua:

| chiave | cosa indicizza | conseguenza di una deriva |
|---|---|---|
| `Key` | transposition table | entry lette per la posizione sbagliata |
| `PawnKey` | pawn history, pawn correction history | ordinamento e correzione della valutazione sbagliati |
| `MinorPieceKey`, `NonPawnKey` | le altre correction history | `to_corrected_static_eval` sbagliata a OGNI nodo |
| `MaterialKey` | riconoscimento del materiale | |

Nuovo `IncrementalKeysTests`: ricorsione su TUTTE le mosse legali (non a campione) fino a
profondita' 2-3 su 5 posizioni scelte per coprire arrocco, presa en passant, promozioni e finali di
pedoni; a ogni nodo confronta le sei chiavi mantenute incrementalmente con quelle ricalcolate da
zero via `Position.Set(fen)`, e verifica anche il ripristino dopo `UndoMove`. Coperto anche il
**null move**, che tocca le chiavi e che il perft non genera mai. **Esito: tutte corrette.**

**Il test e' stato validato per mutazione**: togliendo un solo `PawnKey ^= ...` sulla cattura di un
pedone, 4 casi su 5 falliscono con messaggi precisi. Un test che passa senza saper fallire non prova
nulla — vedi il caso di `NnueIncrementalTests`, che copriva solo il caso facile.

### Allocazioni: ripreso il filo lasciato aperto (2026-09-08)

Il piano di porting aveva lasciato annotato "restano ~210 byte/nodo, i candidati successivi sono le
allocazioni residue". Ripreso e misurato: il bench stampa ora **byte allocati per nodo** nel
riepilogo, cosi' la metrica resta sott'occhio.

Scansione sistematica delle allocazioni dentro i corpi dei metodi (non gli inizializzatori di campo)
sui file del percorso caldo. Esito:
- `DirtyThreat` e' una `readonly struct`: `new DirtyThreat(...)` non alloca sull'heap. Falso allarme.
- I wrapper di `NnueLayers` che restituiscono array **non sono chiamati dal motore**, solo dai test.
- **`MovePicker` era una `sealed class` con 22 campi, costruita a OGNI nodo** (ciclo principale,
  quiescenza e ProbCut). Nella fonte `MovePicker mp(...)` e' un oggetto sullo stack. Convertito a
  istanza riusabile per ply, con **tre slot** per ply:
  0 ciclo principale (condiviso con la quiescenza, che parte a `depth <= 0` cioe' prima che
  Negamax costruisca il proprio), 1 verifica delle Singular Extensions (rientra allo STESSO ply
  mentre il MovePicker esterno e' vivo), 2 ProbCut.

**Risultato**: da **258,8 a 130,4 byte/nodo (-50%)**, con **nodi IDENTICI** (1.664.300) e 124/124
test — la verifica che una conversione di soli buffer deve lasciare invariato il conteggio nodi.
Velocita': 326-351k nodi/s contro 337-339k prima, cioe' dentro la varianza: **nessun guadagno di
velocita' dimostrato**, solo meno pressione sul GC (che conta soprattutto a piu' thread).

#### I 130 byte/nodo rimasti: chiusi (2026-09-08, commit 469818a)

Primo tentativo per bisezione (NNUE acceso / NNUE spento): **fuorviante**. Spegnere la NNUE cambia
anche la forma dell'albero e il numero di nodi, quindi confronta due ricerche diverse — sembrava
dire "meta' e' NNUE", ed era falso. Metodo giusto: **attribuzione diretta**, sonde temporanee con
`GC.GetAllocatedBytesForCurrentThread()` attorno a Eval, MovePicker e al totale del thread di
ricerca (il bench a 1 thread rende il contatore per-thread esatto e attribuibile).

    TOTALE 127,3 byte/nodo  ->  Eval 1,5   MovePick 121,7

**Causa**: i sei `Select(() => ...)` di `MovePicker.NextMove` catturano `this`, quindi il
compilatore alloca un delegate a ogni chiamata. Nella fonte `select<T>(Pred filter)` e' un template
con un lambda, cioe' a costo zero. L'equivalente fedele in C# **non e' un `Func<bool>`** ma un
parametro di tipo generico vincolato a struct (`where TF : struct, IFiltroMossa`), che il JIT
specializza e inlina: stesso codice generato del template, zero allocazioni.

**Risultato**: da **127,3 a 4,4 byte/nodo (-96,5%)**, **nodi IDENTICI** (1.664.300), 124/124 test.

Due contaminazioni della MISURA scoperte per strada, entrambe corrette:
- Il **riscaldamento JIT dell'avvio** (8 thread, ~800 ms) si sovrapponeva a bench e perft: rubava
  CPU ai tempi e le sue allocazioni finivano nel contatore di processo. Sul perft valeva da solo
  46 dei 47,4 byte/nodo misurati. Ora bench e perft lo aspettano.
- Il **perft** allocava `new List<Move>() + new StateInfo()` a ogni nodo (81,1 byte/nodo, tutti
  suoi): misurava soprattutto se stesso. Ora usa buffer per livello. E' anche lo strumento con cui
  si misura il resto del motore, quindi doveva essere neutro.

**Lezione di metodo, generalizzabile**: prima di attribuire un costo, verificare che lo strumento di
misura non sia esso stesso la fonte del costo, e che nessun altro thread stia contribuendo al
contatore. Il confronto "acceso/spento" e' valido solo se il lavoro misurato resta lo stesso.

Restano 4,4 byte/nodo, di cui ~1,2 in `MoveGen.Generate` (crescita dei `List<Move>` di appoggio):
sotto la soglia in cui vale la pena intervenire, ma annotati qui per non riaprire l'indagine.

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
**FATTO**: `Position._drawScratch` (e `_pseudoLegalScratch` per il gemello in `PseudoLegal`).

### 2. Divergenza di punteggio a profondita' medie — MOLTO RIDOTTA il 2026-09-08

**Metodo che ha funzionato, da riusare**: restringere il riproduttore per "divide" successivi
(`scratchpad/divide.py`): per ogni mossa di radice si cerca il FIGLIO a profondita' d-1 con entrambi
i motori e si confronta punteggio E numero di nodi. Su Kiwipete a profondita' 2, **47 figli su 48
combaciavano esattamente** — punteggio e nodi — e uno solo no. Ripetendo si scende in fretta a un
caso minuscolo. Confronto per profondita' con `scratchpad/onset.py` (una ricerca SEPARATA per
profondita': il nostro motore stampa una sola riga `info` a fine ricerca, non una per iterazione
come la fonte).

Riproduttore minimo attuale: `8/2p5/3p4/KP5r/5R1k/8/4P1P1/8 b - - 0 11`, nodi bit-identici
all'oracolo fino a profondita' 4 (2 / 12 / 40 / 191), divergenza da 5.

**Tre cause trovate e corrette** (commit 2ea0fe6 e 8d05f10):
1. Media mobile delle RootMove: arrotondava verso zero invece che verso meno infinito
   (vedi `AritmeticaFedele.cs`). Alimenta la finestra di aspirazione e l'optimism.
2. `bonus` di `update_all_stats` (search.cpp:1979): stessa classe di errore. Su Kiwipete lo scarto
   di nodi contro l'oracolo e' passato da ~65 a **3-4** su tutte le profondita' 3-6.
3. La quiescenza non aggiornava mai la PV (search.cpp:1678-1683, 1840-1842), quindi la PV finiva
   troncata dove comincia la quiescenza, e con essa `lastIterationIdxPV` -> `followPv` -> la
   potatura dell'iterazione successiva.

**VERIFICATO NON COLPEVOLE, non riaprire**: la valutazione. Sul caso divergente il valore grezzo
della rete e' **-68 in entrambi i motori**. La differenza apparente ("noi -79, oracolo -68") era un
confronto fra grandezze NON OMOGENEE: il nostro `eval` stampava il valore FINALE (con complessita',
materiale, optimism e smorzamento per la regola delle 50 mosse), quello dell'oracolo il valore
INTERMEDIO. Aggiunta al nostro `eval` la stessa scomposizione psqt/positional di `Eval::trace` per
non ricascarci. Verificata a mano anche l'aritmetica finale: `material` = 534*16 + 15779 = 24323 da'
esattamente -79 in entrambi, e `to_cp` e' lineare a posizione fissa (`to_cp(68)=24` implica a=283,
da cui `to_cp(79)=27`, cioe' il "+0.27" stampato dall'oracolo).

**Cosa resta**: sul riproduttore minimo il punteggio a d5 ora combacia ma i nodi no (370 contro
425), e da d6 divergono di nuovo entrambi. Su Kiwipete lo scarto e' di 3-4 nodi fino a d6 e poi
esplode a d7. Ripartire da li' con lo stesso metodo del divide.

### 2-quater. (storico) Divergenza di punteggio a profondita' medie

I punteggi combaciano ESATTAMENTE a profondita' 1 e divergono a profondita' variabile secondo la
posizione (d2, d3, d5, d8, d9, o mai). Dopo le correzioni del 2026-09-07 la soglia si e' spostata
piu' in fondo (posizione "tattica" da d2 a d9, esatta fino a d5). Reproducer piu' piccolo:
`r1bbk1nr/pp3p1p/2n5/1N4p1/2Np1B2/8/PPP2PPP/2KR1B1R w kq - 0 13`, che diverge gia' a **profondita'
2** pur essendo esatto a profondita' 1: a quella profondita' l'albero e' abbastanza piccolo da
confrontarlo nodo per nodo.

### 2-bis. Efficienza parallela — misura di riferimento (2026-09-08)

Ripresa DOPO il taglio delle allocazioni (127,3 -> 4,4 byte/nodo), con controllo sull'oracolo nelle
stesse identiche condizioni (`bench 128 <thread> 13`, stessa macchina, 8 core fisici + HT):

| thread | nostro nodi/s | speedup | oracolo nodi/s | speedup |
|---|---|---|---|---|
| 1 | 340.450 | 1,00x | 1.600.816 | 1,00x |
| 2 | 613.419 | 1,80x | 3.326.779 | 2,08x |
| 4 | 1.135.672 | 3,34x | 6.023.549 | 3,76x |
| 8 | 1.825.504 | **5,36x** | 10.946.919 | **6,84x** |

Il divario di SCALABILITA' si e' ridotto ma non chiuso: 4,92x -> 5,36x contro i 6,84x dell'oracolo
(dal 73% al 78% della sua scalabilita'). Quindi la pressione sul GC **contribuiva** ma non era la
causa principale: resta da cercare altrove (contesa sulla TT, `Interlocked`, false sharing).

Il divario di VELOCITA' ASSOLUTA e' un'altra cosa e non e' un difetto di fedelta': 4,7x a 1 thread,
atteso fra C# e C++ con intrinseche AVX2 sulla NNUE.

### 2-ter. TROVATO: le history condivise fra thread non lo sono (2026-09-08)

Emerso vagliando `history.h` con lo strumento. **Non era una deviazione dichiarata da nessuna
parte**: e' un pezzo mai portato.

Nella fonte le history sono divise in due gruppi (search.h:349-357):

| gruppo | tabelle | dove vivono |
|---|---|---|
| per thread (`Worker`) | `mainHistory`, `lowPlyHistory`, `captureHistory`, `continuationCorrectionHistory`, `ttMoveHistory` | una copia per thread |
| **condivise** (`SharedHistories`, history.h:204-257) | **`correctionHistory`** (pawn/minor/nonPawn), **`continuationHistory[2][2]`**, **`pawnHistory`** | **una sola copia per nodo NUMA, usata da TUTTI i thread di quel nodo** |

E le due condivise dinamiche **scalano col numero di thread** (`DynStats`, history.h:91-101):
`SharedHistories(next_power_of_two(threadCount))` (thread.cpp:214), quindi con 8 thread
`correctionHistory` ha 8 x 65536 voci e `pawnHistory` 8 x 8192.

**Da noi**: tutte e tre sono campi di istanza (`MovePick._continuationHistory`,
`MovePick._pawnHistory`, `Search._pawnCorrHistory` e sorelle), cioe' **una copia privata per thread,
a dimensione fissa non scalata**. Conseguenze, tutte nella direzione "piu' deboli a molti thread":
1. i thread helper **non si scambiano nulla** attraverso queste tabelle — e' proprio il canale con
   cui il Lazy SMP moderno guadagna Elo oltre alla sola TT condivisa;
2. con 8 thread la tabella efficace e' 1/8 di quella della fonte, quindi molte piu' collisioni;
3. `_continuationHistory` da sola e' ~8,4 MB per thread (67 MB a 8 thread) contro gli 8,4 MB totali
   della fonte: anche peggio per la cache.

**PORTATO** lo stesso giorno (commit 66a8c4d, `StockfishSharp.Engine/SharedHistories.cs`).
Verifiche:
- **1 thread: nodi IDENTICI** (1.664.300) prima e dopo — con un thread solo il moltiplicatore e' 1
  e non c'e' nessuno con cui condividere, quindi qualunque differenza avrebbe significato che il
  refactoring aveva cambiato altro.
- **8 thread**, `bench 128 8 13`, **4 repliche per ramo** (il Lazy SMP non e' deterministico: una
  misura sola non direbbe nulla):

  | | nodi per arrivare a profondita' 13 | media |
  |---|---|---|
  | prima | 13.879.099 / 12.871.077 / 13.449.691 / 14.060.639 | 13.565.127 |
  | dopo | 11.205.718 / 12.256.870 / 12.669.593 / 12.860.211 | 12.248.098 |

  **-9,7%**, con i due insiemi che non si sovrappongono (Mann-Whitney p ~ 0,014). Nodi/secondo
  invariati: il guadagno e' in efficienza della ricerca, non in velocita' grezza — quindi **non**
  sposta il numero di scalabilita' del punto 2-bis, che e' un rapporto fra nodi/secondo.

Nota di verifica, per non riaprirla: la `CorrectionBundle` unificata della fonte (un solo array
indicizzato da quattro chiavi diverse) **non e' una differenza**, perche' ogni tipo legge un campo
diverso del bundle — le nostre quattro tabelle separate sono funzionalmente equivalenti. La
differenza sta solo nella dimensione e nella condivisione.

### Vagliato il 2026-09-08 (terzo giro): tt.cpp, movegen.cpp

**`tt.cpp` — DUE discrepanze trovate, entrambe corrette** (commit 444ceb4):
1. `TranspositionTable::new_search` (tt.cpp:238-242) fa `++generation8;` **e poi**
   `generation8 &= GENERATION_MASK;`. Noi facevamo solo l'incremento. `genBound8` impacca
   generazione (5 bit), bound (2 bit) e pv (1 bit) nello stesso byte e `save()` li unisce con un OR:
   **dalla 32esima ricerca in poi i bit alti della generazione traboccavano nei campi bound e pv**,
   che venivano poi riletti sbagliati (un UPPER puo' tornare EXACT, cioe' un taglio che non andava
   fatto). La fonte ha un assert esplicito proprio su questo dentro `save()`.
   Effetto misurato sul bench: **1.664.300 -> 1.958.546 nodi (+17,7%)**, piu' vicino ai 2.497.913
   dell'oracolo — i tagli spuri facevano visitare meno nodi del dovuto.
2. `TranspositionTable::clear` (tt.cpp:189) azzera anche `generation8`; il nostro `Clear()` no,
   quindi il contatore proseguiva attraverso `ucinewgame` e "Clear Hash".

   *Quando si manifesta*: una `NewSearch` per mossa giocata, quindi **dalla 32esima mossa in poi di
   ogni partita**. NON si manifesta rigiocando una posizione da un processo nuovo, che riparte da
   generazione 0. **Ipotesi verificata e SMENTITA**: non spiega il blunder mai riprodotto della
   partita persa — il replay usava un processo unico con `go` in sequenza, quindi la generazione
   superava 32 anche li', e il blunder non si e' riprodotto lo stesso. Non riproporla.

   Verificato fedele nello stesso passaggio: `TTEntry::save` (condizione di sovrascrittura,
   invecchiamento secondario e le sue quattro guardie), `relative_age`, la politica di rimpiazzo
   `depth8 - 8 * relative_age`, `Read`, il layout dei bit.

**`movegen.cpp`**: i candidati dello strumento sono tutti intrinseche AVX-512/SIMD (non portate per
scelta dichiarata) o struttura a template. Ma il vaglio ha fatto emergere un **buco di COPERTURA**,
non di fedelta': il perft chiama sempre e solo `GenType.Legal`, mentre il MovePicker — cioe' tutta
la ricerca — usa `Captures`, `Quiets` ed `Evasions` separatamente. **Colmato** con
`StockfishSharp.Tests/GenTypeCoverageTests.cs` (commit 230d6e2), 8 test.

Due errori commessi scrivendo quel test, tutti e due generalizzabili e da non ripetere:
- **Il ramo difficile non veniva mai percorso**: nessuna posizione di partenza raggiungeva un nodo
  sotto scacco, quindi una mutazione che svuotava EVASIONS passava indenne. Rimedio: posizioni gia'
  sotto scacco fra i casi + **guardie di copertura** che falliscono se un ramo non e' stato visitato.
- **Il confronto era una TAUTOLOGIA**: sotto scacco `GenType.Legal` e' COSTRUITO su `Evasions`
  (MoveGen.cs:52), quindi la mutazione rompeva allo stesso modo atteso e ottenuto. E nemmeno
  "NON_EVASIONS filtrate con `pos.Legal`" andava bene, perche' `Legal` e' fedele alla fonte e **non
  verifica che la mossa risolva lo scacco**. L'unico oracolo indipendente e' la definizione stessa
  di legalita': esegui la mossa, guarda se il tuo re resta attaccato, disfa.

**Nota minore, non corretta** (layer UCI, Flow A4 non portato): a matto/stallo l'oracolo stampa
`bestmove (none)`, noi `bestmove 0000`. Entrambi validi per una GUI; annotato per non riscoprirlo.

**Accordo con l'oracolo, misura di riferimento** (`bench 16 1 13`, mossa finale a profondita' 13 su
51 posizioni, strumento in `scratchpad/bench_cmp.py`): **37/51 mosse uguali, 7/51 punteggi uguali**
(39/51 e 6/51 prima della correzione della generazione: due posizioni su 51 non dicono nulla in
nessuna direzione). I punteggi divergono quasi ovunque — e' il punto 2 qui sotto.

### Vagliato il 2026-09-08 (quarto giro): position.cpp

**DUE discrepanze trovate, di cui una e' un meccanismo INTERO mai portato.**

1. **`rule50` veniva incrementato dentro `DoNullMove`** (commit 401b064). Nella fonte
   `++st->rule50` compare una volta sola, dentro `do_move` (position.cpp:839): il contatore conta
   MOSSE VERE. Lungo una linea con N mosse nulle il nostro contatore era N avanti. Non e' innocuo:
   `rule50` smorza la valutazione statica (`v -= v * rule50_count() / 199`), decide la patta e
   declassa i punteggi di matto in `ValueFromTt`.
   **Costo misurato, riportato per intero**: la correzione FEDELE fa PEGGIO sulla metrica di
   qualita' (da 44/43/43 a 41/41 su 51). Si e' tenuta lo stesso — ed e' stata proprio quella
   anomalia a far trovare la seconda discrepanza.

2. **`adjust_key50` MAI PORTATO** (commit 87711ce). Nella fonte la chiave della TT non e' quella
   grezza: `Position::key()` e' `adjust_key50(st->key)` (position.h:319-324), che sopra 14 mezze
   mosse perturba la chiave di un valore che cambia ogni 8 unita' di `rule50`. Serve a impedire che
   due posizioni identiche sulla scacchiera ma a orizzonti di patta diversi condividano la stessa
   entry. Da noi `Position.Key` restituiva `_st.Key`.
   **Effetto sulla parita' di nodi col bench**: 2.159.687 -> **2.440.725** contro i **2.497.913**
   dell'oracolo, cioe' dal 33% di scarto di stamattina al **2,3%**.

**Buco di COPERTURA colmato: `GivesCheck`** (commit dc4c0c3, `GivesCheckTests.cs`). `DoMove` non
ricalcola gli scacchi, si fida del flag (`CheckersBB = givesCheck ? AttackersTo(re) & Pieces(us) :
0`). Ne segue un'asimmetria: un falso NEGATIVO rompe il perft (evasioni non generate), un falso
POSITIVO e' **invisibile** al perft, perche' il ramo "true" ricalcola comunque gli attaccanti veri e
ottiene 0. In ricerca pero' il flag decide potature ed estensioni. **Verificato, non argomentato**:
con una mutazione che fa restituire "true" a ogni promozione, tutti e 22 i test di perft passano e
perft(4) da' ancora 422.333; il test nuovo fallisce.

**Restano da vagliare in `position.cpp`**: i candidati dello strumento sono in gran parte intrinseche
AVX-512 (non portate per scelta) e `set_state`/`do_castling`, gia' coperti dal perft e dai test
sulle chiavi incrementali.

### Vagliato il 2026-09-08 (quinto giro): assunzioni dichiarate in Search.cs

- **`priorCapture` letto dal posto sbagliato** (commit 68e1002, search.cpp:979): usavamo il
  `captureStage` del genitore invece di `pos.captured_piece()`. Non e' la stessa cosa —
  `capture_stage` include le promozioni a donna QUIETE, che non catturano nulla. Trovato
  confrontando fra loro i TRE siti che nella fonte usano `priorCapture`: gli altri due
  (search.cpp:887 e lo Step 23) lo leggevano gia' bene. **Metodo riusabile: quando la fonte usa lo
  stesso nome in piu' punti, confrontare i nostri siti fra loro — la discordanza interna e' un
  segnale piu' forte del confronto con la fonte riga per riga.**

VERIFICATI FEDELI in questo giro, da non ricontrollare:
- struttura `goto moves_loop` sotto scacco (il nostro `if (!inCheck) { Step 6-12 }` con lo Step 7
  tablebase PRIMA, come la fonte);
- Step 13 "piccola idea di ProbCut" (costante 428, condizioni e posizione);
- il workaround `rule50_count() < 96` sui tagli da TT (presente);
- l'intero blocco del bonus da differenza di valutazione statica (search.cpp:978-986), ora identico;
- `partial_insertion_sort` (trascrizione fedele del ramo scalare);
- `score<CAPTURES>` (`captureHistory + 7 * PieceValue`);
- l'ordine di emissione di `generate_pawn_moves` (spinte, poi promozioni UpRight/UpLeft/Up, poi
  catture, poi en passant);
- la media mobile delle RootMove, la finestra di aspirazione e `failedHighCnt` (verificati con una
  sonda sul ciclo di radice: le finestre osservate corrispondono esattamente alla fonte);
- il decadimento 729/1024 della main history a ogni ricerca, `totBestMoveChanges`, `scaleFactor`
  della gestione tempo, il tie-break `inc` con `& 14`.

**FALSO ALLARME chiarito, non riaprirlo**: la fonte scrive `ss->currentMove = Move::null()` nel
null move, noi `Move.None`. Sembra una differenza (le due mosse hanno case diverse: `null().to_sq()`
e' B1, `none().to_sq()` e' A1) ma NON lo e': per il null move la fonte non indicizza mai con
`to_sq()`, sostituisce il PUNTATORE con `&continuationHistory[0][0][NO_PIECE][0]`, e il nostro
`Move.None` (casa A1 = 0) riproduce esattamente quel piano. Ogni altra lettura e' protetta da
`is_ok()`, che scarta entrambi i valori.

### Checklist dello strumento su search.cpp: primo blocco vagliato (2026-09-08)

Degli ~80 candidati "assenti" la grande maggioranza sono falsi positivi da rinomina. Vagliati e
**VERIFICATI FEDELI** (non ricontrollare):
`ttPv` e la sua ereditarieta' su fail-low (search.cpp:1617); il bonus/malus da taglio di TT
(search.cpp:880-889); il **penalize(1)** della entry inutile (search.cpp:915-921, condizione e
costante identiche); il probe della posizione DOPO la mossa di TT (search.cpp:895-905, incluso l'uso
del valore GREZZO e di `pos.key()` — che ora, con `adjust_key50` portato, e' davvero la stessa
chiave); Step 9 futility completo, incluso `futilityMult -= 20 * !ss->ttHit`; l'espressione di
`followPV` (search.cpp:772-775); la soglia del MovePicker di ProbCut (`probCutBeta - ss->staticEval`).

### 5. Lead aperto: 3 nodi di quiescenza

Riproduttore piu' piccolo di tutti: `8/2p5/3p4/KP5r/3R1p1k/8/4P1P1/8 b - - 1 11`, **scarto costante
di 3 nodi gia' a profondita' 1** (20 contro 23), con lo stesso punteggio fino a d3. Le 15 mosse
legali di radice coincidono (perft 1 = 15 in entrambi), quindi i 3 nodi sono dentro la quiescenza.

Gia' verificati fedeli riga per riga e da NON ricontrollare: il blocco di potatura della quiescenza
(search.cpp:1786-1821, incluso `moveCount > 2`, i due rami di futility e la SEE a -74), il calcolo
di `futilityBase`, entrambe le smorzature (441/583 nello stand pat, 462/562 in fondo), e
`score<CAPTURES>` (`captureHistory + 7 * PieceValue`).

**Candidato residuo**: l'ORDINE delle catture. `if (moveCount > 2) continue` fa sopravvivere solo le
prime due, quindi un ordine diverso cambia quali mosse si cercano. L'ordine di `GenType.Captures`
non e' verificato da nulla — il perft valida solo l'ordine di `GenType.Legal`, e il test nuovo sui
tipi di generazione valida gli INSIEMI, non le sequenze. Prossimo passo naturale: confrontare la
sequenza di `GenType.Captures` con quella della fonte, e il comportamento di
`PartialInsertionSort` sui valori pari.

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

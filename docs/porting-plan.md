# Piano di porting Stockfish -> C#

Sorgente di riferimento: [official-stockfish/Stockfish](https://github.com/official-stockfish/Stockfish),
clonato in locale in `../stockfish-upstream-reference` (fuori da questo repo, solo come
riferimento di lettura), commit `edb0d9db6731067ec50ce619ff372b463bc4dd5d` (2026-09-05).

Porting **reale**: struttura, nomi, algoritmi e costanti numeriche della fonte C++ tradotti in C#
idiomatico, non un adattamento libero come nel motore di [ACMyChess](../../ACMyChess) (progetto
sorella). Ogni file C# cita il file/le righe della fonte da cui deriva, per poter verificare (e
ri-verificare in futuro contro una versione più recente di Stockfish) riga per riga.

Dimensione della fonte upstream (solo `src/`, `.cpp`+`.h`): ~25.300 righe totali (~16.700 nel
nucleo, ~3.400 NNUE, ~2.050 Syzygy, resto in `universal/`). Un porting completo è un progetto
pluri-sessione, non qualcosa da completare in un colpo solo — questo documento è la tabella di
marcia e lo stato di avanzamento, aggiornato a ogni fase.

## Semplificazioni deliberate (valide per l'intero porting, non solo per la Fase 1)

- **Solo 64 bit**: `Is64Bit` è sempre vero su .NET moderno — i rami a 32 bit della fonte (es.
  `Magic::index()` a due metà in attacks.cpp) non vengono portati.
- **Solo l'implementazione "fancy magic bitboard" classica** per le sliding attacks — la fonte
  sceglie a tempo di compilazione fra tre varianti (hyperbola quintessence ARM, "dual hyperbola
  quintessence" AVX2, magic bitboard classici) in base alla CPU target; qui portiamo solo la
  terza, indipendente dalla piattaforma e più facile da verificare. Se in futuro servisse
  spremere prestazioni extra su x86-64 con AVX2, la variante dual hyperbola resta un candidato
  per una fase successiva, separata.
- **Value/Depth restano `int`** invece di un alias di tipo dedicato (C# non alias-a i tipi
  primitivi attraverso più file in modo pulito) — stesso approccio già usato in ACMyChess.
- Le tabelle `constexpr` calcolate a tempo di compilazione nella fonte (`PseudoAttacks`,
  `PawnPushOrAttacks`, ecc.) diventano tabelle calcolate a runtime al primo utilizzo (vedi
  `Attacks.EnsureInitialized()`) — stesso risultato, C# non ha un equivalente diretto di
  `constexpr` su cicli generici.

## Stato di avanzamento

### Fase 1 — Fondamenta: tipi, bitboard, tabelle di attacco, posizione, generazione mosse — **FATTA**

| Sorgente | File C# | Stato |
|---|---|---|
| `types.h` | `StockfishSharp.Engine/Types.cs` | ✅ fatto (esclusi `DirtyPiece`/`DirtyThreat*`, rimandati alla Fase NNUE) |
| `bitboard.h` + `bitboard.cpp` | `StockfishSharp.Engine/Bitboards.cs` | ✅ fatto |
| `attacks.h` + `attacks.cpp` | `StockfishSharp.Engine/Attacks.cs` | ✅ fatto (magic bitboard classici E la variante AVX2, selezione a runtime) — **verificato**: confronto a forza bruta + le due varianti confrontate direttamente fra loro |
| `misc.h` (solo `PRNG`) | inline in `Attacks.cs` (`XorShift64StarRng`) | ✅ fatto, parziale — il resto di `misc.h` (timer, logging, syzygy path helpers) non ancora toccato |
| `position.h` + `position.cpp` | `StockfishSharp.Engine/Position.cs`, `StateInfo.cs`, `Zobrist.cs` | ✅ fatto per quanto serve a perft (vedi sotto per cosa manca) |
| `movegen.h` + `movegen.cpp` | `StockfishSharp.Engine/MoveGen.cs` | ✅ fatto (esclusa l'ottimizzazione SIMD AVX-512ICL di impacchettamento mosse, non scacchisticamente rilevante) |
| perft (in Stockfish dentro `benchmark.cpp`) | `StockfishSharp.Engine/Perft.cs` | ✅ fatto (implementazione indipendente, non porting di un file specifico) |

**Verificato**: 35/35 test, incluso **perft esatto su tutte le 6 posizioni di riferimento standard**
della comunità chess-programming (iniziale, Kiwipete, posizioni 3-6) a più profondità ciascuna —
i valori pubblicati e universalmente noti, non specifici di Stockfish. Anche verificato: round-trip
FEN (parse->serializza->stesso testo) e fare+disfare ogni mossa legale dalla posizione iniziale
riporta esattamente alla FEN di partenza.

**NON portato da `position.h`/`position.cpp`** (non serve a perft, rimandato):
- Static Exchange Evaluation (`see_ge`) — serve all'ordinamento mosse in ricerca.
- `is_draw`/`is_repetition`/`upcoming_repetition` e le "cuckoo table" per la rilevazione veloce di
  ripetizione — perft conta tutti i nodi foglia comunque, ripetizioni comprese; questa rilevazione
  serve alla ricerca vera. `StateInfo.Repetition` resta sempre a 0 per ora.
- `pos_is_ok()` (asserzioni di consistenza per debug) e `flip()` (specchia la posizione, utile per
  test di simmetria — buon candidato per rinforzare i test di perft in futuro).
- Gli agganci NNUE (`DirtyPiece`/`DirtyThreats`/`scratchDirties`) e transposition
  table/SharedHistories (i prefetch in `do_move`) — arriveranno con le fasi NNUE e ricerca.

**Semplificazioni meccaniche** (stesso algoritmo, meccanica di linguaggio diversa): FEN parsata per
token di stringa invece che via `std::istringstream` carattere per carattere; errori di parsing
FEN come eccezione (`PositionSetException`) invece di `std::optional<PositionSetError>`
restituito; parametri template `Color Us`/`GenType Type` (specializzazione a tempo di compilazione
in C++) diventati parametri normali con branch a runtime.

### Fase 2 — Ricerca (core) + Fase 3 — UCI minimo — **FATTE, con scope rivisto**

Scoperto leggendo le fonti: `evaluate.cpp` moderno (105 righe) è ormai solo un sottile involucro
attorno a NNUE — non esiste più una "valutazione classica" separata da portare. La Fase 3
originariamente pianificata ("eval classica") non corrisponde a niente di reale in questa
versione; rinominata/assorbita in una valutazione temporanea (vedi sotto). `search.cpp` inoltre è
2369 righe (quasi il doppio della stima iniziale) — portarlo per intero con OGNI tecnica di
potatura sarebbe un progetto a sé; qui è stato portato un NUCLEO funzionante e verificato, non il
totale.

| Sorgente / scopo | File C# | Stato |
|---|---|---|
| `tt.h` + `tt.cpp` | `StockfishSharp.Engine/TranspositionTable.cs` | ✅ portato fedelmente (cluster da 3, bitfield impacchettati, invecchiamento) — no NUMA/huge-page, non rilevanti in C# |
| `see_ge` (in `position.cpp`) | `Position.SeeGe` | ✅ portato fedelmente |
| valutazione statica | `StockfishSharp.Engine/Evaluate.cs` | ⚠️ **NON un porting** — placeholder originale (materiale + PSQT standard) in attesa della Fase NNUE, che sostituirà questo file per intero |
| `movepick.h` + `movepick.cpp` | `StockfishSharp.Engine/MovePick.cs` | ⚠️ **NON un porting diretto** — ordinamento più semplice (TT move, SEE, killer, history) dello stesso spirito ma senza continuation history/countermove/capture history della fonte reale |
| `search.h` + `search.cpp` | `StockfishSharp.Engine/Search.cs` | ⚠️ **in corso, Flow A1 di `porting-master-plan.md`** — negamax+PVS, quiescenza, TT (con `value_to_tt`/`value_from_tt` fedeli), mate distance pruning, null-move, RFP, LMR base, **aspiration windows, Razoring (Step 8), Futility pruning per mossa figlia (Step 9)**. NON portati: ProbCut, Singular Extensions, IIR, multi-cut, correction history, cutNode/allNode, Lazy SMP |
| `timeman.h` + `timeman.cpp` | inline in `StockfishSharp.Uci/Program.cs` (`HandleGo`) | ⚠️ gestione tempo semplice originale, non porting |
| `uci.h/cpp` + `ucioption.h/cpp` (~1500 righe insieme) | `StockfishSharp.Uci/Program.cs` | ⚠️ **layer minimo, non porting** — uci/isready/ucinewgame/position/go/setoption Hash/quit, sufficiente a giocare una partita reale via GUI/lichess-bot |

**Verificato**: 39/39 test (i 35 di prima + 4 nuovi su Search: matto in 1, matto in 2, nessun crash
sulla posizione iniziale a profondità 5, sceglie una cattura vincente su una torre indifesa
invece di mosse neutre). **Test end-to-end manuale via UCI reale**: il motore risponde a
`uci`/`isready`/`position`/`go movetime N` e gioca mosse d'apertura sensate (1.e4, poi 2.Cf3 dopo
1...e5) — un motore realmente giocabile via protocollo UCI, collegabile a lichess-bot o a
qualunque GUI, seppure ancora debole (valutazione placeholder, nucleo di ricerca parziale).

**Prossimo passo**: rinforzare la ricerca (ProbCut/Singular Extensions/aspiration windows, gli
stessi Step numerati già portati come ADATTAMENTI nel motore di ACMyChess questa sessione, qui da
portare FEDELMENTE), oppure passare a NNUE per sostituire la valutazione placeholder — le due
direzioni sono indipendenti, decidere in base a cosa dà più soddisfazione vedere funzionare prima.

### Fase 2 — Ricerca: `Search::Worker`, transposition table, move ordering — non iniziata

`tt.h`/`tt.cpp`, `movepick.h`/`movepick.cpp` (history euristiche, killer, countermove,
continuation history), `search.h`/`search.cpp` (negamax con tutte le potature/estensioni — lo
stesso codice usato come riferimento per gli Step numerati portati nel motore di ACMyChess questa
sessione, ma qui va portato per intero, non solo l'idea), `timeman.h`/`timeman.cpp` (gestione del
tempo), `thread.h`/`thread.cpp` (Lazy SMP multi-thread).

### Fase 3 — Valutazione classica (PSQT) — non iniziata

`evaluate.h`/`evaluate.cpp` nella parte non-NNUE (fallback per posizioni fuori dal training NNUE,
serve comunque per avere un motore funzionante prima di NNUE).

### Fase 4 — UCI — non iniziata

`uci.h`/`uci.cpp`, `ucioption.h`/`ucioption.cpp`, `engine.h`/`engine.cpp`, `benchmark.h`/`benchmark.cpp`.
Obiettivo verificabile: `position`/`go`/`stop`/`setoption` funzionanti, engine giocabile via una
GUI UCI qualunque (o lichess-bot, stesso schema di ACMyChess).

### Fase 5 — NNUE — non iniziata

`nnue/` (rete Big+Small, accumulatore incrementale, quantizzazione) — ~3.400 righe, la parte più
delicata da verificare (serve un oracolo: le reti `.nnue` ufficiali già scaricate per ACMyChess
in `../ACMyChess/Stockfish/stockfish/src/nn-*.nnue` sono compatibili, stesso formato binario).

### Fase 6 — Tablebase Syzygy — non iniziata

`syzygy/` (~2.050 righe) — probing WDL/DTZ, stesso formato file già scaricato per ACMyChess in
`../ACMyChess/Syzygy/`.

### Fase 7 — Rifinitura — non iniziata

Threading multi-core reale (Lazy SMP, non il root-splitting semplificato di ACMyChess),
`tune.cpp` (parametri tunable via UCI), confronto diretto nodi/mosse contro l'eseguibile C++
originale sulle stesse posizioni a profondità fissa, come criterio di accettazione finale per
ogni fase invece che solo "compila e i test passano".

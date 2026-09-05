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

### Fase 1 — Fondamenta: tipi, bitboard, tabelle di attacco — **IN CORSO**

| Sorgente | File C# | Stato |
|---|---|---|
| `types.h` | `StockfishSharp.Engine/Types.cs` | ✅ fatto (esclusi `DirtyPiece`/`DirtyThreat*`, rimandati alla Fase NNUE) |
| `bitboard.h` + `bitboard.cpp` | `StockfishSharp.Engine/Bitboards.cs` | ✅ fatto |
| `attacks.h` + `attacks.cpp` | `StockfishSharp.Engine/Attacks.cs` | ✅ fatto (solo ramo magic bitboard classico, vedi sopra) — **verificato**: 9/9 test, incluso confronto a forza bruta su 400 occupazioni casuali per casa (torre+alfiere) |
| `misc.h` (solo `PRNG`) | inline in `Attacks.cs` (`XorShift64StarRng`) | ✅ fatto, parziale — il resto di `misc.h` (timer, logging, syzygy path helpers) non ancora toccato |

**Prossimo passo immediato dentro la Fase 1**: `position.h`/`position.cpp` (rappresentazione
della posizione: bitboard per pezzo/colore, chiave Zobrist, stato di arrocco/en-passant/50 mosse,
`do_move`/`undo_move`) e `movegen.h`/`movegen.cpp` (generazione mosse pseudo-legali e legali).
Obiettivo verificabile: **perft** su posizioni note (iniziale, Kiwipete, posizioni 3-6 della suite
standard) con conteggi nodi identici ai valori di riferimento pubblicati — lo stesso standard
oggettivo di correttezza usato per qualunque move generator, indipendente da Stockfish stesso.

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

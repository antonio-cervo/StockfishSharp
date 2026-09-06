# StockfishSharp

Porting **reale** (non un adattamento) del motore [Stockfish](https://github.com/official-stockfish/Stockfish)
in C# / .NET 10 — stessa struttura, stessi algoritmi, stessi nomi dove ragionevole, tradotti da
C++ a C# mantenendo l'architettura originale invece di reinterpretarla in uno stile proprio (come
fatto invece nel motore di [ACMyChess](https://github.com/antonio-cervo/ACMyChess), progetto
sorella e ispirazione di questo).

Sorgente di riferimento: [official-stockfish/Stockfish](https://github.com/official-stockfish/Stockfish),
commit `edb0d9db6731067ec50ce619ff372b463bc4dd5d` ("Stockfish 19", 2026-09-05).

## Stato del porting

Motore completo e giocabile via protocollo UCI. I pilastri che determinano la forza di gioco
sono tutti portati fedelmente e verificati (test automatici + confronto diretto con l'eseguibile
Stockfish ufficiale su posizioni note):

- **Ricerca** — negamax con PVS, quiescenza, transposition table, aspiration windows, null-move,
  futility/razoring, LMR (formula reale con tabella logaritmica), ProbCut, Singular Extensions,
  Correction History, generazione delle mosse a stadi (`MovePicker`), tutte le history di
  `history.h` (main/continuation/capture/low-ply/pawn/TT-move).
- **Valutazione** — rete neurale NNUE (HalfKAv2Hm, FullThreats, Pp3Wide) con accumulatore
  aggiornato in modo incrementale (non ricalcolato da zero a ogni nodo) e percorso vettoriale
  AVX512.
- **Multi-thread** — Lazy SMP (transposition table condivisa fra i thread di ricerca, voto
  ponderato per scegliere la mossa del miglior thread).
- **Tablebase Syzygy** — probing WDL/DTZ completo (decodifica Huffman "Recursive Pairing",
  calcolo dell'indice di posizione, tutte le simmetrie), ordinamento delle mosse alla radice via
  DTZ, hook nel nodo di ricerca (non solo alla radice).
- **Protocollo UCI** — `uci`/`isready`/`ucinewgame`/`position`/`go`/`stop`/`setoption`/`bench`,
  comandi di debug (`d`/`eval`/`flip`/`compiler`/`perft`), libro di aperture Polyglot.

Resta da completare solo ciò che **non** incide sulla forza di gioco: `MultiPV` e gli handicap
volontari `UCI_Elo`/`Skill Level` (richiedono una struttura `RootMoves` completa, non ancora
costruita), e qualche utilità minore (`misc`/`score`). Dettaglio completo, file per file, con lo
stato di avanzamento di ogni singolo pezzo: [`docs/porting-master-plan.md`](docs/porting-master-plan.md)
(vista d'insieme) e i piani di dettaglio [`docs/nnue-porting-plan.md`](docs/nnue-porting-plan.md) /
[`docs/syzygy-porting-plan.md`](docs/syzygy-porting-plan.md).

## Struttura

- `StockfishSharp.Engine/` — libreria motore (bitboard, posizione, ricerca, valutazione NNUE,
  tablebase Syzygy, threading)
- `StockfishSharp.Uci/` — eseguibile UCI a riga di comando
- `StockfishSharp.Tests/` — test automatici (perft su posizioni standard, verifiche indipendenti
  stile "forza bruta" per i pezzi più delicati — SEE, accumulatore NNUE, tablebase — e confronto
  diretto con l'eseguibile Stockfish ufficiale dove possibile)
- `docs/` — piano di porting completo e stato di avanzamento, file per file

## Requisiti per compilare

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Facoltativo, per una valutazione reale invece del segnaposto materiale+PSQT: una rete NNUE
  ufficiale Stockfish (`.nnue`) — stesso file usato da una qualunque release ufficiale recente —
  copiata in `nnue-networks/` (percorso configurato in `StockfishSharp.Uci/Program.cs`). Senza,
  il motore si avvia comunque e gioca con la valutazione placeholder.
- Facoltativo, per il probing tablebase: file Syzygy (`.rtbw`/`.rtbz`) in una cartella qualsiasi,
  indicata poi con `setoption name SyzygyPath value <percorso>`.

```bash
dotnet build -c Release
dotnet test                      # suite di test
dotnet run --project StockfishSharp.Uci -c Release   # avvia il motore via UCI (stdin/stdout)
```

## Opzioni UCI principali

| Opzione | Default | Note |
|---|---|---|
| `Hash` | 16 (MB) | dimensione della transposition table |
| `Threads` | 8 | Lazy SMP — thread di ricerca |
| `SyzygyPath` | vuoto | cartella delle tabelle Syzygy; vuoto = probing disattivato |
| `SyzygyProbeDepth` | 1 | profondità minima per sondare quando ci sono più pezzi del limite |
| `Syzygy50MoveRule` | true | tiene conto della regola delle 50 mosse nel punteggio delle tablebase |
| `SyzygyProbeLimit` | 7 | numero massimo di pezzi per cui sondare |
| `UCI_Chess960` | false | varianti Chess960/Fischer Random |

## Licenza

Stockfish è distribuito sotto licenza [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0.html).
Questo è un lavoro derivato: stessa licenza, vedi [LICENSE](LICENSE). Copyright degli autori
originali di Stockfish (vedi `AUTHORS` nel repository upstream) per l'algoritmo/design portato;
le modifiche di porting sono di Antonio Cervo.

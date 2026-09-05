# StockfishSharp

Porting reale (non un adattamento) del motore [Stockfish](https://github.com/official-stockfish/Stockfish)
in C#/.NET — stessa struttura, stessi algoritmi, stessi nomi dove ragionevole, tradotti da C++ a
C# mantenendo l'architettura originale invece di reinterpretarla nel proprio stile (come fatto
invece nel motore di [ACMyChess](../ACMyChess), progetto sorella e ispirazione di questo).

Sorgente di riferimento: [official-stockfish/Stockfish](https://github.com/official-stockfish/Stockfish),
commit `edb0d9db6731067ec50ce619ff372b463bc4dd5d` (2026-09-05) — vedi `docs/porting-plan.md` per la
tabella di marcia completa, file per file, e lo stato di avanzamento.

## Licenza

Stockfish è distribuito sotto licenza [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0.html).
Questo è un lavoro derivato: stessa licenza, vedi [LICENSE](LICENSE). Copyright degli autori
originali di Stockfish (vedi `AUTHORS` nel repository upstream) per tutto l'algoritmo/design
portato; le modifiche di porting sono di Antonio Cervo.

## Struttura

- `StockfishSharp.Engine/` — libreria motore (bitboard, posizione, ricerca, valutazione NNUE,
  tablebase Syzygy)
- `StockfishSharp.Uci/` — eseguibile UCI a riga di comando
- `StockfishSharp.Tests/` — test (perft, posizioni note, confronto diretto coi valori dell'engine
  C++ originale dove possibile)
- `docs/porting-plan.md` — piano di porting completo e stato di avanzamento

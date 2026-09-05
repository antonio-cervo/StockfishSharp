# Piano di porting NNUE (Stockfish 19 → C#)

Documento di pianificazione scritto **prima** di scrivere codice, come per le altre fasi. Fonte:
`../stockfish-upstream-reference/src/nnue/` (commit `edb0d9d`, Stockfish 19, rilasciato il
2026-09-05).

## Stato della conoscenza (cosa ho già letto e capito)

| File fonte | Righe | Letto | Cosa contiene |
|---|---|---|---|
| `nnue_common.h` | 352 | ✅ | Costanti, tipi, `Version`, lettura little-endian, **decoder LEB128** |
| `nnue_architecture.h` | 174 | ✅ | Struttura rete, `propagate()` (forward L1→L2→L3→out), scelta feature set |
| `features/half_ka_v2_hm.h/.cpp` | 233 | ✅ | Feature PSQ classiche, `make_index` |
| `features/full_threats.h/.cpp` | 424 | ✅ | Feature minacce (59.808 dim), tabelle offset cumulativi |
| `features/pp_3wide.h/.cpp` | 309 | ✅ | Feature coppie di pedoni (4.560 dim) |
| `nnue_feature_transformer.h` | 440 | ✅ | Combinazione dei tre feature set, `transform_perspective` |
| `nnue_accumulator.h` | 142 | ✅ | Strutture dati accumulatore |
| `evaluate.cpp` | 105 | ✅ | Involucro finale (optimism, complexity, damping 50 mosse) |
| `nnue_accumulator.cpp` | 953 | ❌ | **Da leggere**: layout esatto delle righe di peso, somma dei tre feature set |
| `layers/affine_transform.h` | 426 | ❌ | **Da leggere**: solo il ramo scalare |
| `layers/affine_transform_sparse_input.h` | 445 | ❌ | **Da leggere**: solo il ramo scalare |
| `layers/clipped_relu.h` | 178 | ❌ | **Da leggere**: solo il ramo scalare |
| `layers/sqr_clipped_relu.h` | 247 | ❌ | **Da leggere**: solo il ramo scalare |
| `network.cpp` | 367 | 🟡 | Header/hash letti; resta la sequenza di caricamento completa |

## Scoperta che riduce molto lo scope

Gran parte delle righe di quei file è **codice SIMD** (AVX2/AVX512/NEON/RVV/LASX/wasm). Ogni
funzione ha un ramo scalare di riferimento (`#else`) molto più corto e leggibile. Portiamo **solo
lo scalare**, come già fatto altrove nel progetto (correttezza prima, velocità dopo).

Due conseguenze importanti:

1. **`permute_weights()` è un no-op nello scalare**: `PackusEpi16Order` vale `{0,1,2,3,4,5,6,7}`
   (identità) quando non ci sono vettori — la permutazione dei pesi si può saltare del tutto.
2. **`transform_perspective` scalare è 8 righe**: per ogni `j` in 0..511, `clamp(acc[j],0,255) *
   clamp(acc[j+512],0,255) / 512` → un byte. Due prospettive × 512 = 1024 input per i layer.

## Cosa NON portiamo (per ora, deliberatamente)

- **Aggiornamento incrementale dell'accumulatore** (`AccumulatorStack`, `forward/backward_update_
  incremental`) e le **Finny tables** (`AccumulatorCaches`): sono ottimizzazioni di velocità.
  Ricalcoliamo l'accumulatore da zero a ogni valutazione. Richiederebbero anche il tracciamento
  `DirtyPiece`/`DirtyThreats` in `Position`, deliberatamente saltato nella Fase 1.
- **Tutti i percorsi SIMD** e le varianti per CPU non-x86.
- `trace_evaluate`, `save()`, memoria condivisa fra thread, `NumaPolicy`.

## L'oracolo di verifica

`stockfish-reference-binary/stockfish/stockfish-windows-x86-64-universal.exe` (Stockfish 19
ufficiale). Il comando `eval` stampa, per **tutti e 8 i bucket**, due colonne indipendenti:

```
|   Bucket   |  Material (PSQT)  | Positional (Layers) |   Total   |
```

Sono due segnali di verifica **separati**: la colonna PSQT dipende solo da indici delle feature +
pesi PSQT (nessun layer), la colonna Positional solo dal forward pass. Questo permette di
verificare la metà del sistema prima di aver scritto l'altra metà.

Valori già raccolti (posizione iniziale, `bucket 7` — la formula è `(numPezzi-1)/4`):
`NNUE evaluation -1` (unità interne, lato al tratto), `Final evaluation +0.00`.

Conversione per il confronto: `valoreMostrato_pedoni = (raw / OutputScale) / 100` con
`OutputScale = 16`.

## Fasi di implementazione (ognuna con la sua verifica)

### N1 — Caricamento del file di rete
`NnueNetworkLoader.cs`: header (version `0x6A448AFA`, hash, descrizione), **decoder LEB128**,
lettura di tutti gli array di pesi nelle dimensioni corrette:

| Array | Tipo | Dimensione |
|---|---|---|
| `biases` | i16 | 1024 |
| `threatAndPpWeights` | **i8** | (59808 + 4560) × 1024 |
| `psqtWeights` | i32 | 8 × 22528 |
| `threatAndPpPsqtWeights` | i32 | (59808 + 4560) × 8 |
| `weights` | i16 | 1024 × 22528 |
| 8 × layer stack | vari | fc_0, ac_0, fc_1, ac_1, fc_2 |

**Verifica**: hash calcolato = hash nel file, e lo stream finisce **esattamente** a EOF dopo
l'ultimo layer (come fa `read_parameters`). Se il layout fosse sbagliato, non finirebbe a EOF.
Questa fase si verifica **senza scrivere una riga di matematica di valutazione**.

### N2 — Indici delle feature (i tre insiemi)
`NnueFeatures.cs`: `HalfKAv2_hm.MakeIndex`, `FullThreats.MakeIndex` + `AppendActiveIndices`,
`PP3Wide.MakeIndex` + `AppendActiveIndices`. Include le tabelle costruite a runtime che nella
fonte sono `constexpr` (offset cumulativi, LUT di compressione from/to).

**Verifica**: tutti gli indici < `Dimensions` del rispettivo insieme; conteggi coerenti su
posizioni note; nessun duplicato dove la fonte esclude i duplicati (`semi_excluded`).

### N3 — Accumulatore (ricalcolo da zero) + PSQT
`NnueAccumulator.cs`: parte dai bias, somma la riga di peso di ogni feature attiva (i16 per PSQ,
**i8** per threat/pawn-pair), e in parallelo accumula i valori PSQT (i32) per gli 8 bucket.

⚠️ Da risolvere leggendo `nnue_accumulator.cpp`: l'**ordine esatto di indicizzazione** delle
righe di peso (`weights[idx * 1024 + j]` o trasposto?) e l'ordine dei due array PSQT, che hanno
convenzioni scritte in modo diverso nel sorgente.

**Verifica**: la colonna **Material (PSQT)** dell'oracolo, per tutti e 8 i bucket, su un set di
posizioni di prova. Se combacia, indici + pesi + accumulazione sono giusti.

### N4 — `transform_perspective`
Quantizzazione a byte: clamp 0..255 a coppie, prodotto, `/512`. 1024 byte in uscita.

**Verifica**: indiretta (entra in N5), ma è il pezzo più semplice e meglio isolato.

### N5 — Forward pass dei layer
`NnueLayers.cs`: `fc_0` (1024→32, ramo scalare della versione "sparse input"), `sqr_clipped_relu`
+ `clipped_relu` → concatenazione 64, `fc_1` (64→32), attivazioni → concatenazione 128, `fc_2`
(128→1), più la **skip connection** (`fc_0_out[30] - fc_0_out[31]`) e la scalatura finale
`(fwdOut * 600 * 16) / (128 * 64 * 2)`.

**Verifica**: la colonna **Positional (Layers)** dell'oracolo, tutti e 8 i bucket.

### N6 — `evaluate()` finale
Involucro di `evaluate.cpp` (già letto per intero): blend optimism/complexity, scalatura per
materiale, damping sul contatore delle 50 mosse, clamp fuori dal range tablebase.

**Verifica**: riga **Final evaluation** dell'oracolo.

### N7 — Integrazione nel motore
Sostituisce `Evaluate.cs` (il placeholder materiale+PSQT). Attenzione: NNUE **non** va chiamata
sotto scacco (`assert(!pos.checkers())` nella fonte) — serve il fallback nella quiescenza, come
fa Stockfish.

**Verifica**: partita end-to-end via UCI; confronto delle mosse scelte con l'oracolo a profondità
fissa bassa su posizioni tattiche note.

### N8 — (dopo, opzionale) Prestazioni
Aggiornamento incrementale dell'accumulatore + Finny tables + eventualmente SIMD via
`System.Runtime.Intrinsics` (come già fatto per i magic bitboard AVX2). Solo dopo che N1-N7 sono
verificati: senza aggiornamento incrementale il motore sarà **corretto ma lento**.

## Ordine di lavoro consigliato

1. Leggere `nnue_accumulator.cpp` (solo i rami scalari e il layout dei pesi) → risolve l'unica
   incognita vera rimasta (N3).
2. Leggere i 4 file dei layer (solo rami scalari) → sono corti una volta tolto il SIMD.
3. Implementare N1 e verificarlo (caricamento + EOF esatto) prima di scrivere qualunque matematica.
4. N2 → N3 → verifica colonna PSQT.
5. N4 → N5 → verifica colonna Positional.
6. N6 → verifica Final evaluation.
7. N7 → integrazione e partita reale.

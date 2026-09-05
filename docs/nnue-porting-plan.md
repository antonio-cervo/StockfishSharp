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
funzione ha anche un ramo scalare di riferimento (`#else`) molto più corto e leggibile.

**Portiamo entrambi: prima lo scalare, poi AVX512ICL** — stesso schema già usato con successo per
i magic bitboard in `Attacks.cs` (dove il percorso AVX2 è verificato sia contro la forza bruta sia
contro il percorso classico). L'ordine non è negoziabile per un motivo pratico: se si scrive
prima il SIMD e il risultato è sbagliato, non si distingue un errore di comprensione
dell'algoritmo da un errore di intrinseche. Con lo scalare già verificato contro l'oracolo, il
percorso SIMD ha un secondo riferimento indipendente contro cui essere controllato.

Il bersaglio SIMD è **AVX512ICL** (non AVX2): è quello che la CPU di questa macchina supporta ed
è lo stesso percorso che esegue l'oracolo — vedi `porting-master-plan.md` per la verifica
empirica di quali sottoinsiemi .NET 10 espone davvero, e per l'unica limitazione trovata
(`Avx512Vnni` non esiste in .NET: si usa la variante `maddubs`+`madd` che Stockfish ha già).

Lo scalare **resta in pianta stabile** nel codice, non è codice usa-e-getta: serve da fallback su
CPU senza AVX2 e da oracolo nei test.

Due conseguenze di partire dallo scalare:

1. **`permute_weights()` è un no-op nello scalare**: `PackusEpi16Order` vale `{0,1,2,3,4,5,6,7}`
   (identità) quando non ci sono vettori. ⚠️ Ma con AVX512 diventa `{0,2,4,6,1,3,5,7}`: la
   permutazione dei pesi **va implementata** per il percorso SIMD. Facendo selezione a runtime
   (non a tempo di compilazione come la fonte), la scelta naturale è permutare **al caricamento**
   in base al percorso scelto.
2. **`transform_perspective` scalare è 8 righe**: per ogni `j` in 0..511, `clamp(acc[j],0,255) *
   clamp(acc[j+512],0,255) / 512` → un byte. Due prospettive × 512 = 1024 input per i layer.
   La versione SIMD fa lo stesso con il trucco `packus`+`mulhi` descritto nel commento della
   fonte (shift a sinistra di 7 + `mulhi` = divisione per 512 con il segno preservato).

## Cosa NON portiamo (per ora, deliberatamente)

- **Aggiornamento incrementale dell'accumulatore** (`AccumulatorStack`, `forward/backward_update_
  incremental`) e le **Finny tables** (`AccumulatorCaches`): sono ottimizzazioni di velocità.
  Ricalcoliamo l'accumulatore da zero a ogni valutazione. Richiederebbero anche il tracciamento
  `DirtyPiece`/`DirtyThreats` in `Position`, deliberatamente saltato nella Fase 1. Rimandato a
  N9, non abbandonato.
- **I percorsi SIMD non-x86**: NEON (ARM), RVV (RISC-V), LASX/LSX (LoongArch), wasm. E anche
  AVX2, che diventa superfluo: la macchina di destinazione ha AVX512ICL, che è quello che
  portiamo in N8. (Se un domani servisse girare su una CPU solo-AVX2, il fallback è lo scalare —
  corretto ma lento — oppure si aggiunge il livello AVX2 allora.)
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

### N3 — Accumulatore (ricalcolo da zero) + PSQT — ✅ FATTO E VERIFICATO
`NnueAccumulator.cs`: parte dai bias, somma la riga di peso di ogni feature attiva (i16 per PSQ,
**i8** per threat/pawn-pair), e in parallelo accumula i valori PSQT (i32) per gli 8 bucket.
Layout confermato leggendo `nnue_accumulator.cpp` (funzioni `apply_psq_features`/`apply_psqt`):
`weights[featureIdx * 1024 + j]` e `psqtWeights[featureIdx * 8 + bucket]` — esattamente come
scritto in `NnueNetwork.cs` durante N1, nessuna trasposizione.

Nota su `permute_weights()` (nnue_feature_transformer.h): la fonte permuta biases/weights/
threatAndPpWeights al caricamento SOLO quando compilata con SIMD (`PackusEpi16Order` non-identità
con AVX512/AVX2). Nel nostro percorso scalare l'ordine resta l'identità — la permutazione è un
dettaglio di layout per i trick SIMD (`packus`), matematicamente reversibile e ininfluente sul
risultato finale: non va replicata finché non si porta N8 (AVX512ICL), dove weights e accumulatore
andranno permutati in modo coerente fra loro.

**Verifica FATTA** contro l'eseguibile ufficiale (comando `eval`), colonna **Material (PSQT)**,
tutti e 8 i bucket: posizione iniziale (tutti 0.00, per simmetria) e una posizione asimmetrica
(Kiwipete-style) — combaciano esattamente (`+1.71 -0.06 -0.23 -0.42 -0.46 -0.48 -0.53 -0.49`).
Per il confronto in "pedoni" è servito portare anche `win_rate_params`/`to_cp` (uci.cpp) in
`WinRateModel.cs` — la normalizzazione NON è una divisione fissa per 100 come ipotizzato
inizialmente, dipende dal materiale in campo (modello WDL).

### N4 — `transform_perspective` — ✅ FATTO
Quantizzazione a byte: clamp 0..255 a coppie, prodotto, `/512`. 1024 byte in uscita (512 per
prospettiva, propria prima poi avversaria — `perspectives[2] = {stm, ~stm}`).

**Verifica**: indiretta via N5 (vedi sotto) — combacia.

### N5 — Forward pass dei layer — ✅ FATTO E VERIFICATO
`NnueLayers.cs`: `fc_0` (1024→32, ramo scalare della versione "sparse input" — nello scalare è
IDENTICO alla `AffineTransform` normale, stessa funzione `affine_transform_non_ssse3` nella fonte),
`sqr_clipped_relu` + `clipped_relu` → concatenazione 64, `fc_1` (64→32), attivazioni →
concatenazione 128, `fc_2` (128→1), più la **skip connection** (`fc_0_out[30] - fc_0_out[31]`,
sull'uscita GREZZA i32 di fc_0, non quella attivata) e la scalatura finale
`(fwdOut * 600 * 16) / (128 * 64 * 2)` in aritmetica a 64 bit.

Layout dei pesi confermato leggendo `affine_transform.h`/`affine_transform_sparse_input.h`:
`weights[outIdx*inputDim + inIdx]`, identità nella lettura (nessuna macro `USE_*` = nessuno
scrambling) — stesso schema già visto in N1/N3.

**Verifica FATTA** contro l'oracolo, colonna **Positional (Layers)**, tutti e 8 i bucket, sulla
stessa posizione asimmetrica di N3: combaciano esattamente al primo tentativo
(`-0.70 -1.22 -0.98 -1.28 -1.37 -1.39 -1.36 -1.49`).

### N6 — `evaluate()` finale — ✅ FATTO E VERIFICATO
`NnueEvaluate.cs`: involucro di `evaluate.cpp` — blend optimism/complexity, scalatura per
materiale (`non_pawn_material()` ricalcolato al volo dai conteggi pezzi, mai stato un campo
incrementale su `Position` — non serve a perft/do_move di base), damping sul contatore delle 50
mosse, clamp fuori dal range tablebase.

**Verifica FATTA** contro la riga **Final evaluation** dell'oracolo, tre posizioni (iniziale,
mediogioco asimmetrico, finale di pedoni/torre — bucket 7/7/2 rispettivamente): combaciano
esattamente (`+0.00`, `-2.48`, `+0.48`).

### N7 — Integrazione nel motore — ✅ FATTO E VERIFICATO
`Evaluate.StaticEval` delega a `Nnue.NnueEvaluate` quando `Evaluate.NnueNetwork` è impostato
(fatto da `StockfishSharp.Uci/Program.cs` all'avvio, se il file `.nnue` è presente — altrimenti
resta il placeholder materiale+PSQT). Il placeholder non è stato rimosso: resta fallback per i
test di `Search` (che non caricano la rete da ~100MB) e per l'eventuale caso "rete non trovata".
Il vincolo "NNUE non va chiamata sotto scacco" (`assert(!pos.checkers())` nella fonte) è già
rispettato: entrambi i punti di chiamata in `Search.cs` (riga 113, 194) usano un ternario
`inCheck ? ... : Evaluate.StaticEval(pos)` che non valuta il ramo NNUE quando in scacco.

**Verificato**: 54/54 test (nessuna regressione). **Partita end-to-end via UCI reale** con la rete
caricata: `info string NNUE evaluation using nn-1a298aa575a0.nnue`, poi mosse sensate su tre
posizioni (apertura, dopo 1.e4 e5, un mediogioco). **Confronto con l'oracolo** (stesso eseguibile
Stockfish 19 di riferimento, stessa profondità fissa): posizione iniziale a depth 6 → `e2e4`
combacia; dopo 1.e4 e5 a depth 5 → `g1f3` combacia; terza posizione (mediogioco) a depth 8 →
diverge (`b1c3` contro `a2a3` dell'oracolo). La divergenza sulla terza è attesa e non è un bug di
N7: la ricerca è ancora il nucleo parziale di Flow A1 (mancano ProbCut, Singular Extensions,
aspiration windows, ecc.) — l'accordo pieno con l'oracolo è il criterio di fine progetto, non di
questa fase, che riguarda solo l'integrazione della valutazione.

### N8 — Percorso AVX512ICL (obiettivo, non extra)
Porting dei rami `USE_AVX512*` di `transform_perspective`, `AffineTransform`,
`AffineTransformSparseInput` (con `nnz_helper.h`), `ClippedReLU`, `SqrClippedReLU`, via
`System.Runtime.Intrinsics.X86.Avx512F/BW/DQ/Vbmi/Vbmi2` — stesso approccio già usato in
`Attacks.cs` per AVX2. Include la permutazione dei pesi al caricamento
(`PackusEpi16Order = {0,2,4,6,1,3,5,7}`), che nello scalare non serviva.

Verificato empiricamente che .NET 10 espone tutto il necessario **tranne `Avx512Vnni`**: per i
prodotti scalari int8 dei layer si porta la variante senza VNNI (`maddubs_epi16` + `madd_epi16`),
che è già presente nel `simd.h` della fonte. Vedi `porting-master-plan.md` per la tabella
completa dei sottoinsiemi e per l'assunzione sulla saturazione int16 che questo comporta.

Selezione a **runtime** (`Avx512F.IsSupported && Avx512Vbmi2.IsSupported && ...`), non a tempo di
compilazione come la fonte: un solo binario che usa il meglio disponibile, con lo scalare come
fallback.

**Verifica** (doppia, come per i magic bitboard):
1. SIMD contro **lo scalare già verificato**, su molte posizioni casuali — devono dare valori
   identici bit per bit, non "simili".
2. SIMD contro **l'oracolo**, sulle stesse posizioni di N3/N5/N6. Qui il confronto è
   particolarmente pulito perché è lo **stesso percorso ISA** che esegue l'oracolo (a meno di
   VNNI).

Il guadagno atteso è grande: NNUE è il percorso più caldo del motore, lo scalare sarà
plausibilmente un ordine di grandezza più lento.

### N9 — (dopo) Aggiornamento incrementale
`AccumulatorStack` + Finny tables + tracciamento `DirtyPiece`/`DirtyThreats` in `Position`. È
l'altra metà delle prestazioni: senza, ogni nodo ricalcola l'accumulatore da zero. Da fare solo
quando N1-N8 sono verificati e stabili.

## Ordine di lavoro consigliato

1. Leggere `nnue_accumulator.cpp` (rami scalari + layout dei pesi) → risolve l'unica incognita
   vera rimasta (N3).
2. Leggere i 4 file dei layer (rami scalari) → sono corti una volta tolto il SIMD.
3. Implementare N1 e verificarlo (caricamento + EOF esatto) prima di scrivere qualunque matematica.
4. N2 → N3 → verifica colonna PSQT.
5. N4 → N5 → verifica colonna Positional.
6. N6 → verifica Final evaluation.
7. N7 → integrazione e partita reale.
8. **N8 → AVX512ICL**, verificato contro lo scalare (che resta come fallback e come oracolo nei
   test).
9. N9 → aggiornamento incrementale, quando tutto il resto è stabile.

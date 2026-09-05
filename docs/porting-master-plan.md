# Piano generale di porting — Stockfish 19 → C#

Documento di riferimento che tiene insieme tutto il progetto. I due piani di dettaglio sono
`porting-plan.md` (fasi 1-3, già eseguite) e `nnue-porting-plan.md` (fasi N1-N9).

Fonte: `../stockfish-upstream-reference/src/`, commit `edb0d9d` = **Stockfish 19**, rilasciato il
2026-09-05 (lo stesso giorno in cui è iniziato questo porting).

## Stato reale, in numeri

**Sorgente totale**: 24.849 righe (`.cpp` + `.h`, escluso `incbin/`).
**Righe lette finora**: ~6.600 → **27%**.

Questo numero è il punto di partenza onesto del piano: quasi tre quarti del sorgente non è
ancora stato aperto, e alcune parti già "consegnate" non sono porting veri (vedi Flusso A).

## Le tre categorie di lavoro rimanente

| Flusso | Cosa | Righe fonte | Perché |
|---|---|---|---|
| **A — Debito** | Rifare come porting fedele parti che ho scritto di mio pugno | ~6.000 | Consegnate come "fatte" ma sono codice originale, non traduzioni |
| **B — NNUE** | Valutazione neurale | ~5.700 | Mai iniziato; piano di dettaglio già scritto |
| **C — Resto** | Threading, tablebase, utilità | ~4.000 | Mai toccato |

---

## Flusso A — Il debito (parti da rifare davvero)

Queste parti **funzionano** e sono etichettate onestamente nei commenti/commit come codice
originale, ma rispetto all'obiettivo "porting reale" sono segnaposto, non lavoro finito.

### A1 — Ricerca (`search.h` 439 + `search.cpp` 2.369 = 2.808 righe)
**Oggi**: `Search.cs`, ~200 righe scritte da me — negamax+PVS, quiescenza, TT, mate distance
pruning, null-move, RFP, LMR base.
**Manca**: ProbCut (entrambe le varianti), Singular Extensions, aspiration windows, internal
iterative reduction, futility per mossa, razoring, multi-cut, correction history, tutta la
taratura fine dei margini, la struttura `Worker`/`RootMove`/`Stack` della fonte.
⚠️ È il file più grande del progetto. Da solo vale più di tutto quello portato finora.

### A2 — Ordinamento mosse (`movepick.h` 78 + `movepick.cpp` 383 + `history.h` 261 = 722 righe)
**Oggi**: `MovePick.cs`, ~70 righe — TT move, SEE, killer, history semplice.
**Manca**: generazione a stadi (la fonte non genera tutte le mosse in una volta), continuation
history, countermove history, capture history, pawn history, le formule di bonus/malus.

### A3 — Gestione del tempo (`timeman.h` 70 + `timeman.cpp` 144 = 214 righe)
**Oggi**: ~15 righe dentro `Program.cs`.
**Manca**: il modello a due livelli optimum/maximum, `nodestime`, ponder, move overhead.

### A4 — Livello UCI (`uci.cpp` 704 + `ucioption.cpp` 213 + `engine.cpp` + `benchmark.cpp` ≈ 1.600)
**Oggi**: ~180 righe in `Program.cs` — i comandi minimi per giocare.
**Manca**: infrastruttura opzioni generica, `setoption` completo, `bench`, `MultiPV`,
`UCI_LimitStrength`/`UCI_Elo`, `UCI_ShowWDL`, conversione punteggi WDL, `Skill Level`, `d`,
`flip`, `compiler`, `export_net`.

### A5 — Parti non lette di `position.cpp` (~700 righe)
`is_draw`/`is_repetition`/`upcoming_repetition`/`has_repeated` + **tabelle cuckoo** (rilevazione
veloce delle ripetizioni), `pos_is_ok`, `material_key_is_ok`, `flip`, `dtz_is_dtm`, e tutta la
macchina `update_piece_threats`/`DirtyThreats` — quest'ultima è **prerequisito di N9**.

---

## Flusso B — NNUE

Piano di dettaglio in `nnue-porting-plan.md` (fasi N1-N9). Riassunto:

N1 caricamento file → N2 indici feature → N3 accumulatore + **verifica colonna PSQT** → N4
quantizzazione → N5 layer → **verifica colonna Positional** → N6 involucro `evaluate()` → **verifica
Final evaluation** → N7 integrazione → N8 **AVX2** → N9 aggiornamento incrementale.

Da leggere ancora: `nnue_accumulator.cpp` (953), i 4 file dei layer (1.296), `nnz_helper.h` (171),
`network.cpp` (resto), `simd.h` (532, solo come riferimento per le intrinseche).

---

## Flusso C — Il resto

### C1 — Threading (`thread.h`/`thread.cpp`, 464+ righe)
Lazy SMP. Prerequisito: N9 (accumulatore per-thread). Oggi il motore è a thread singolo.

### C2 — Tablebase Syzygy (`syzygy/`, 2.053 righe)
Mai aperto. I file di dati sono già disponibili in `../ACMyChess/Syzygy/`.

### C3 — Utilità (`misc`, `memory`, `score`, `numa`, `tune`, `universal/`, ~1.500 righe)
Portate finora solo le briciole che servivano (`PRNG` dentro `Attacks.cs`).

---

## L'oracolo: cosa garantisce e cosa no

`stockfish-reference-binary/.../stockfish-windows-x86-64-universal.exe`, **Stockfish 19**
ufficiale. Interrogato con `compiler` su questa macchina risponde:

```
Compilation architecture : x86-64-avx512icl
Compilation settings     : 64bit AVX512ICL VNNI AVX512 BMI2 AVX2 SSE41 SSSE3 SSE2 POPCNT
```

**Conseguenza importante**: il binario "universal" rileva la CPU e sceglie il percorso migliore —
su questa macchina esegue i rami **AVX512ICL**, cioè un terzo percorso, diverso sia dallo scalare
che porteremo per primo sia dall'AVX2 che porteremo dopo.

Stockfish progetta tutti i percorsi ISA per dare risultati **identici** (le reti devono valutare
uguale su qualunque macchina, altrimenti il loro framework di test non funzionerebbe). La
permutazione dei pesi esiste proprio per questo, e ha tre ordini diversi:

| Percorso | `PackusEpi16Order` |
|---|---|
| scalare | `{0,1,2,3,4,5,6,7}` (identità) |
| AVX2 | `{0,2,1,3,4,6,5,7}` |
| AVX512 | `{0,2,4,6,1,3,5,7}` |

⚠️ **Assunzione da tenere presente, non dimostrata**: che scalare e AVX512ICL diano lo stesso
identico intero. Se durante N3/N5/N6 il mio scalare dovesse differire dall'oracolo di ±1 unità in
alcune posizioni, la **prima ipotesi da verificare non è "il mio codice è rotto"** ma "divergenza
scalare/SIMD dentro Stockfish". Come si distingue: si completa N8 e si confronta; se il percorso
SIMD combacia con l'oracolo e lo scalare no, la divergenza è nella fonte ed è un risultato
interessante da documentare, non un bug nostro.

### Bersaglio SIMD: AVX512ICL (deciso dopo verifica empirica)

Il bersaglio SIMD **non è AVX2 ma AVX512ICL**, lo stesso percorso che esegue l'oracolo. Vantaggio
non secondario: il confronto con l'oracolo diventa diretto, senza l'ambiguità
scalare-contro-SIMD descritta sopra.

Quali sottoinsiemi .NET 10 espone davvero, verificato eseguendo un programma di prova su questa
macchina (non dedotto dalla documentazione):

| Sottoinsieme | Esposto da .NET 10 | Supportato dalla CPU |
|---|---|---|
| `Avx2` | sì | sì |
| `Avx512F` / `BW` / `DQ` / `CD` | sì | sì |
| `Avx512Vbmi` | sì | sì |
| **`Avx512Vbmi2`** | **sì** | **sì** |
| `Gfni` | sì | sì |
| `AvxVnni` | sì | **no** (la CPU non ce l'ha) |
| **`Avx512Vnni`** | **il tipo non esiste in .NET 10** | — |
| `Avx512Bitalg` | il tipo non esiste in .NET 10 | — |

Due conseguenze operative:

1. ✅ **`Avx512Vbmi2` c'è**: è quello che serve per `VPCOMPRESSB`
   (`_mm512_maskz_compress_epi8`), l'istruzione su cui poggiano i rami `USE_AVX512ICL` della
   generazione indici (`half_ka_v2_hm::write_indices`, `pp_3wide`) e delle mosse
   (`movegen.cpp::splat_*`). Era il rischio tecnico principale ed è rientrato.
2. ⚠️ **`Avx512Vnni` non è raggiungibile da C#**: niente `VPDPBUSD` per i prodotti scalari int8
   dei layer. Non è un blocco — Stockfish ha già nel suo `simd.h` la variante **senza VNNI**
   (`maddubs_epi16` + `madd_epi16` + somma), che è un suo percorso legittimo. Portiamo quella.

Il bersaglio effettivo è quindi **AVX512ICL meno VNNI**.

⚠️ **Assunzione aggiuntiva da questa scelta**: l'oracolo usa VNNI, il nostro porting no. Le due
sequenze sono matematicamente equivalenti, ma `maddubs_epi16` satura a int16 mentre `VPDPBUSD`
accumula in int32 senza quella saturazione intermedia. Nel primo layer i valori sono vincolati in
modo che la saturazione non possa avvenire (per questo Stockfish si permette entrambe le
varianti), ma se emergesse una divergenza nei layer, **questa è la prima cosa da controllare**.

---

## Ordine consigliato dei flussi

1. **B (NNUE) fino a N7** — è la cosa che cambia di più il motore: oggi la valutazione è un
   segnaposto materiale+PSQT, con NNUE diventa un motore vero. Ha un piano dettagliato e una
   verifica solida (l'oracolo a due colonne).
2. **N8 (AVX512ICL meno VNNI)** — senza, NNUE sarà corretto ma lento.
3. **A1 (ricerca)** — il pezzo singolo più grande del progetto, ma anche quello dove il divario
   fra il mio segnaposto e la fonte vera è più grande in forza di gioco.
4. **A2 (move ordering)** — strettamente legato ad A1, conviene farli vicini.
5. **A5** (`DirtyThreats`) → **N9** (accumulatore incrementale) → **C1** (Lazy SMP): sono in
   catena, in quest'ordine.
6. **A3, A4** (tempo, UCI) — meno urgenti: le versioni attuali funzionano, il divario è in
   completezza di funzioni, non in forza.
7. **C2** (Syzygy), **C3** (utilità) — alla fine.

## Come si misura la fine

Il criterio di completamento del progetto non è "tutti i file portati", ma:
**a parità di posizione, profondità fissa e opzioni, StockfishSharp e l'eseguibile ufficiale
scelgono la stessa mossa e visitano lo stesso numero di nodi.** È il test che Stockfish stesso
usa fra build diverse (`bench`), ed è l'unico che dimostra che il porting è fedele davvero.

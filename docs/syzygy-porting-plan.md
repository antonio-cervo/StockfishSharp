# Porting Syzygy (C2) — piano di dettaglio

Fonte: `syzygy/tbprobe.h` (85 righe) + `syzygy/tbprobe.cpp` (1.968 righe) = 2.053 righe.
Letto per intero riga per riga prima di iniziare (stesso metodo di N1-N9 per NNUE).

Dati disponibili: tabelle fino a 5 pezzi già scaricate in `../ACMyChess/Syzygy/` (290 file
.rtbw/.rtbz). Se in futuro servono tabelle a 6-7 pezzi si scaricano — nessuna limitazione
voluta nel porting per fermarsi a 5 pezzi, il codice è fedele fino a TBPIECES=7 come la fonte.

Oracolo per la verifica: `stockfish-reference-binary/stockfish/stockfish-windows-x86-64-universal.exe`
(supporta SyzygyPath/SyzygyProbeDepth/Syzygy50MoveRule/SyzygyProbeLimit) puntato sulla stessa
cartella dati — a parità di posizione deve dare lo stesso WDL/DTZ/bestmove.

## Deviazione dichiarata: niente mmap reale

La fonte mappa i file .rtbw/.rtbz in memoria con mmap()/CreateFileMapping() — tecnica di
prestazioni (evita di caricare l'intero file in RAM, lascia che il sistema operativo lo
paginizzi on-demand), non parte dell'algoritmo di decodifica in sé (stesso principio già
applicato a C1: NUMA/thread nativi omessi perché "meccanica del sistema operativo", non
logica). Le tabelle attualmente disponibili (fino a 5 pezzi) sono piccole (KB-basse MB);
si legge il file intero in un `byte[]` invece di usare `MemoryMappedFile` di .NET. Tutta
l'aritmetica di indicizzazione (offset nei vari array) è portata fedele, cambia solo il
mezzo di accesso (indice in un array invece di puntatore in una regione mappata). Se in
futuro servissero tabelle a 6-7 pezzi (GB, impraticabili da caricare intere) passare a
`MemoryMappedFile` sarebbe un cambiamento meccanico confinato a `TbFile`, non all'algoritmo.

## Deviazione dichiarata: template C++ → interfaccia C#

`TBTable<Type>`/`do_probe_table<T>`/`probe_table<Type>` usano specializzazione di template
C++ per differenziare WDL (Ret=WDLScore, Sides=2) da DTZ (Ret=int, Sides=1) e le funzioni
`check_dtz_stm`/`map_score`/`set_dtz_map` (overload diversi per tipo). Qui due classi
concrete `TbTableWdl`/`TbTableDtz` implementano un'interfaccia comune `ITbTable` con i
campi condivisi, più i metodi `CheckDtzStm`/`MapScore` come override — stesso dispatch a
tempo di compilazione della fonte, espresso con l'idioma C# invece del C++.

## Fasi

- **TB1**: Tipi di base — `WdlScore` enum, `ProbeState` enum, `TbType`, `TbFlag`, `Config`
  struct, `PairsData` classe (campi 1:1 con la fonte, puntatori raw → array + indice int).
- **TB2**: Tabelle combinatorie — `MapPawns`/`MapB1H1H7`/`MapA1D1D4`/`MapKK`/`Binomial`/
  `LeadPawnIdx`/`LeadPawnsSize`, costruite una volta in `Init()` (stessa logica di
  generazione della fonte, righe 1545-1637).
- **TB3**: `TbFile` (ricerca nei path, apertura, lettura in `byte[]`) + `TbTableWdl`/
  `TbTableDtz` (metadati, popolati al momento dell'aggiunta) + `TbTables` (registro
  hash Robin Hood, righe 491-603).
- **TB4**: `Init()` completo — scansione di tutte le combinazioni di pezzi (righe
  1639-1683), popola TB2+TB3.
- **TB5**: `Set()`/`SetGroups()`/`SetSymlen()`/`SetSizes()`/`SetDtzMap()` — parsing del
  layout del file al primo accesso (righe 1023-1381). Fase più delicata insieme a TB6:
  bitfield/allineamenti/ordine dei campi devono essere byte-esatti.
- **TB6**: `DecompressPairs()` — decoder Huffman "Recursive Pairing" (righe 605-744).
- **TB7**: `DoProbeTable()` — calcolo dell'indice di posizione (simmetrie/gruppi/binomiali,
  righe 793-1021) + probe vero e proprio.
- **TB8**: `Mapped()`/`ProbeTable()` + `Search<CheckZeroingMoves>()` (righe 1442-1513) +
  `ProbeWdl`/`ProbeDtz` pubblici (righe 1685-1784).
- **TB9**: `RootProbe`/`RootProbeWdl`/`RankRootMoves` (righe 1787-1965) — la fonte opera su
  `Search::RootMoves` complete (con `pv`/`tbRank`/`tbScore`), struttura che questo porting
  non ha ancora (stesso prerequisito mancante già annotato per MultiPV in A4 e per
  `get_best_thread` in C1). Semplificazione: una struttura locale minima
  `(Move move, int tbRank, int tbScore)` sulle sole mosse legali alla radice, stessa
  formula di ranking DTZ/WDL della fonte.
- **TB10**: Wiring — opzioni UCI `SyzygyPath`/`SyzygyProbeDepth`/`Syzygy50MoveRule`/
  `SyzygyProbeLimit`; hook nel nodo di ricerca (search.cpp:922-973, Step 7) tramite un
  `TbConfig` su `Search`; hook alla radice (bias sulla scelta della mossa quando la
  posizione rientra nel cardinality configurato).

## Verifica

Per ogni fase: `dotnet test` (nessuna regressione), più per TB6-TB9 confronto diretto
WDL/DTZ/bestmove contro l'oracolo (`stockfish-windows-x86-64-universal.exe` con
`SyzygyPath` puntato alla stessa cartella) su un set di posizioni di finale note (KPvK,
KQvK, KRvKP, KBNvK — il matto "difficile" cavallo+alfiere è un buon caso limite per DTZ).

## Stato — TB1-TB8 FATTI E VERIFICATI (2026-09-06)

Motore di probing completo (WDL+DTZ) implementato in `StockfishSharp.Engine/Tablebases/`
(`TbTypes.cs`, `PairsData.cs`, `TbFile.cs`, `TbTable.cs`, `TbTables.cs`, `Tablebase.cs`).
Verificato: `CombinatorialTablesAreConsistent` (biiezioni MapA1D1D4/MapB1H1H7, coefficienti
binomiali standard), `KQvKWhiteToMoveIsWin`/`BlackToMoveIsLoss`/`KvKIsAlwaysDraw`/
`KPvKPromotingPawnIsWin`/`KRvKMateIn1`/`KNNvKIsDraw`/`KBBvKBlackToMoveIsLoss` (WDL/DTZ
confrontati con l'oracolo Stockfish reale su materiali diversi: con/senza pedoni, con/senza
pezzo "unico" — coprono entrambi i rami di indicizzazione di `do_probe_table`, sia
`hasUniquePieces` sia `MapKK`) + `KQvKDtzReachesMateWithinDeclaredDistance` (matto raggiunto
seguendo solo `ProbeDtz`, nessuna euristica di scacchi).

**Bug reale trovato e corretto**: `TBFile::map` nella fonte ritorna `data + 4` (salta i 4
byte del magic number, MA il puntatore resta nello stesso buffer, quindi l'allineamento a 64
byte più avanti nel parsing — `(data+0x3F)&~0x3F` in `set()`, tbprobe.cpp:1370 — arrotonda
nella griglia di allineamento del FILE VERO, che comprende il magic). Il primo porting
invece "spogliava" l'array dei 4 byte di magic (`raw[4..]`) e faceva partire il cursore da 0
— stesso valore logico ma SFASATO di 4 byte rispetto alla vera griglia a 64: l'arrotondamento
a 64 in questo array "accorciato" può produrre un `DataOffset` diverso da quello vero (il
salto di 4 byte non attraversa sempre lo stesso multiplo di 64), portando a leggere i dati
Huffman compressi 4 byte fuori posto — sintomo osservato: un DTZ plausibile ma sbagliato
(matto-in-1 dato come "3" invece di "1"), con indice di posizione, tabelle combinatorie e
flags tutti verificati corretti a mano. Trovato confrontando passo-passo con **python-chess**
(`chess.syzygy`, libreria Python indipendente, terzo oracolo installato al volo con `pip
install chess` per questa verifica) sulla stessa identica posizione e stessi file: bit a bit
identico fino al calcolo del blocco/offset sparso, poi divergente proprio al campo
`DataOffset`/`d.data`. Fix: `TbFile.ReadTable` ritorna ora l'array COMPLETO (magic incluso),
`TbTable.Populate` fa partire il cursore a 4 invece che a 0 — stessa tecnica della fonte
(stesso buffer, puntatore avanzato di 4), non più un array "accorciato" con la propria
numerazione indipendente.

**Resta da fare**: TB10 (wiring: opzioni UCI
`SyzygyPath`/`SyzygyProbeDepth`/`Syzygy50MoveRule`/`SyzygyProbeLimit`, hook nel nodo di
ricerca via `TbConfig` su `Search`, Step 7 di search.cpp).

## TB9 FATTO E VERIFICATO (2026-09-06, stessa sessione)

`RootProbe`/`RootProbeWdl`/`RankRootMoves` in `Tablebase.cs`, più `TbRootMove` (sostituto
minimo di `Search::RootMove` — solo `Move`/`TbRank`/`TbScore`, non l'intero PV) in
`TbTypes.cs`. `DtzIsDtm` (`Position::dtz_is_dtm`, position.h:346-349) come helper privato
(non un metodo di `Position`, usato solo qui). `RankRootMoves` non ha `OptionsMap` (non
presente in questo porting): le tre opzioni UCI (`Syzygy50MoveRule`/`SyzygyProbeDepth`/
`SyzygyProbeLimit`) diventano parametri espliciti, da passare dal chiamante quando arriverà
il wiring (TB10). `std::stable_sort` → LINQ `OrderByDescending` (garantito stabile).

Verificato: `RankRootMovesPicksSameMoveAsOracle` — su KQvK, confronta con l'oracolo Stockfish
reale (stesse opzioni UCI: Syzygy50MoveRule=true, SyzygyProbeDepth=1, SyzygyProbeLimit=7).
L'oracolo sceglie `e1h4` ("mate 2"); il nostro port classifica **tre** mosse (Qb4/Qe5/Qh4)
allo stesso `TbRank` massimo (DTZ=3, matto ugualmente veloce) — lo spareggio fra mosse
equivalenti dipende dall'ordine di generazione delle mosse, che differisce naturalmente da
un'implementazione all'altra (confermato sondando `RankRootMoves` direttamente: tutte e tre
riportano `rank=262141`), quindi il test verifica che `e1h4` sia fra le mosse in cima a pari
merito, non che sia esattamente la prima — la correttezza è nel trovare il DTZ minimo, non
nel tie-break arbitrario. 109 test totali, nessuna regressione.

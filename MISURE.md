# Misure di velocita': come si fanno qui, e quelle rifatte

## Il metodo

`tools/misura_appaiata.py`. A e B girano **back-to-back** alternando l'ordine, e la coppia e'
l'unita' di misura. Si guarda soprattutto il **conteggio delle direzioni**: 9 coppie su 10 e' un
risultato, +3% di media con 5 su 10 e' rumore. Serve anche la controprova che i nodi siano
identici, altrimenti non si sta confrontando la stessa ricerca.

## Perche' questo file esiste

Il 2026-09-10 ho riportato **+3,2% (9 su 10)** per l'ottimizzazione di `score<QUIETS>`
(commit f8e4a1b). Rimisurata a macchina ferma contro il commit precedente (06ecaf4), su 22 coppie
in due lotti indipendenti:

    lotto da 10:  7 a favore, mediana +1,0%, media +1,9%
    lotto da 12:  7 a favore, mediana +1,0%, media +1,0%
    -----------------------------------------------------
    totale:      14 su 22,   mediana +1,0%

**Il numero vero e' circa +1%, non +3,2%.** Il guadagno c'e' — 14 coppie su 22 e le mediane dei due
lotti coincidono — ma vale un terzo di quanto avevo annunciato.

## Le due cause, entrambe da tenere a mente

1. **Il carico esterno.** Durante le misure della serata era aperto un browser con un video. Le
   coppie appaiate reggono bene un carico COSTANTE (colpisce A e B allo stesso modo), ma NON un
   carico che parte e finisce a meta' coppia: quello sposta una sola meta' della coppia. Sintomo
   riconoscibile: tempi assoluti fuori scala (4900-5800 ms invece dei soliti 4100-4300) e coppie
   con scarti assurdi, fino a -30%.

2. **La media contro la mediana.** Anche a macchina ferma, i giri "vecchio" ogni tanto sparano un
   valore alto isolato (4647 e 4623 ms fra i 4214-4290 delle altre coppie). Ogni singolo outlier
   del genere regala tre punti percentuali alla MEDIA senza spostare la mediana. Con dieci coppie
   bastano due outlier per trasformare un +1% in un +3%.

**Regola operativa**: riportare mediana e conteggio delle direzioni, e usare la media solo come
terzo numero. E verificare che i tempi assoluti siano nella banda solita PRIMA di credere al
risultato — se il bench parte da 4900 invece che da 4200, la macchina non e' libera e la misura
va buttata.

## Le altre misure della serata

Rimisurata a macchina ferma anche la caccia "indirizzo vs coordinate" (commit 56a570c): **5 su 10,
media -0,0%, mediana +0,0%**, con tempi 4084-4273 ms. Neutra, e stavolta e' una misura buona: il
verdetto che avevo dato su dati sporchi era per fortuna quello giusto.

Non rimisurate perche' precedenti al carico esterno: `ExtMove` (+0,25%, 14 su 20) e i sei prefetch
di `do_move` (neutri, 6 su 16).

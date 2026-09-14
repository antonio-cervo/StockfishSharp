@echo off
rem ============================================================================================
rem  SCONTRO DIRETTO 1 thread contro 8 thread, stesso identico binario.
rem
rem  PERCHE' ESISTE. Il bot gira a Threads=1 dall'8 settembre, e nessuno ha mai misurato quanto
rem  costi in FORZA. La profondita' non serve a dirlo: il Lazy SMP non paga in ply ma in qualita'
rem  della mossa a pari profondita', e la misura del 2026-09-14 ha 0,9 ply di rumore fra due giri
rem  identici (docs/audit-multithread.md). L'unica prova che decide e' far giocare le due
rem  configurazioni una contro l'altra.
rem
rem  PERCHE' 8 E NON 2. Il bot ha "concurrency: 1" in config-stockfishsharp.yml: gioca una partita
rem  alla volta e ha tutta la macchina (Ryzen 7 8745HS, 8 core fisici / 16 logici). Si prova
rem  quello che si dispiegherebbe davvero, cioe' un thread per core fisico.
rem
rem  SCELTE, e i loro limiti dichiarati:
rem   - una partita alla volta (concurrency 1): 1+8 = 9 thread occupati. Con due partite in
rem     parallelo si chiederebbero 18 thread su 16 logici e la parte a 8 thread sarebbe penalizzata
rem     proprio nel confronto che deve vincere o perdere onestamente.
rem   - Hash 64 MB per ENTRAMBI, come il bot. A 8 thread 64 MB sono pochi, ma qui si misura la
rem     configurazione che si dispiegherebbe, non la migliore possibile: il dimensionamento della
rem     TT e' una domanda separata.
rem   - OwnBook=false: le aperture le da' il libro UHO, non il motore, altrimenti non si controlla
rem     la varieta' delle partite (errore gia' fatto in quality.py e trovato il 2026-09-09).
rem   - libro UHO_Lichess_4852_v1.epd, quello che usa il framework di test di Stockfish: posizioni
rem     sbilanciate di proposito, per avere meno patte e distinguere qualcosa con poche partite.
rem   - ogni apertura giocata DUE volte a colori invertiti (-repeat).
rem   - 60+0,6: a 10+5 in una notte si arriva a una ventina di partite e non direbbero nulla.
rem
rem  NON LANCIARLO MENTRE IL BOT GIOCA.
rem ============================================================================================

set FC=D:\Antcer\Documenti\ProgettiVS\fastchess\fastchess-windows-x86-64\fastchess.exe
set LIBRO=D:\Antcer\Documenti\ProgettiVS\fastchess\books\UHO_Lichess_4852_v1.epd
set DLL=D:\Antcer\Documenti\ProgettiVS\StockfishSharp\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll
set SYZYGY=D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy
set ESITI=D:\Antcer\Documenti\ProgettiVS\StockfishSharpDiario\match-thread

if not exist "%ESITI%" mkdir "%ESITI%"

"%FC%" ^
  -engine cmd=dotnet args="%DLL%" name=T1 option.Threads=1 ^
  -engine cmd=dotnet args="%DLL%" name=T8 option.Threads=8 ^
  -each tc=60+0.6 timemargin=200 option.Hash=64 option.OwnBook=false ^
        option.SyzygyPath=%SYZYGY% "option.Move Overhead=100" ^
  -openings file="%LIBRO%" format=epd order=random ^
  -rounds 400 -games 2 -repeat -concurrency 1 ^
  -draw movenumber=40 movecount=8 score=10 ^
  -resign movecount=4 score=700 ^
  -maxmoves 250 ^
  -ratinginterval 10 -report penta=true ^
  -pgnout file="%ESITI%\match.pgn" ^
  -log file="%ESITI%\fastchess.log" level=warn

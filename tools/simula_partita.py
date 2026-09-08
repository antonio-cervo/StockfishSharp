"""Simula una PARTITA VERA con l'orologio che cala, per verificare la gestione del tempo.

PERCHE' SERVE: tutte le prove a orologio fatte finora mandano "ucinewgame" e ripartono da un
orologio pieno — il caso PIU' GENEROSO che esista, e per giunta quello in cui i valori persistenti
della gestione tempo adattiva (bestPreviousScore) sono al sentinella "partita appena iniziata", che
in passato ha gia' causato una mossa da 4 minuti e un abbandono. In partita vera l'orologio cala e
il motore deve accorgersene.

Qui il motore gioca contro se stesso da una posizione data, un solo processo per tutta la partita
(nessun ucinewgame fra una mossa e l'altra), con l'orologio aggiornato mossa per mossa come fa
lichess-bot. Si controlla che il tempo residuo non scenda mai sotto una soglia di sicurezza.

Uso:  python tools/simula_partita.py 300000 3000 20 [--oracolo]
      (orologio iniziale, incremento, numero di semimosse)
"""
import subprocess, sys, time

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
SYZYGY = r'D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

TEMPO = int(sys.argv[1]) if len(sys.argv) > 1 else 300000
INC = int(sys.argv[2]) if len(sys.argv) > 2 else 3000
SEMIMOSSE = int(sys.argv[3]) if len(sys.argv) > 3 else 20
CHI = ORACLE if "--oracolo" in sys.argv else OURS
ETICHETTA = "oracolo" if "--oracolo" in sys.argv else "nostro"

# Fuori dal libro, mediogioco reale: da qui in poi ogni mossa e' una ricerca vera.
PARTENZA = "position startpos moves e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 a7a6 f1e2 e7e5 d4b3 f8e7 e1g1 e8g8"

p = subprocess.Popen(CHI, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
for c in ["setoption name Threads value 1", "setoption name Hash value 64",
          "setoption name OwnBook value false",
          "setoption name SyzygyPath value " + SYZYGY, "isready"]:
    p.stdin.write(c + "\n")
p.stdin.flush()
while p.stdout.readline().strip() != "readyok":
    pass

mosse = PARTENZA.split(" moves ")[1].split()
wtime = btime = TEMPO
peggiore = 0.0
print("%s — orologio %d ms + %d ms/mossa, %d semimosse\n" % (ETICHETTA, TEMPO, INC, SEMIMOSSE))

for i in range(SEMIMOSSE):
    bianco = (len(mosse) % 2) == 0
    p.stdin.write("position startpos moves " + " ".join(mosse) + "\n")
    p.stdin.write("go wtime %d btime %d winc %d binc %d\n" % (wtime, btime, INC, INC))
    p.stdin.flush()
    t0 = time.time()
    prof, mossa = 0, None
    while True:
        l = p.stdout.readline()
        if not l:
            break
        if l.startswith("info depth "):
            try:
                prof = max(prof, int(l.split()[2]))
            except (IndexError, ValueError):
                pass
        if l.startswith("bestmove"):
            mossa = l.split()[1] if len(l.split()) > 1 else None
            break
    ms = (time.time() - t0) * 1000
    if mossa in (None, "0000", "(none)"):
        print("  partita finita alla semimossa %d" % (i + 1))
        break

    residuo = wtime if bianco else btime
    frazione = 100.0 * ms / residuo
    peggiore = max(peggiore, frazione)
    if bianco:
        wtime = int(wtime - ms + INC)
    else:
        btime = int(btime - ms + INC)
    mosse.append(mossa)
    print("  %2d. %-6s %-5s d%-4d %6.0f ms  (%4.1f%% del residuo)   orologio B %6.1fs  N %6.1fs"
          % (i + 1, "bianco" if bianco else "nero", mossa, prof, ms, frazione,
             wtime / 1000.0, btime / 1000.0), flush=True)
    if wtime <= 0 or btime <= 0:
        print("  *** TEMPO SCADUTO ***")
        break

p.stdin.write("quit\n")
p.stdin.flush()
p.kill()
print("\n  peggior singola mossa: %.1f%% dell'orologio residuo" % peggiore)
print("  orologio finale: bianco %.1fs, nero %.1fs" % (wtime / 1000.0, btime / 1000.0))

"""Finali con OROLOGIO REALE: confronta mossa, tempo speso e profondita' raggiunta con l'oracolo.

PERCHE' SERVE: tutte le altre misure di fedelta' sono a PROFONDITA' FISSA, dove il nostro
"maxDepth" del layer UCI non entra mai in gioco. Con un orologio vero invece entra: la fonte fa
girare l'iterative deepening fino a MAX_PLY (search.cpp:332-334, il limite di profondita' esiste
solo se la GUI ha mandato "go depth N"), noi ci fermavamo a 30. Nei finali — dove la profondita'
utile e' molto piu' alta che nel mediogioco — quello e' tempo di orologio lasciato inutilizzato.

Misura le tre cose che contano insieme: la MOSSA (uguale o no), il TEMPO speso (non deve
avvicinarsi al tetto) e la PROFONDITA' raggiunta (quanto del divario resta).

Uso:  python tools/finali_orologio.py 300000 3000
"""
import subprocess, sys, time

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

SYZYGY = r'D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy'
OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

TEMPO = int(sys.argv[1]) if len(sys.argv) > 1 else 300000
INC = int(sys.argv[2]) if len(sys.argv) > 2 else 3000

# Finali veri, sopra i 5 pezzi (fuori dalla portata delle tablebase locali, quindi la ricerca deve
# lavorare davvero) piu' qualcuno dentro. Sono posizioni dove la profondita' paga: strutture di
# pedoni bloccate, finali di torri, opposizione, pedoni passati lontani.
FENS = [
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11",
    "8/8/1P6/5pr1/8/4R3/7k/2K5 w - - 0 1",
    "8/2p4P/8/kr6/6R1/8/8/1K6 w - - 0 1",
    "6k1/4pp1p/3p2p1/P1pPb3/R7/1r2P1PP/3B1P2/6K1 w - - 0 1",
    "5k2/7R/4P2p/5K2/p1r2P1p/8/8/8 b - - 0 1",
    "8/8/3P3k/8/1p6/8/1P6/1K3n2 b - - 0 1",
    "8/3p4/p1bk3p/Pp6/1Kp1PpPp/2P2P1P/2P5/5B2 b - - 0 1",
    "8/8/8/8/5kp1/P7/8/1K1N4 w - - 0 1",
    "8/R7/2q5/8/6k1/8/1P5p/K6R w - - 0 124",
    "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
]


def cerca(cmd, fen):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 64",
              "setoption name OwnBook value false",
              "setoption name SyzygyPath value " + SYZYGY, "isready"]:
        p.stdin.write(c + "\n")
    p.stdin.flush()
    while p.stdout.readline().strip() != "readyok":
        pass

    p.stdin.write("ucinewgame\nposition fen %s\ngo wtime %d btime %d winc %d binc %d\n"
                  % (fen, TEMPO, TEMPO, INC, INC))
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
    p.stdin.write("quit\n")
    p.stdin.flush()
    p.kill()
    return mossa, prof, ms


print("orologio %d ms + %d ms/mossa, 1 thread, libro spento, Syzygy attivo\n" % (TEMPO, INC))
print("  %-46s %-22s %-22s" % ("posizione", "nostro", "oracolo"))
uguali = 0
sommaProfNostra = sommaProfOracolo = sommaMsNostro = sommaMsOracolo = 0
for fen in FENS:
    ma, pa, ta = cerca(OURS, fen)
    mb, pb, tb = cerca(ORACLE, fen)
    if ma == mb:
        uguali += 1
    sommaProfNostra += pa; sommaProfOracolo += pb
    sommaMsNostro += ta; sommaMsOracolo += tb
    print("  %-46s %-6s d%-3d %6.0fms   %-6s d%-3d %6.0fms  %s"
          % (fen[:46], ma, pa, ta, mb, pb, tb, "" if ma == mb else "  <-- diversa"), flush=True)

n = len(FENS)
print("\n  stessa mossa %d/%d = %.0f%%" % (uguali, n, 100.0 * uguali / n))
print("  profondita' media: nostra %.1f, oracolo %.1f" % (sommaProfNostra / n, sommaProfOracolo / n))
print("  tempo medio:       nostro %.0f ms, oracolo %.0f ms" % (sommaMsNostro / n, sommaMsOracolo / n))

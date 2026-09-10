"""Affianca le tracce interne del NOSTRO motore e dell'ORACOLO COMPILATO sulla stessa posizione.

E' lo strumento principale dell'audit da quando l'oracolo si compila (2026-09-08): invece di
formulare ipotesi sulle cause di una divergenza, si stampano le stesse grandezze da entrambe le
parti e si guarda quale voce non combacia.

PREREQUISITI
  - il nostro motore ha la traccia integrata, attivata da SFS_TRACE=1 (Search.Traccia);
  - l'oracolo va compilato e patchato: vedi tools/oracolo-traccia.patch, che aggiunge a
    src/search.cpp le stesse identiche tracce, attivate da SF_TRACE=1.

NOTA sul pilotaggio: stderr va scritto su FILE. Con subprocess.PIPE, leggendo solo stdout, il
buffer di stderr si riempie e il processo figlio si blocca senza mai emettere "bestmove".

Uso:  python tools/confronta_traccia.py "FEN" PROFONDITA [prefisso] [plyMax]
      prefisso: quale traccia confrontare — "PLY" (mosse), "MH sito" (scritture di main history),
                "CHIN"/"CH" (correction history), "PATTA", "Q" (quiescenza), "R mc=", "STEP9"
      plyMax:   fin dove scendono le tracce (default 3). Si alza quando la prima divergenza e' piu'
                in basso: viene passato a ENTRAMBI i motori (SFS_PLY e SF_PLY), che devono avere lo
                stesso tetto o le righe non si affiancano.
"""
import subprocess, os, io, sys, tempfile

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

ORACOLO_DIR = r'D:\Antcer\Documenti\ProgettiVS\oracolo-build\src'
OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ORACOLO_DIR + r'\stockfish.exe']

FEN = sys.argv[1] if len(sys.argv) > 1 else "8/3k4/8/8/8/4B3/4KB2/2B5 w - - 0 1"
DEPTH = int(sys.argv[2]) if len(sys.argv) > 2 else 2
PREFISSO = sys.argv[3] if len(sys.argv) > 3 else "RADICE"
PLY = int(sys.argv[4]) if len(sys.argv) > 4 else 3


def esegui(cmd, cwd, var, extra):
    env = dict(os.environ)
    env[var] = "1"
    # Tetto di ply delle tracce, uguale sui due motori (SFS_PLY / SF_PLY): senza, le due tracce
    # contengono insiemi di righe diversi e l'affiancamento posizionale non ha senso.
    env["SFS_PLY" if var == "SFS_TRACE" else "SF_PLY"] = str(PLY)
    percorso = os.path.join(tempfile.gettempdir(), "traccia_" + var + ".txt")
    with io.open(percorso, 'w', encoding='utf-8', errors='ignore') as ferr:
        p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=ferr,
                             text=True, bufsize=1, cwd=cwd, env=env)
        for c in ["setoption name Threads value 1", "setoption name Hash value 64"] + extra + \
                 ["ucinewgame", "position fen " + FEN, "go depth %d" % DEPTH]:
            p.stdin.write(c + "\n")
        p.stdin.flush()
        finale = ""
        while True:
            l = p.stdout.readline()
            if not l:
                break
            if l.startswith("info depth ") and " score " in l:
                finale = l.strip()
            if l.startswith("bestmove"):
                break
        p.kill()
    righe = io.open(percorso, encoding='utf-8', errors='ignore').read().splitlines()
    return finale, [r.strip() for r in righe if r.strip().startswith(PREFISSO)]


fa, ta = esegui(OURS, ROOT, "SFS_TRACE", ["setoption name OwnBook value false"])
fb, tb = esegui(ORACLE, ORACOLO_DIR, "SF_TRACE", [])

print(FEN + "   profondita' " + str(DEPTH) + "\n")
print("  nostro : " + fa)
print("  oracolo: " + fb + "\n")

n = max(len(ta), len(tb))
print("  %d righe '%s' da noi, %d dall'oracolo\n" % (len(ta), PREFISSO, len(tb)))
diverse = 0
for i in range(n):
    a = ta[i] if i < len(ta) else "(manca)"
    b = tb[i] if i < len(tb) else "(manca)"
    # normalizza le maiuscole delle case (noi stampiamo E2D2, l'oracolo e2d2)
    if a.lower() == b.lower():
        continue
    diverse += 1
    print("  #%d" % (i + 1))
    print("    nostro : " + a)
    print("    oracolo: " + b)
print("\n  righe diverse: %d/%d" % (diverse, n))

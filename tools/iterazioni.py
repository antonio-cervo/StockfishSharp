"""Affianca le righe "info depth" (una per ITERAZIONE) nostre e dell'oracolo sulla stessa posizione.

Serve a stabilire a quale ITERAZIONE nasce una divergenza: se le iterazioni 1..k coincidono in
mossa, punteggio e nodi, la causa e' dentro l'iterazione k+1 e non prima. E' il primo passo da fare
prima di aprire le tracce riga per riga, che sono molto piu' costose da leggere.

Uso:  python tools/iterazioni.py "FEN" PROFONDITA
"""
import subprocess, re, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET da usare. Finche' l'SDK 11 non e' installato a livello di sistema, il
# 'dotnet' del PATH e' il 10 e NON puo' eseguire un binario net11.0: si preferisce quindi
# l'installazione utente, ricadendo su quella di sistema appena c'e'.
import os as _os
_MUX = _os.path.expanduser(r'~\.dotnet11\dotnet.exe')
DOTNET = _MUX if _os.path.exists(_MUX) else 'dotnet'

OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

FEN = sys.argv[1]
DEPTH = int(sys.argv[2]) if len(sys.argv) > 2 else 4


def cerca(cmd, fen, d):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 64",
              "setoption name OwnBook value false", "ucinewgame",
              "position fen " + fen, "go depth %d" % d]:
        p.stdin.write(c + "\n")
    p.stdin.flush()
    righe = []
    while True:
        line = p.stdout.readline()
        if not line:
            break
        if line.startswith("info depth ") and " score " in line:
            righe.append(line.strip())
        if line.startswith("bestmove"):
            break
    p.kill()
    return righe


def compatta(r):
    d = re.search(r'^info depth (\d+)', r)
    sd = re.search(r' seldepth (\d+)', r)
    sc = re.search(r' score (\S+ -?\d+)', r)
    nd = re.search(r' nodes (\d+)', r)
    pv = re.search(r' pv (.*)$', r)
    return "d=%-3s sel=%-3s %-12s nodi=%-8s pv %s" % (
        d.group(1) if d else '?', sd.group(1) if sd else '?',
        sc.group(1) if sc else '?', nd.group(1) if nd else '?',
        (pv.group(1) if pv else '').lower())


a, b = cerca(OURS, FEN, DEPTH), cerca(ORACLE, FEN, DEPTH)
print(FEN + "   profondita' " + str(DEPTH) + "\n")
for i in range(max(len(a), len(b))):
    ca = compatta(a[i]) if i < len(a) else "(manca)"
    cb = compatta(b[i]) if i < len(b) else "(manca)"
    print(("  " if ca == cb else "! ") + "nostro : " + ca)
    print(("  " if ca == cb else "! ") + "oracolo: " + cb)
    print()

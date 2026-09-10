"""Parita' con l'oracolo CON LE TABLEBASE SYZYGY CONFIGURATE — punto cieco dell'audit fino al
2026-09-09.

PERCHE' SERVE: nessuno degli altri strumenti configura SyzygyPath, quindi tutto il percorso
tablebase (rank_root_moves alla radice, il probing in-tree dello Step 7, e soprattutto
`syzygy_extend_pv`, che NON e' portato) non e' mai stato confrontato con la fonte. Non e' un
dettaglio di visualizzazione: `syzygy_extend_pv` (search.cpp:60, ~110 righe) TRONCA o ESTENDE
`rootMoves[0].pv`, che a inizio iterazione successiva diventa `previousPV` e da li' alimenta
`followPV` — cioe' puo' cambiare l'albero dell'iterazione dopo. E il bot gioca CON le tablebase
configurate.

Confronta sia il conteggio nodi sia la PV: la seconda e' il segnale specifico per
`syzygy_extend_pv`.

Uso:  python tools/tablebase.py 10 16
"""
import subprocess, re, sys, os

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

SYZYGY = r'D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy'
OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

# Finali entro i 5 pezzi (il set disponibile in locale e' 3-4-5), scelti con punteggi DECISIVI:
# e' la condizione che fa scattare syzygy_extend_pv (search.cpp:2301-2303, "is_decisive(v) &&
# !is_mate_or_mated(v) && !usePreviousScore").
FENS = [
    "8/8/8/8/8/2K5/1Q6/1k6 w - - 0 1",          # KQvK, matto forzato
    "8/8/8/8/8/2K5/1R6/1k6 w - - 0 1",          # KRvK
    "8/8/8/4k3/8/8/4P3/4K3 w - - 0 1",          # KPvK, vinto
    "8/8/8/8/4k3/8/4P3/4K3 w - - 0 1",          # KPvK, patta
    "8/8/8/3k4/8/8/3KB3/3B4 w - - 0 1",         # KBBvK
    "8/8/8/8/1k6/8/1K6/1NN5 w - - 0 1",         # KNNvK, patta
    "8/8/8/8/3k4/8/3K4/3R4 b - - 0 1",          # KRvK dal lato debole
    "8/8/8/8/8/1k6/1p6/1K6 w - - 0 1",          # KvKP
    "6k1/8/6K1/8/8/8/6P1/8 w - - 0 1",          # KPvK, opposizione
    "8/8/1k6/8/8/1K6/1P6/8 w - - 0 1",          # KPvK, colonna b
    "8/8/8/8/8/k1K5/8/1R6 w - - 0 1",           # KRvK, matto vicino
    "5k2/8/5K2/8/8/8/5R2/8 w - - 0 1",          # KRvK, opposizione
]

DEPTHS = [int(a) for a in sys.argv[1:] if a.lstrip('-').isdigit()] or [10, 16]


def cerca(cmd, fen, d):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 64",
              "setoption name OwnBook value false",
              "setoption name SyzygyPath value " + SYZYGY,   # <-- il punto di tutto questo file
              "ucinewgame", "position fen " + fen, "go depth %d" % d]:
        p.stdin.write(c + "\n")
    p.stdin.flush()
    ultima = None
    while True:
        line = p.stdout.readline()
        if not line:
            break
        if line.startswith("info depth ") and " nodes " in line:
            ultima = line
        if line.startswith("bestmove"):
            break
    p.kill()
    if not ultima:
        return -1, "", ""
    nodi = re.search(r' nodes (\d+)', ultima)
    pv = re.search(r' pv (.*)$', ultima)
    sc = re.search(r' score (\S+ -?\d+)', ultima)
    return (int(nodi.group(1)) if nodi else -1,
            (pv.group(1).strip() if pv else "").lower(),
            sc.group(1) if sc else "")


if not os.path.isdir(SYZYGY):
    sys.exit("Tablebase non trovate in " + SYZYGY)

print("%d finali entro i 5 pezzi, SyzygyPath configurato su ENTRAMBI i motori\n" % len(FENS))
for d in DEPTHS:
    ugNodi = ugPv = 0
    diversi = []
    for fen in FENS:
        na, pa, sa = cerca(OURS, fen, d)
        nb, pb, sb = cerca(ORACLE, fen, d)
        if na == nb:
            ugNodi += 1
        if pa == pb:
            ugPv += 1
        if na != nb or pa != pb or sa != sb:
            diversi.append((fen, na, nb, sa, sb, pa, pb))
    print("  profondita' %d:  nodi %d/%d   PV %d/%d"
          % (d, ugNodi, len(FENS), ugPv, len(FENS)), flush=True)
    for fen, na, nb, sa, sb, pa, pb in diversi:
        print("      %s" % fen, flush=True)
        print("        nostro  nodi %-10s %-12s pv %s" % (f"{na:,}", sa, pa), flush=True)
        print("        oracolo nodi %-10s %-12s pv %s" % (f"{nb:,}", sb, pb), flush=True)

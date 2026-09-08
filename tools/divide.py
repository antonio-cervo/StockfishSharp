"""Divide sulla ricerca: per ogni mossa legale della radice, cerca il FIGLIO a profondita' d-1 con
entrambi i motori e confronta. Se tutti i figli combaciano ma la radice no, la differenza sta nel
nodo radice (finestra di aspirazione, ri-ricerca, combinazione), non piu' in basso.
"""
import subprocess, re, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

FEN = sys.argv[1] if len(sys.argv) > 1 else "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11"
DEPTH = int(sys.argv[2]) if len(sys.argv) > 2 else 5


def apri(cmd):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 16",
              "setoption name OwnBook value false"]:
        p.stdin.write(c + "\n")
    p.stdin.flush()
    return p


def cerca(p, fen, moves, depth):
    p.stdin.write("ucinewgame\n")
    p.stdin.write(f"position fen {fen}" + (" moves " + moves if moves else "") + "\n")
    p.stdin.write(f"go depth {depth}\n")
    p.stdin.flush()
    ultima = None
    while True:
        line = p.stdout.readline()
        if not line:
            break
        if line.startswith("info depth ") and " score " in line and " pv " in line:
            ultima = line
        if line.startswith("bestmove"):
            break
    if not ultima:
        return ("?", -1)
    sc = re.search(r'score (cp -?\d+|mate -?\d+)', ultima).group(1)
    nd = re.search(r' nodes (\d+)', ultima)
    return (sc, int(nd.group(1)) if nd else -1)


# mosse legali della radice: chiediamole al nostro perft, che e' verificato esattamente
q = subprocess.run(OURS, input=f"position fen {FEN}\ngo perft 1\nquit\n",
                   capture_output=True, text=True, cwd=ROOT)
mosse = [l.split(":")[0] for l in q.stdout.splitlines() if re.match(r'^[a-h][1-8][a-h][1-8][qrbn]?:', l)]
print(f"{FEN}\n{len(mosse)} mosse di radice, figli a profondita' {DEPTH}\n")

a, b = apri(OURS), apri(ORACLE)
diverse = 0
for m in mosse:
    (sa, na) = cerca(a, FEN, m, DEPTH)
    (sb, nb) = cerca(b, FEN, m, DEPTH)
    flag = ""
    if sa != sb:
        flag += "  PUNTEGGIO DIVERSO"
        diverse += 1
    print(f"  {m:6s} nostro {sa:>9s} {na:>8,}n | oracolo {sb:>9s} {nb:>8,}n{flag}")
print(f"\nfigli con punteggio diverso: {diverse}/{len(mosse)}")
a.kill(); b.kill()

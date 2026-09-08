"""Per ogni posizione, confronta punteggio e mossa a OGNI profondita' 1..N contro l'oracolo, per
vedere dove esattamente comincia la divergenza (e se e' sul punteggio, sulla mossa, o su entrambi).
"""
import subprocess, re, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

MAXD = int(sys.argv[1]) if len(sys.argv) > 1 else 12

FENS = [
    ("A tattica", "r1bbk1nr/pp3p1p/2n5/1N4p1/2Np1B2/8/PPP2PPP/2KR1B1R w kq - 0 13"),
    # posizione iniziale tolta: la gioca il LIBRO, quindi non produce nessuna riga info
    ("C Kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 10"),
    ("D finale pedoni", "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 11"),
]


def per_depth(cmd, fen, maxd):
    """Ritorna {depth: (score, bestmove-della-pv)} prendendo l'ULTIMA riga info per ogni depth."""
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 16",
              "ucinewgame", "position fen " + fen]:
        p.stdin.write(c + "\n")
    p.stdin.flush()
    out = {}
    # Una ricerca SEPARATA per profondita': il nostro motore stampa una sola riga "info" a fine
    # ricerca, non una per iterazione come la fonte, quindi un solo "go depth N" darebbe un punto
    # solo. E readline() esplicito, non "for line in p.stdout": l'iteratore fa read-ahead e non si
    # puo' riusare fra cicli di scrittura sullo stesso processo.
    for d in range(1, maxd + 1):
        p.stdin.write("ucinewgame\n")
        p.stdin.write("position fen " + fen + "\n")
        p.stdin.write(f"go depth {d}\n")
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
        if ultima:
            sc = re.search(r'score (cp -?\d+|mate -?\d+)', ultima).group(1)
            mv = re.search(r' pv (\S+)', ultima).group(1)
            nd = re.search(r' nodes (\d+)', ultima)
            out[d] = (sc, mv, int(nd.group(1)) if nd else -1)
    p.kill()
    return out


for label, fen in FENS:
    a = per_depth(OURS, fen, MAXD)
    b = per_depth(ORACLE, fen, MAXD)
    print(f"\n{label}  {fen}")
    primo_sc = primo_mv = None
    for d in range(1, MAXD + 1):
        if d not in a or d not in b:
            continue
        (sa, ma, na), (sb, mb, nb) = a[d], b[d]
        oks, okm = sa == sb, ma == mb
        if not oks and primo_sc is None:
            primo_sc = d
        if not okm and primo_mv is None:
            primo_mv = d
        flag = ("" if oks else " PUNTEGGIO") + ("" if okm else " MOSSA")
        print(f"   d{d:<2} nostro {sa:>9s} {ma:6s} {na:>9,} nodi | oracolo {sb:>9s} {mb:6s} {nb:>9,} nodi"
              f"  {'NODI UGUALI' if na == nb else ''}{flag}")
    print(f"   -> prima divergenza di punteggio: {primo_sc}   di mossa: {primo_mv}")

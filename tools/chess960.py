"""Parita' di conteggio nodi su CHESS960, l'unico percorso mai confrontato con l'oracolo.

PERCHE' SERVE: tutti gli altri strumenti di audit ESCLUDONO deliberatamente le posizioni Chess960
(vedi la nota in accordo_profondita.py: senza "setoption name UCI_Chess960 value true" i diritti di
arrocco della FEN si interpretano diversamente e si confronterebbero due posizioni DIVERSE). La
conseguenza pero' e' che l'arrocco Chess960 — dove la torre puo' stare ovunque e re e torre possono
finire l'uno sulla casa di partenza dell'altro — non e' mai stato messo alla prova contro la fonte,
pur essendo il ramo piu' contorto di do_castling/CastlingImpeded/generate.

Qui l'opzione si ACCENDE su entrambi i motori prima di ogni posizione, quindi le due FEN
significano davvero la stessa cosa.

Uso:  python tools/chess960.py 8 12
"""
import subprocess, re, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

# Posizioni Chess960 con arrocco ancora disponibile da entrambe le parti, scelte fra quelle in cui
# la torre NON e' sulla casa standard (altrimenti si ricadrebbe nel caso classico e non si
# proverebbe nulla di nuovo). Le prime due sono le Defaults Chess960 del bench di Stockfish.
FENS = [
    "nqbnrkrb/pppppppp/8/8/8/8/PPPPPPPP/NQBNRKRB w KQkq - 0 1",
    "bbqnnrkr/pppppppp/8/8/8/8/PPPPPPPP/BBQNNRKR w KQkq - 0 1",
    "rknbbnqr/pppppppp/8/8/8/8/PPPPPPPP/RKNBBNQR w KQkq - 0 1",
    "qrkbbnrn/pppppppp/8/8/8/8/PPPPPPPP/QRKBBNRN w KQkq - 0 1",
    "bqnbrkrn/pppppppp/8/8/8/8/PPPPPPPP/BQNBRKRN w KQkq - 0 1",
    # Mediogiochi Chess960 con l'arrocco ancora in ballo: e' li' che il ramo conta davvero.
    "nqbnrkrb/pp2pppp/2pp4/8/3P4/2N5/PPP1PPPP/1QB1RKRB w KQkq - 0 4",
    "bbqnnrkr/pp1ppppp/2p5/8/4P3/5P2/PPPP2PP/BBQNNRKR b KQkq - 0 3",
    "rknbbnqr/1ppppppp/p7/8/2P5/8/PP1PPPPP/RKNBBNQR w KQkq - 0 3",
]

DEPTHS = [int(a) for a in sys.argv[1:] if a.lstrip('-').isdigit()] or [8, 12]


def cerca(cmd, fen, d):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 64",
              "setoption name OwnBook value false",
              "setoption name UCI_Chess960 value true",  # <-- il punto di tutto questo file
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
        return -1
    m = re.search(r' nodes (\d+)', ultima)
    return int(m.group(1)) if m else -1


print("%d posizioni Chess960 (UCI_Chess960 acceso su ENTRAMBI i motori)\n" % len(FENS))
for d in DEPTHS:
    uguali, diversi = 0, []
    for fen in FENS:
        na, nb = cerca(OURS, fen, d), cerca(ORACLE, fen, d)
        if na == nb:
            uguali += 1
        else:
            diversi.append((fen, na, nb))
    print("  profondita' %d:  stesso numero di nodi %d/%d = %5.1f%%"
          % (d, uguali, len(FENS), 100.0 * uguali / len(FENS)), flush=True)
    for fen, na, nb in diversi:
        print("      nostro %10s | oracolo %10s  (%+d)  %s"
              % (f"{na:,}", f"{nb:,}", na - nb, fen), flush=True)

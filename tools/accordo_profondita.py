"""Quanto GIOCHIAMO come l'oracolo, a PARITA' DI PROFONDITA'.

E' la misura di fedelta' di gioco depurata dalla velocita': se cerchiamo la stessa profondita' e
scegliamo la stessa mossa, la ricerca "pensa" come quella della fonte, indipendentemente dal fatto
che noi ci mettiamo di piu'. Confrontata con l'accordo a parita' di TEMPO (tools/quality.py) dice
quanto del divario in partita e' fedelta' e quanto e' solo velocita'.

Uso:  python tools/accordo_profondita.py 8 11 14
"""
import subprocess, re, io, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

src = io.open(ROOT + r'\StockfishSharp.Uci\Program.cs', encoding='utf-8').read()
i = src.index('Defaults')
fens = [f for f in re.findall(r'"([^"]*?/[^"]*? [wb] [^"]*?)"', src[i:i + 20000]) if f.count('/') == 7]
fens = [f for f in fens if 'K7/8/8/8' not in f]

DEPTHS = [int(a) for a in sys.argv[1:]] or [8, 11, 14]


def apri(cmd):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    p.stdin.write("setoption name Threads value 1\n")
    p.stdin.write("setoption name Hash value 64\n")
    p.stdin.flush()
    return p


def cerca(p, fen, d):
    p.stdin.write("ucinewgame\nposition fen " + fen + "\n")
    p.stdin.write("go depth %d\n" % d)
    p.stdin.flush()
    ultima, best = None, None
    while True:
        line = p.stdout.readline()
        if not line:
            break
        if line.startswith("info depth ") and " score " in line:
            ultima = line
        if line.startswith("bestmove"):
            parti = line.split()
            best = parti[1] if len(parti) > 1 else None
            break
    sc = None
    if ultima:
        m = re.search(r'score cp (-?\d+)', ultima)
        sc = int(m.group(1)) if m else None
    return best, sc


a, b = apri(OURS), apri(ORACLE)
print("%d posizioni, 1 thread\n" % len(fens))
for d in DEPTHS:
    uguali, diffs = 0, []
    for fen in fens:
        ma, sa = cerca(a, fen, d)
        mb, sb = cerca(b, fen, d)
        if ma == mb:
            uguali += 1
        if sa is not None and sb is not None:
            diffs.append(abs(sa - sb))
    diffs.sort()
    mediana = diffs[len(diffs) // 2] if diffs else -1
    print("  profondita' %2d:  stessa mossa %2d/%d = %5.1f%%   "
          "scarto di punteggio: mediana %d cp, peggiore %d cp"
          % (d, uguali, len(fens), 100.0 * uguali / len(fens), mediana, diffs[-1] if diffs else -1),
          flush=True)
a.kill(); b.kill()

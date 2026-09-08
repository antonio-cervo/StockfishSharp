"""Qualita' della mossa contro una verita' di riferimento CONGELATA (truth.json, prodotta da
truth_build.py con l'oracolo a 1 thread e profondita' fissa: deterministica, quindi confrontabile
fra esecuzioni diverse e fra versioni diverse del motore).

Uso:  python quality.py [movetime_ms] [threads] [etichetta]
"""
import subprocess, re, json, io, os, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']
TRUTH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'truth.json')

MOVETIME = int(sys.argv[1]) if len(sys.argv) > 1 else 2000
THREADS = int(sys.argv[2]) if len(sys.argv) > 2 else 1
ETICHETTA = sys.argv[3] if len(sys.argv) > 3 else "nostro"
CHI = ORACLE if ETICHETTA.lower().startswith("oracolo") else OURS

d = json.load(io.open(TRUTH, encoding='utf-8'))
truth = d["truth"]


def chiedi(cmd, th, fen, ms):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    p.stdin.write("setoption name Threads value %d\n" % th)
    p.stdin.write("setoption name Hash value 256\n")
    # LIBRO SPENTO. Trappola gia' documentata in accordo_profondita.py ma sfuggita in QUESTO
    # strumento fino al 2026-09-08: con il libro attivo il motore risponde SENZA cercare sulle
    # posizioni coperte, e su quelle si misura il libro invece della ricerca. L'oracolo ignora
    # l'opzione, non ce l'ha.
    p.stdin.write("setoption name OwnBook value false\n")
    p.stdin.write("ucinewgame\nposition fen " + fen + "\n")
    p.stdin.write("go movetime %d\n" % ms)
    p.stdin.flush()
    mv, prof = None, 0
    while True:
        line = p.stdout.readline()
        if not line:
            break
        m = re.search(r'^info depth (\d+)', line)
        if m:
            prof = int(m.group(1))
        if line.startswith("bestmove"):
            parti = line.split()
            mv = parti[1] if len(parti) > 1 else None
            break
    p.kill()
    return mv, prof


accordo, profondita, sbagliate = 0, [], []
for fen, atteso in truth.items():
    mv, prof = chiedi(CHI, THREADS, fen, MOVETIME)
    if mv == atteso:
        accordo += 1
    else:
        sbagliate.append((fen, mv, atteso))
    profondita.append(prof)

n = len(truth)
print("%s  threads=%d  movetime=%dms" % (ETICHETTA, THREADS, MOVETIME))
print("  accordo %d/%d = %.1f%%   profondita' media %.2f"
      % (accordo, n, 100.0 * accordo / n, sum(profondita) / n))
print("  (verita': oracolo 1 thread profondita' %d, congelata)" % d["depth"])
for fen, mv, atteso in sbagliate:
    print("    %-70s nostro %-6s atteso %s" % (fen[:70], mv, atteso))

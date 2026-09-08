"""Costruisce UNA VOLTA la verita' di riferimento per la metrica di qualita' della mossa, e la
congela su file.

PERCHE': finora la verita' veniva rigenerata a ogni esecuzione con l'oracolo a 8 thread. Il Lazy
SMP non e' deterministico, quindi il riferimento cambiava da un'esecuzione all'altra e i numeri di
esecuzioni diverse NON erano confrontabili. Misurato: il controllo "oracolo a 1 thread" e' passato
da 86,1% a 88,9% fra due esecuzioni senza che nulla dell'oracolo fosse cambiato.

Qui la verita' e' l'oracolo a 1 THREAD e profondita' fissa: una ricerca limitata in profondita' e'
deterministica a thread singolo, quindi il file prodotto e' riproducibile e riusabile per sempre.
"""
import subprocess, re, json, io, os, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'truth.json')
DEPTH = int(sys.argv[1]) if len(sys.argv) > 1 else 20

src = io.open(ROOT + r'\StockfishSharp.Uci\Program.cs', encoding='utf-8').read()
i = src.index('Defaults')
fens = [f for f in re.findall(r'"([^"]*?/[^"]*? [wb] [^"]*?)"', src[i:i + 20000]) if f.count('/') == 7]
fens = [f for f in fens if 'K7/8/8/8' not in f]

p = subprocess.Popen(ORACLE, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
p.stdin.write("setoption name Threads value 1\nsetoption name Hash value 256\n")
p.stdin.flush()

truth = {}
for k, fen in enumerate(fens, 1):
    p.stdin.write("ucinewgame\nposition fen " + fen + "\n")
    p.stdin.write("go depth %d\n" % DEPTH)
    p.stdin.flush()
    mv = None
    while True:
        line = p.stdout.readline()
        if not line:
            break
        if line.startswith("bestmove"):
            parti = line.split()
            mv = parti[1] if len(parti) > 1 else None
            break
    truth[fen] = mv
    print("  %2d/%d  %s" % (k, len(fens), mv), flush=True)
p.kill()

json.dump({"depth": DEPTH, "threads": 1, "truth": truth}, io.open(OUT, 'w', encoding='utf-8'), indent=1)
print("\nscritto %s (%d posizioni, oracolo 1 thread profondita' %d)" % (OUT, len(truth), DEPTH))

"""Quanto GIOCHIAMO come l'oracolo, a PARITA' DI PROFONDITA'.

E' la misura di fedelta' di gioco depurata dalla velocita': se cerchiamo la stessa profondita' e
scegliamo la stessa mossa, la ricerca "pensa" come quella della fonte, indipendentemente dal fatto
che noi ci mettiamo di piu'. Confrontata con l'accordo a parita' di TEMPO (tools/quality.py) dice
quanto del divario in partita e' fedelta' e quanto e' solo velocita'.

DUE TRAPPOLE, entrambe cadute davvero il 2026-09-08:

1. **Il LIBRO.** Con il libro attivo il motore risponde senza cercare: sulle posizioni coperte si
   misura il libro, non la ricerca. La posizione iniziale risultava un "disaccordo" a ogni
   profondita' solo per questo. Qui si spegne con "setoption name OwnBook value false".

2. **La DESINCRONIZZAZIONE del dialogo.** Riusando un solo processo per tutte le posizioni, il
   lettore puo' attribuire a una posizione la risposta di un'altra: lo stesso confronto dava 50/53,
   51/53 e 52/53 in tre esecuzioni, mentre entrambi i motori sono DETERMINISTICI a profondita'
   fissa (verificato con 3 ripetizioni per posizione). Qui si usa un PROCESSO NUOVO per ogni
   posizione: piu' lento ma senza stato condiviso da sbagliare.

Uso:  python tools/accordo_profondita.py 1 3 6 9 12
"""
import subprocess, re, io, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
OURS = ['dotnet', ROOT + r'\StockfishSharp.Uci\bin\Release\net10.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

src = io.open(ROOT + r'\StockfishSharp.Uci\Program.cs', encoding='utf-8').read()
i = src.index('Defaults')
# La lista Defaults contiene DUE posizioni Chess960, racchiuse fra righe
# "setoption name UCI_Chess960 value true/false". Vanno escluse: senza quell'opzione i diritti di
# arrocco della FEN si interpretano diversamente e si confronterebbero due posizioni diverse.
fens, chess960 = [], False
for riga in re.findall(r'"([^"]*)"', src[i:i + 20000]):
    if riga.startswith("setoption name UCI_Chess960"):
        chess960 = riga.rstrip().endswith("true")
        continue
    if riga.count('/') == 7 and re.search(r' [wb] ', riga) and not chess960:
        fens.append(riga)

DEPTHS = [int(a) for a in sys.argv[1:] if a.lstrip('-').isdigit()] or [1, 3, 6, 9, 12]


def cerca(cmd, fen, d):
    """Processo nuovo, una sola ricerca, poi si chiude. Nessuno stato da desincronizzare."""
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    p.stdin.write("setoption name Threads value 1\n")
    p.stdin.write("setoption name Hash value 64\n")
    p.stdin.write("setoption name OwnBook value false\n")  # l'oracolo la ignora, non ce l'ha
    p.stdin.write("ucinewgame\n")
    p.stdin.write("position fen " + fen + "\n")
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
    p.kill()

    sc = None
    if ultima:
        m = re.search(r'score cp (-?\d+)', ultima)
        sc = int(m.group(1)) if m else None
    # "(none)" dell'oracolo e "0000" nostro sono la stessa cosa (matto/stallo): differenza di
    # formato del layer UCI, non di gioco.
    if best in ("(none)", "0000"):
        best = "0000"
    return best, sc


print("%d posizioni, 1 thread, libro spento\n" % len(fens))
for d in DEPTHS:
    uguali, diffs, disaccordi = 0, [], []
    for fen in fens:
        ma, sa = cerca(OURS, fen, d)
        mb, sb = cerca(ORACLE, fen, d)
        if ma == mb:
            uguali += 1
        else:
            disaccordi.append((fen, ma, sa, mb, sb))
        if sa is not None and sb is not None:
            diffs.append(abs(sa - sb))
    diffs.sort()
    mediana = diffs[len(diffs) // 2] if diffs else -1
    print("  profondita' %2d:  stessa mossa %2d/%d = %5.1f%%   "
          "scarto di punteggio: mediana %d cp, peggiore %d cp"
          % (d, uguali, len(fens), 100.0 * uguali / len(fens), mediana, diffs[-1] if diffs else -1),
          flush=True)
    if "-v" in sys.argv:
        for fen, ma, sa, mb, sb in disaccordi:
            print("      nostro %-6s (%s) | oracolo %-6s (%s)  %s" % (ma, sa, mb, sb, fen), flush=True)

"""Confronta i CONTEGGI NODI con l'oracolo a profondita' bassa, posizione per posizione.

Il conteggio nodi e' il segnale piu' severo che esista: due ricerche possono scegliere la stessa
mossa e lo stesso punteggio per caso, ma se visitano lo stesso NUMERO di nodi hanno quasi
certamente visitato lo stesso albero. Serve a capire se la divergenza e' diffusa o circoscritta, e
a quale profondita' comincia davvero.

Processo nuovo per posizione e libro spento: vedi le trappole documentate in
tools/accordo_profondita.py.

Le posizioni sono per default le 51 Defaults del bench (la lista di Stockfish stesso). Con
"--fen FILE" si usa invece un elenco di FEN da file, una per riga, righe "#" ignorate: serve a
battere strade che le Defaults non battono — vedi tools/fen-varie.txt.

Uso:  python tools/nodi_bassa_profondita.py 1 2 3
      python tools/nodi_bassa_profondita.py --fen tools/fen-varie.txt 8 12
"""
import subprocess, re, io, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']

src = io.open(ROOT + r'\StockfishSharp.Uci\Program.cs', encoding='utf-8').read()
i = src.index('Defaults')
fens, chess960 = [], False
for riga in re.findall(r'"([^"]*)"', src[i:i + 20000]):
    if riga.startswith("setoption name UCI_Chess960"):
        chess960 = riga.rstrip().endswith("true")
        continue
    if riga.count('/') == 7 and re.search(r' [wb] ', riga) and not chess960:
        fens.append(riga)

if "--fen" in sys.argv:
    percorso = sys.argv[sys.argv.index("--fen") + 1]
    fens = [r.strip() for r in io.open(percorso, encoding='utf-8')
            if r.strip() and not r.lstrip().startswith("#")]
    print("%d posizioni da %s\n" % (len(fens), percorso))

DEPTHS = [int(a) for a in sys.argv[1:] if a.lstrip('-').isdigit()] or [1, 2, 3]


def cerca(cmd, fen, d):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, bufsize=1, cwd=ROOT)
    for c in ["setoption name Threads value 1", "setoption name Hash value 64",
              "setoption name OwnBook value false", "ucinewgame",
              "position fen " + fen, "go depth %d" % d]:
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


for d in DEPTHS:
    uguali, diversi, esclusi = 0, [], 0
    for fen in fens:
        na, nb = cerca(OURS, fen, d), cerca(ORACLE, fen, d)
        # Matto/stallo alla radice: l'oracolo emette "bestmove (none)" senza alcuna riga "info
        # depth ... nodes", quindi non c'e' NULLA da confrontare. Contarle come divergenze faceva
        # apparire un 96,1% dove la parita' era piena: sono escluse dal denominatore.
        if nb < 0:
            esclusi += 1
            continue
        if na == nb:
            uguali += 1
        else:
            diversi.append((fen, na, nb))
    tot = len(fens) - esclusi
    print("  profondita' %d:  stesso numero di nodi %2d/%d = %5.1f%%%s"
          % (d, uguali, tot, 100.0 * uguali / max(1, tot),
             "   (%d posizioni escluse: matto/stallo alla radice)" % esclusi if esclusi else ""),
          flush=True)
    for fen, na, nb in diversi:
        print("      nostro %8s | oracolo %8s  (%+d)  %s" % (f"{na:,}", f"{nb:,}", na - nb, fen), flush=True)

# -*- coding: utf-8 -*-
"""A 8 thread si spende piu' budget di tempo sui finali di tablebase? Misura, non opinione.

DA DOVE NASCE. L'utente ha osservato in partita che a 8 thread il motore "pensa molto" nei finali a
5 pezzi, consumando molto budget, e ha chiesto perche' le tablebase non lo rendano immediato.

La PRIMA meta' della domanda ha gia' una risposta verificata (2026-09-11): non e' immediato per
fedelta' alla fonte. In search.cpp:923, Step 7, la probe delle tablebase e' sotto
`if (!rootNode && ...)`: alla RADICE non si interroga la tabella per decidere. Alla radice la fonte
chiama `Tablebases::rank_root_moves` (thread.cpp:323), che ORDINA e filtra le mosse per DTZ, e poi
cerca normalmente spendendo tutto il budget. Verificato contro l'oracolo a un thread: stessi tempi
(11,0 contro 11,0 s; 13,6 contro 13,8 s) e stessi tbhits.

La SECONDA meta' — "a 8 thread si spende DI PIU'" — non e' coperta da nessuna misura: quella
verifica era a un thread solo. Questo banco la copre.

IPOTESI DA FALSIFICARE. La gestione del tempo allunga il pensiero quando la mossa migliore cambia
fra un'iterazione e l'altra (`bestMoveChanges`), e il principale somma quel contatore su TUTTI i
thread del pool (search.cpp:562-566). Nei finali vinti a tabella molte mosse di radice sono
equivalenti — tutte vincenti, stesso punteggio — quindi la "migliore" puo' ballare fra mosse
ugualmente buone; con otto thread che ballano ognuno per conto suo il segnale di instabilita'
potrebbe gonfiarsi. Precedente reale: il 2026-09-06 un bug sullo stesso contatore faceva impiegare
tempi spropositati nelle posizioni instabili.

IL CONTROLLO CHE RENDE CONCLUSIVA LA MISURA. Tre categorie, non una:
  A) finali IN tablebase (<=5 pezzi)
  B) finali NON in tablebase (7-9 pezzi) -> separa "finale" da "tablebase"
  C) mediogioco                          -> separa "tablebase" da "tutto"
Se a 8 thread si spende di piu' dappertutto, e' una proprieta' della gestione del tempo e le
tablebase non c'entrano. Se si spende di piu' SOLO in A, l'ipotesi regge.

METODO. Orologio VERO (10+5): a `go movetime` la gestione del tempo e' spenta e non ci sarebbe
niente da misurare. Due motori vivi insieme, uno per numero di thread, stessa posizione chiesta a
entrambi uno dopo l'altro e ordine invertito a posizioni alterne
([[stockfishsharp-misure-appaiate]]).

NON LANCIARLO MENTRE IL BOT GIOCA.

Uso:  python tools/tempo_finali_tablebase.py
"""
import queue
import re
import statistics
import subprocess
import threading
import time

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
DOTNET = 'dotnet'
DLL = ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll'
SYZYGY = r'D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy'

BASE_MS, INC_MS = 600_000, 5_000
THREAD = [1, 8]

POSIZIONI = [
    ("A tablebase", "8/5p2/8/4k3/8/8/4PK2/4R3 w - - 0 1"),          # K+T+P vs K+P, 5 pezzi
    ("A tablebase", "8/8/8/3k4/8/8/5QK1/7r w - - 0 1"),             # K+D vs K+T, 4 pezzi
    ("A tablebase", "8/8/8/4k3/8/4P3/4K3/R5r1 w - - 0 1"),          # K+T+P vs K+T, 5 pezzi
    ("B finale no-tb", "8/8/5pk1/8/8/5PK1/8/R5r1 w - - 0 1"),        # finale di torri, 6 pezzi
    ("B finale no-tb", "8/5pk1/6p1/7p/7P/6P1/5PK1/8 w - - 0 1"),     # finale di pedoni, 8 pezzi
    ("C mediogioco", "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15"),
    ("C mediogioco", "4rrk1/pp1n3p/3q2pQ/2p1pb2/2PP4/2P3N1/P2B2PP/4RRK1 b - - 7 19"),
]


class Motore:
    def __init__(self, thread):
        self.p = subprocess.Popen([DOTNET, DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.righe = queue.Queue()
        threading.Thread(target=self._leggi, daemon=True).start()
        for c in ["uci", "setoption name Threads value %d" % thread,
                  "setoption name Hash value 64", "setoption name OwnBook value false",
                  "setoption name SyzygyPath value " + SYZYGY, "ucinewgame", "isready"]:
            self.manda(c)
        time.sleep(1.5)

    def _leggi(self):
        for r in self.p.stdout:
            self.righe.put(r)
        self.righe.put(None)

    def manda(self, c):
        self.p.stdin.write(c + "\n")
        self.p.stdin.flush()

    def cerca(self, fen, limite=240):
        self.manda("position fen " + fen)
        t0 = time.time()
        self.manda("go wtime %d btime %d winc %d binc %d" % (BASE_MS, BASE_MS, INC_MS, INC_MS))
        prof = tb = 0
        while True:
            rimasto = t0 + limite - time.time()
            if rimasto <= 0:
                return prof, time.time() - t0, tb
            try:
                r = self.righe.get(timeout=rimasto)
            except queue.Empty:
                return prof, time.time() - t0, tb
            if r is None:
                return prof, time.time() - t0, tb
            m = re.search(r'^info depth (\d+)', r)
            if m:
                prof = max(prof, int(m.group(1)))
                t = re.search(r' tbhits (\d+)', r)
                if t:
                    tb = max(tb, int(t.group(1)))
            if r.startswith("bestmove"):
                return prof, time.time() - t0, tb

    def chiudi(self):
        try:
            self.manda("quit")
        except Exception:
            pass
        self.p.kill()


def main():
    print(__doc__.split('Uso:')[0])
    print("=" * 84)
    motori = {t: Motore(t) for t in THREAD}
    ris = {t: [] for t in THREAD}

    print("%-16s | %s" % ("categoria", " | ".join("%2dthr: tempo  prof  tbhits" % t for t in THREAD)))
    for i, (cat, fen) in enumerate(POSIZIONI):
        ordine = THREAD if i % 2 == 0 else list(reversed(THREAD))
        riga = {}
        for t in ordine:
            prof, sec, tb = motori[t].cerca(fen)
            ris[t].append((cat, sec, prof, tb))
            riga[t] = (sec, prof, tb)
        print("%-16s | %s" % (cat, " | ".join("%8.1fs %4d %7d" % riga[t] for t in THREAD)))

    for m in motori.values():
        m.chiudi()

    print("-" * 84)
    print("TEMPO MEDIO SPESO PER CATEGORIA (e' la domanda: a 8 thread si spende di piu'?)")
    for cat in ["A tablebase", "B finale no-tb", "C mediogioco"]:
        riga = "%-16s" % cat
        medie = {}
        for t in THREAD:
            sec = [s for c, s, _, _ in ris[t] if c == cat]
            medie[t] = statistics.mean(sec)
            riga += " | %2dthr %6.1fs" % (t, medie[t])
        if len(THREAD) == 2:
            a, b = THREAD
            riga += "  ->  %+.0f%%" % (100 * (medie[b] / medie[a] - 1))
        print(riga)


main()

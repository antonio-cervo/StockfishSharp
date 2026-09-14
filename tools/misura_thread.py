# -*- coding: utf-8 -*-
"""Quanto rende un thread in piu': PROFONDITA' raggiunta a orologio vero, 1/2/4/8 thread.

PERCHE' COSI'. La metrica sbagliata e' "tempo per arrivare a una profondita' fissa": misurata il
2026-09-07, peggiora anche sull'ORACOLO (0,44x e 0,70x su 8 thread) perche' il Lazy SMP non serve
a quello. La metrica giusta e' l'opposto: orologio vero, e si guarda dove si arriva. Il bench a
profondita' fissa qui non c'entra nulla — serve solo come invariante di non-regressione a 1 thread.

MISURE APPAIATE. Il portatile oscilla del 12% fra un giro e l'altro, quindi non si fanno medie di
giri indipendenti: i motori (uno per numero di thread) restano tutti vivi insieme e la STESSA
posizione viene chiesta a tutti uno dopo l'altro. L'ordine si inverte a posizioni alterne, cosi'
nessun numero di thread eredita sempre la macchina fredda. Si conta anche la DIREZIONE, non solo
la media (vedi [[stockfishsharp-misure-appaiate]] nella memoria di progetto).

L'orologio e' 10+5, il controllo con cui ci sfidano di solito. Serve un orologio VERO e non
"go movetime": increaseDepth (threads.increaseDepth, search.cpp:613) viene scritto solo quando la
gestione tempo e' attiva, e a movetime resterebbe sempre true, cioe' proprio il divario che si
vuole misurare.

NON LANCIARLO MENTRE IL BOT GIOCA: ruba CPU al motore in partita e falsa tutto.

CONFRONTO FRA DUE BINARI. Con --confronto PATH_DLL si aggiungono le stesse colonne calcolate su un
secondo motore (tipicamente una worktree su un commit precedente): le colonne restano appaiate fra
loro posizione per posizione, che e' l'unico modo di confrontare due build su questa macchina.

Uso:  python tools/misura_thread.py [--motore PATH_DLL] [--thread 1,2,4,8] [--confronto PATH_DLL]
"""
import argparse
import queue
import re
import statistics
import subprocess
import threading
import time

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
DOTNET = 'dotnet'
DLL_DEFAULT = ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll'
SYZYGY = r'D:\Antcer\Documenti\ProgettiVS\ACMyChess\Syzygy'

BASE_MS, INC_MS = 600_000, 5_000

# Mediogioco, dalla lista Defaults del bench (benchmark.cpp:34-101): posizioni normali, non casi
# limite — qui si misura la forza tipica, non la fedelta' sulle strade strette.
POSIZIONI = [
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 10",
    "4rrk1/pp1n3p/3q2pQ/2p1pb2/2PP4/2P3N1/P2B2PP/4RRK1 b - - 7 19",
    "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
    "r1bq1rk1/ppp1nppp/4n3/3p3Q/3P4/1BP1B3/PP1N2PP/R4RK1 w - - 1 16",
    "4r1k1/r1q2ppp/ppp2n2/4P3/5Rb1/1N1BQ3/PPP3PP/R5K1 w - - 1 17",
    "r1bq1r1k/b1p1npp1/p2p3p/1p6/3PP3/1B2NN2/PP3PPP/R2Q1RK1 w - - 1 16",
    "3r1rk1/p5pp/bpp1pp2/8/q1PP1P2/b3P3/P2NQRPP/1R2B1K1 b - - 6 22",
    "3q2k1/pb3p1p/4pbp1/2r5/PpN2N2/1P2P2P/5PP1/Q2R2K1 b - - 4 26",
]


class Motore:
    """UN SOLO lettore di stdout con una coda: due thread sulla stessa pipe si rubano le righe."""

    def __init__(self, dll, thread):
        self.p = subprocess.Popen([DOTNET, dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.righe = queue.Queue()
        threading.Thread(target=self._leggi, daemon=True).start()
        for c in ["uci", "setoption name Threads value %d" % thread,
                  "setoption name Hash value 64", "setoption name OwnBook value false",
                  "setoption name SyzygyPath value " + SYZYGY, "ucinewgame", "isready"]:
            self.manda(c)
        time.sleep(1.5)  # il riscaldamento JIT parte da solo in sottofondo

    def _leggi(self):
        for r in self.p.stdout:
            self.righe.put(r)
        self.righe.put(None)

    def manda(self, c):
        self.p.stdin.write(c + "\n")
        self.p.stdin.flush()

    def cerca(self, fen, limite=180):
        """(profondita', secondi, nodi) dell'ultima riga info prima del bestmove."""
        self.manda("position fen " + fen)
        t0 = time.time()
        self.manda("go wtime %d btime %d winc %d binc %d" % (BASE_MS, BASE_MS, INC_MS, INC_MS))
        prof, nodi = 0, 0
        while True:
            rimasto = t0 + limite - time.time()
            if rimasto <= 0:
                return prof, time.time() - t0, nodi
            try:
                r = self.righe.get(timeout=rimasto)
            except queue.Empty:
                return prof, time.time() - t0, nodi
            if r is None:
                return prof, time.time() - t0, nodi
            m = re.search(r'^info depth (\d+)', r)
            if m:
                prof = max(prof, int(m.group(1)))
                n = re.search(r' nodes (\d+)', r)
                if n:
                    nodi = int(n.group(1))
            if r.startswith("bestmove"):
                return prof, time.time() - t0, nodi

    def chiudi(self):
        try:
            self.manda("quit")
        except Exception:
            pass
        self.p.kill()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--motore', default=DLL_DEFAULT)
    ap.add_argument('--thread', default='1,2,4,8')
    ap.add_argument('--confronto', default=None, help='secondo binario, stesse colonne')
    a = ap.parse_args()
    threads = [int(x) for x in a.thread.split(',')]

    # (etichetta, dll, thread) — le colonne sono tutte appaiate fra loro: stessa posizione, una
    # dopo l'altra, ordine invertito a posizioni alterne.
    colonne = [("%dthr" % t, a.motore, t) for t in threads]
    if a.confronto:
        colonne += [("%dthr-B" % t, a.confronto, t) for t in threads]

    print(__doc__.split('Uso:')[0])
    print("motore   A: %s" % a.motore)
    if a.confronto:
        print("motore   B: %s" % a.confronto)
    print("=" * 78)

    motori = {e: Motore(dll, t) for e, dll, t in colonne}
    ris = {e: [] for e, _, _ in colonne}
    etich = [e for e, _, _ in colonne]

    print("pos | " + " | ".join("%8s" % e for e in etich))
    for i, fen in enumerate(POSIZIONI):
        ordine = etich if i % 2 == 0 else list(reversed(etich))
        riga = {}
        for e in ordine:
            prof, sec, nodi = motori[e].cerca(fen)
            ris[e].append((prof, sec, nodi))
            riga[e] = prof
        print(" %2d | " % (i + 1) + " | ".join("%8d" % riga[e] for e in etich))

    for m in motori.values():
        m.chiudi()

    print("-" * 78)
    base = etich[0]
    for e in etich:
        prof = [r[0] for r in ris[e]]
        sec = [r[1] for r in ris[e]]
        nodi = [r[2] for r in ris[e]]
        vinte = sum(1 for x, y in zip(prof, [r[0] for r in ris[base]]) if x > y)
        perse = sum(1 for x, y in zip(prof, [r[0] for r in ris[base]]) if x < y)
        print("%8s | profondita' media %5.2f | tempo medio %5.2f s | nodi/s %8.0f"
              % (e, statistics.mean(prof), statistics.mean(sec),
                 sum(nodi) / max(1e-9, sum(sec))))
        if e != base:
            print("           piu' profondo di %s in %d posizioni su %d, meno in %d"
                  % (base, vinte, len(prof), perse))


main()

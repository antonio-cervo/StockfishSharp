# -*- coding: utf-8 -*-
"""Quanto orologio lasciamo INUTILIZZATO, e quanto ci costano i tre tetti pratici sul tempo.

PERCHE' ESISTE. Il 2026-09-10, vincendo contro eigenmann-chess (2697) a 15+10, siamo finiti con
11,7 minuti mai spesi mentre l'avversario andava in bandiera. La mossa mediana ci e' costata 8,2 s
contro un incremento di 10 s: a ogni mossa tipica il nostro orologio SALIVA. Con il pondering che
ricicla il 63% del pensiero avversario, il sospetto e' che i tre tetti pratici — messi li' per
compensare il fatto che siamo ~3,85x piu' lenti del C++ — siano ora troppo prudenti.

QUESTO BANCO NON CAMBIA NIENTE. Misura e basta. I tetti sono la parte piu' pericolosa del motore:
tre partite perse per tempo vengono da li', e una misura gia' fatta dice che toglierli costa +33%
di tempo per mossa e RADDOPPIA il rischio sull'orologio per guadagnare 1,5 ply. Serve un valore di
partenza solido prima ancora di pensare a toccarli.

CONTROLLO DI TEMPO. Il riferimento e' **10+5**, che e' quello con cui ci sfidano di solito; 15+10
e' secondario. Non si misura su un controllo che non giochiamo.

NON LANCIARLO MENTRE IL BOT GIOCA: ruba CPU al motore in partita e falsa sia questa misura sia le
partite vere.

Uso:  python tools/banco_tetti_tempo.py [mosse_per_partita]
"""
import queue
import re
import statistics
import subprocess
import sys
import threading
import time

import chess

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
DOTNET = 'dotnet'
MOTORE = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
SYZYGY = r'D:\Antcer\Documenti\ProgettiVS\ACMyChess\Syzygy'

MOSSE = int(sys.argv[1]) if len(sys.argv) > 1 else 30

# (nome, base in ms, incremento in ms) — il primo e' quello che conta
CONTROLLI = [("10+5", 600_000, 5_000),
             ("15+10", 900_000, 10_000)]

# Quanto "pensa" l'avversario simulato prima di rispondere, in secondi. Serve al pondering: e' il
# tempo che gli rubiamo. Tenuto vicino a quello che si vede in partita contro i bot forti.
PENSIERO_AVVERSARIO = 6.0

POSIZIONI = [
    "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
    "r1bq1rk1/pp2bppp/2n1pn2/3p4/3P4/2NBPN2/PP3PPP/R1BQ1RK1 w - - 0 9",
    "r2q1rk1/pb1nbppp/1p2pn2/2pp4/2PP4/1PN1PN2/PB2BPPP/R2Q1RK1 w - - 0 10",
]


class Motore:
    """Stesso schema di invarianti_orologio.py: UN SOLO lettore di stdout, con una coda.
    Due thread che leggono la stessa pipe si rubano le righe e si bloccano — gia' successo."""

    def __init__(self):
        self.p = subprocess.Popen(MOTORE, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.righe = queue.Queue()
        threading.Thread(target=self._leggi, daemon=True).start()
        for c in ["uci", "setoption name Threads value 1", "setoption name Hash value 64",
                  "setoption name OwnBook value false",
                  "setoption name SyzygyPath value " + SYZYGY, "ucinewgame", "isready"]:
            self.manda(c)
        time.sleep(1.0)

    def _leggi(self):
        for r in self.p.stdout:
            self.righe.put(r)
        self.righe.put(None)

    def manda(self, c):
        self.p.stdin.write(c + "\n")
        self.p.stdin.flush()

    def _riga(self, scadenza):
        rimasto = scadenza - time.time()
        if rimasto <= 0:
            return None
        try:
            return self.righe.get(timeout=rimasto)
        except queue.Empty:
            return None

    def cerca(self, comando, limite=300):
        """(bestmove, ponder, profondita', secondi)."""
        t0 = time.time()
        self.manda(comando)
        prof = 0
        while True:
            r = self._riga(t0 + limite)
            if r is None:
                return None, None, prof, time.time() - t0
            m = re.search(r'^info depth (\d+)', r)
            if m:
                prof = max(prof, int(m.group(1)))
            if r.startswith("bestmove"):
                pezzi = r.split()
                return pezzi[1], (pezzi[3] if len(pezzi) > 3 else None), prof, time.time() - t0

    def svuota(self, secondi):
        """Consuma le righe per un po' senza aspettarsi un bestmove — usato durante il pondering."""
        scadenza = time.time() + secondi
        while self._riga(scadenza) is not None:
            pass

    def chiudi(self):
        try:
            self.manda("quit")
        except Exception:
            pass
        self.p.kill()


def partita(nome, base, inc, con_ponder, fen):
    """Una partita simulata contro se stesso, con orologio vero. Ritorna le misure."""
    m = Motore()
    b = chess.Board(fen)
    orologio = {chess.WHITE: base, chess.BLACK: base}
    spesi, profondita, sotto_incremento = [], [], 0
    ponder_mandati = ponder_azzeccati = 0
    previsione = None

    for _ in range(MOSSE):
        if b.is_game_over():
            break
        turno = b.turn

        # --- pondering: se avevamo previsto la mossa appena giocata, l'albero e' gia' pronto ------
        indovinato = False
        if con_ponder and previsione is not None:
            ponder_mandati += 1
            if previsione == (b.move_stack[-1].uci() if b.move_stack else None):
                indovinato = True
                ponder_azzeccati += 1

        go = "go wtime %d btime %d winc %d binc %d" % (
            orologio[chess.WHITE], orologio[chess.BLACK], inc, inc)
        m.manda("position fen " + b.fen())
        bm, pd, prof, dt = m.cerca(go)
        if bm is None:
            print("      il motore non ha risposto: partita interrotta")
            break

        # Quando la previsione era giusta, in partita vera una fetta della ricerca l'avremmo gia'
        # fatta durante il pensiero dell'avversario: si scala dal costo, senza poter scendere sotto
        # un decimo di secondo (il tempo di rispondere resta).
        costo = dt * 1000
        if indovinato:
            costo = max(100.0, costo - PENSIERO_AVVERSARIO * 1000)

        spesi.append(costo)
        profondita.append(prof)
        if costo < inc:
            sotto_incremento += 1

        orologio[turno] = orologio[turno] - costo + inc
        if orologio[turno] <= 0:
            print("      BANDIERA alla mossa %d" % len(spesi))
            break

        b.push_uci(bm)
        previsione = pd

    residuo = min(orologio.values())
    m.chiudi()
    return {
        'spesi': spesi, 'profondita': profondita, 'sotto': sotto_incremento,
        'residuo': residuo, 'base': base, 'inc': inc,
        'ponder': (ponder_azzeccati, ponder_mandati),
    }


def stampa(nome, con_ponder, r):
    if not r['spesi']:
        print("   nessuna mossa misurata")
        return
    sp = r['spesi']
    print("   %-6s ponder %-2s | mosse %2d | mediana %5.1f s (incremento %.0f s) | max %5.1f s"
          % (nome, "SI" if con_ponder else "no", len(sp),
             statistics.median(sp) / 1000, r['inc'] / 1000, max(sp) / 1000))
    print("                     | sotto l'incremento %d su %d (%.0f%%) | profondita' mediana %d"
          % (r['sotto'], len(sp), 100.0 * r['sotto'] / len(sp),
             statistics.median(r['profondita'])))
    print("                     | orologio finale %.1f min su %.1f iniziali  <- il tempo mai speso"
          % (r['residuo'] / 60000, r['base'] / 60000))
    if con_ponder and r['ponder'][1]:
        print("                     | previsioni azzeccate %d su %d"
              % (r['ponder'][0], r['ponder'][1]))


def main():
    print(__doc__.split('Uso:')[0])
    print("=" * 78)
    for nome, base, inc in CONTROLLI:
        for con_ponder in (False, True):
            for fen in POSIZIONI[:1] if nome != "10+5" else POSIZIONI:
                r = partita(nome, base, inc, con_ponder, fen)
                stampa(nome, con_ponder, r)
        print("-" * 78)
    print("\nCosa guardare: se l'orologio finale resta alto E la percentuale di mosse sotto")
    print("l'incremento e' alta, stiamo accumulando tempo che non useremo mai. E' il valore di")
    print("partenza contro cui misurare qualunque ritocco ai tetti — che pero' resta la modifica")
    print("piu' rischiosa del motore: tre partite perse per tempo vengono da li'.")


if __name__ == '__main__':
    main()

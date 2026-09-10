"""Invarianti del percorso a OROLOGIO — il punto cieco dell'audit di fedelta'.

PERCHE' ESISTE. L'audit confronta con l'oracolo a PROFONDITA' FISSA, ed e' fortissimo li': conteggio
nodi identico fino a d24, 51/51 sulle mosse. Ma per costruzione non guarda mai il percorso a
orologio — e li' vivono TUTTI i guasti reali del bot: tre partite perse per tempo e due piantamenti
in cui il motore restava vivo senza mai rispondere (2026-09-08/09 e 2026-09-09/10, causa: la ricerca
si fermava lanciando un'eccezione, che saltava le UndoMove in sospeso e lasciava la posizione
profonda nell'albero; il conto lo presentava ExtractPonderFromTt con un IndexOutOfRange dentro il
Task, e in .NET l'eccezione non osservata di un Task non fa crashare il processo).

Qui non si puo' confrontare coi nodi dell'oracolo: a orologio uguale facciamo alberi diversi perche'
siamo piu' lenti. Si verificano invece INVARIANTI, che non dipendono dalla velocita' e che una
ricerca sana non puo' violare:

  1. ogni "go" produce un "bestmove", sempre, entro un tetto generoso  <- avrebbe preso i piantamenti
  2. il bestmove e' LEGALE nella posizione mandata                     <- prende la posizione corrotta
  3. anche la PV annunciata e' una sequenza legale                     <- idem, piu' in profondita'
  4. il tempo speso non supera una frazione ragionevole del residuo    <- prende gli sforamenti
  5. "stop" a meta' ricerca fa arrivare il bestmove subito
  6. orologi limite (1 ms, incremento enorme, residuo quasi zero) non fanno saltare nulla
  7. niente eccezioni su stderr

Le ricerche girano nello STESSO PROCESSO una dopo l'altra, come in partita: entrambi i piantamenti
sono avvenuti a meta' partita, non alla prima ricerca.

Uso:  python tools/invarianti_orologio.py [giri]
"""
import random
import re
import subprocess
import sys
import threading
import time

import chess

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET da usare. Finche' l'SDK 11 non e' installato a livello di sistema, il
# 'dotnet' del PATH e' il 10 e NON puo' eseguire un binario net11.0: si preferisce quindi
# l'installazione utente, ricadendo su quella di sistema appena c'e'.
import os as _os
_MUX = _os.path.expanduser(r'~\.dotnet11\dotnet.exe')
DOTNET = _MUX if _os.path.exists(_MUX) else 'dotnet'

MOTORE = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
SYZYGY = r'D:\Antcer\Documenti\ProgettiVS\ACMyChess\Syzygy'

GIRI = int(sys.argv[1]) if len(sys.argv) > 1 else 3
FRAZIONE_MASSIMA = 0.35   # nessuna mossa deve costare piu' di questo del proprio residuo
MARGINE_PIANTAMENTO = 90  # secondi oltre il residuo prima di dichiarare il motore piantato


class Motore:
    def __init__(self, threads=1):
        self.p = subprocess.Popen(MOTORE, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, bufsize=1)
        self.errori = []
        threading.Thread(target=self._leggi_stderr, daemon=True).start()
        for c in ["uci", "setoption name Threads value %d" % threads,
                  "setoption name Hash value 64", "setoption name OwnBook value false",
                  "setoption name SyzygyPath value " + SYZYGY, "ucinewgame", "isready"]:
            self.manda(c)
        time.sleep(1.0)

    def _leggi_stderr(self):
        for riga in self.p.stderr:
            if "Exception" in riga or "Unhandled" in riga or "Traceback" in riga:
                self.errori.append(riga.strip())

    def manda(self, c):
        self.p.stdin.write(c + "\n")
        self.p.stdin.flush()

    def cerca(self, comando_go, limite):
        """Ritorna (bestmove, pv, secondi) oppure (None, ..., secondi) se non risponde entro limite."""
        t0 = time.time()
        self.manda(comando_go)
        pv = []
        while time.time() - t0 < limite:
            riga = self.p.stdout.readline()
            if not riga:
                return None, pv, time.time() - t0
            if riga.startswith("info ") and " pv " in riga:
                pv = riga.split(" pv ", 1)[1].split()
            if riga.startswith("bestmove"):
                return riga.split()[1], pv, time.time() - t0
        return None, pv, time.time() - t0

    def chiudi(self):
        try:
            self.manda("quit")
        except Exception:
            pass
        self.p.kill()


def posizioni():
    """Una manciata di posizioni di natura diversa, piu' quelle limite gia' raccolte."""
    fen = ["rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
           "r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15",
           "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
           "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
           "8/8/5k1p/6PP/5K2/8/8/8 b - - 0 90",
           "5QN1/pp1r1ppk/5n2/2p4P/8/2P5/3B1PP1/q1KRR3 w - - 6 30"]
    try:
        with open(ROOT + r'\tools\fen-varie.txt', encoding='utf-8') as f:
            fen += [r.strip() for r in f if r.strip() and not r.startswith('#')][:10]
    except OSError:
        pass
    return fen


def controlla(guasti, condizione, descrizione):
    if not condizione:
        guasti.append(descrizione)
        print("   GUASTO: " + descrizione)


def prova_partita(guasti, threads, giro):
    """Una partita simulata: stesso processo, orologio che cala, mosse una dopo l'altra."""
    rng = random.Random(1000 + giro)
    m = Motore(threads)
    b = chess.Board(rng.choice(posizioni()))
    orologio = {chess.WHITE: rng.choice([10_000, 60_000, 300_000]), chess.BLACK: 60_000}
    inc = rng.choice([0, 1000, 5000])
    print("  partita %d (threads=%d, %d ms + %d ms) da %s" % (giro, threads, orologio[chess.WHITE], inc, b.fen()))

    for _ in range(14):
        if b.is_game_over():
            break
        turno = b.turn
        residuo = orologio[turno]
        go = "go wtime %d btime %d winc %d binc %d" % (orologio[chess.WHITE], orologio[chess.BLACK], inc, inc)
        m.manda("position fen " + b.fen())
        bm, pv, dt = m.cerca(go, residuo / 1000.0 + MARGINE_PIANTAMENTO)

        # 1. deve sempre rispondere
        controlla(guasti, bm is not None,
                  "nessun bestmove dopo %.0f s (residuo %d ms) su %s" % (dt, residuo, b.fen()))
        if bm is None:
            break

        # 2. il bestmove dev'essere legale nella posizione mandata
        legale = bm in [x.uci() for x in b.legal_moves]
        controlla(guasti, legale, "bestmove %s ILLEGALE in %s" % (bm, b.fen()))
        if not legale:
            break

        # 3. anche la PV annunciata dev'essere legale
        prova = b.copy()
        for i, mossa in enumerate(pv):
            try:
                mv = chess.Move.from_uci(mossa)
            except ValueError:
                controlla(guasti, False, "PV con mossa malformata '%s' su %s" % (mossa, b.fen()))
                break
            if mv not in prova.legal_moves:
                controlla(guasti, False, "PV illegale al passo %d (%s) su %s" % (i + 1, mossa, b.fen()))
                break
            prova.push(mv)

        # 4. il tempo speso non deve divorare l'orologio
        controlla(guasti, dt * 1000 <= residuo * FRAZIONE_MASSIMA + 1500,
                  "spesi %.1f s su %d ms di residuo (%.0f%%) in %s" % (dt, residuo, 100 * dt * 1000 / residuo, b.fen()))

        orologio[turno] = residuo - int(dt * 1000) + inc
        controlla(guasti, orologio[turno] > 0, "orologio SCADUTO in %s" % b.fen())
        if orologio[turno] <= 0:
            break
        b.push(chess.Move.from_uci(bm))

    controlla(guasti, not m.errori, "eccezioni su stderr: %s" % m.errori[:2])
    m.chiudi()


def prova_stop(guasti, giro):
    """"stop" a meta' ricerca: il bestmove deve arrivare subito."""
    m = Motore(1)
    b = chess.Board("r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15")
    for attesa in (0.05, 0.5, 3.0):
        m.manda("position fen " + b.fen())
        m.manda("go wtime 300000 btime 300000 winc 3000 binc 3000")
        time.sleep(attesa)
        t0 = time.time()
        m.manda("stop")
        bm = None
        while time.time() - t0 < 10:
            riga = m.p.stdout.readline()
            if not riga:
                break
            if riga.startswith("bestmove"):
                bm = riga.split()[1]
                break
        dt = time.time() - t0
        print("   stop dopo %.2fs -> %s in %.2fs" % (attesa, bm, dt))
        controlla(guasti, bm is not None, "nessun bestmove dopo 'stop' (attesa %.2fs)" % attesa)
        controlla(guasti, dt < 5, "'stop' ha impiegato %.1f s a produrre bestmove" % dt)
    controlla(guasti, not m.errori, "eccezioni su stderr durante 'stop': %s" % m.errori[:2])
    m.chiudi()


def prova_orologi_limite(guasti):
    """Orologi assurdi: gia' una volta un Math.Clamp e' esploso con l'orologio quasi a zero."""
    m = Motore(1)
    b = chess.Board("r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15")
    # Orologio a ZERO: la fonte NON fa gestione del tempo (use_time_management() falso) e cerca
    # finche' la GUI non manda "stop" — verificato sull'oracolo, che dopo 6 s non aveva ancora
    # risposto ed era vivo. Quindi qui l'invariante NON e' "deve rispondere": e' "non deve morire,
    # e deve rispondere allo stop". Prima di correggere il motore, questo caso lo faceva CRASHARE
    # (ArgumentOutOfRangeException, TimeSpan overflow).
    m.manda("position fen " + b.fen())
    m.manda("go wtime 0 btime 0 winc 0 binc 0")
    time.sleep(3)
    controlla(guasti, m.p.poll() is None, "il motore e' MORTO con l'orologio a zero")
    if m.p.poll() is None:
        bm, _, dt = m.cerca("stop", 10)
        print("   %-46s -> %s in %.2fs (dopo stop)" % ("go wtime 0 btime 0", bm, dt))
        controlla(guasti, bm is not None, "nessun bestmove dopo 'stop' con orologio a zero")

    casi = ["go wtime 1 btime 1 winc 0 binc 0",
            "go wtime 50 btime 50 winc 0 binc 0",
            "go wtime 100 btime 100 winc 100000 binc 100000",
            "go movetime 1",
            "go depth 1",
            "go wtime 2000000000 btime 2000000000 winc 0 binc 0" .replace("2000000000", "600000")]
    for c in casi:
        m.manda("position fen " + b.fen())
        bm, _, dt = m.cerca(c, 30)
        print("   %-46s -> %s in %.2fs" % (c, bm, dt))
        controlla(guasti, bm is not None, "nessun bestmove per '%s'" % c)
        controlla(guasti, bm in [x.uci() for x in b.legal_moves] if bm else False,
                  "bestmove illegale '%s' per '%s'" % (bm, c))
    controlla(guasti, not m.errori, "eccezioni su stderr sugli orologi limite: %s" % m.errori[:2])
    m.chiudi()


def main():
    guasti = []
    print("INVARIANTI DEL PERCORSO A OROLOGIO\n")
    print(" orologi limite:")
    prova_orologi_limite(guasti)
    print("\n 'stop' a meta' ricerca:")
    prova_stop(guasti, 0)
    print("\n partite simulate:")
    for giro in range(GIRI):
        prova_partita(guasti, 1 if giro % 2 == 0 else 4, giro)

    print("\n" + "=" * 70)
    if guasti:
        print("VIOLAZIONI: %d" % len(guasti))
        for g in guasti:
            print("  - " + g)
        sys.exit(1)
    print("nessuna violazione degli invarianti")


if __name__ == '__main__':
    main()

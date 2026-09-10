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
  8. PONDERING: durante "go ponder" il motore NON annuncia bestmove nemmeno se ha finito, e lo
     annuncia subito a "ponderhit" o "stop"; dopo entrambi resta sano  <- percorso mai esercitato

Le ricerche girano nello STESSO PROCESSO una dopo l'altra, come in partita: entrambi i piantamenti
sono avvenuti a meta' partita, non alla prima ricerca.

Uso:  python tools/invarianti_orologio.py [giri]
"""
import queue
import random
import re
import subprocess
import sys
import threading
import time

import chess

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

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
        self.ultima_ponder = None
        # UN SOLO lettore di stdout, che riversa tutto in una coda. Due thread che fanno readline()
        # sulla stessa pipe si rubano le righe a vicenda e si bloccano: e' successo davvero il
        # 2026-09-10 provando il pondering, e sembrava un piantamento del motore.
        self.righe = queue.Queue()
        threading.Thread(target=self._leggi_stdout, daemon=True).start()
        threading.Thread(target=self._leggi_stderr, daemon=True).start()
        for c in ["uci", "setoption name Threads value %d" % threads,
                  "setoption name Hash value 64", "setoption name OwnBook value false",
                  "setoption name SyzygyPath value " + SYZYGY, "ucinewgame", "isready"]:
            self.manda(c)
        time.sleep(1.0)

    def _leggi_stdout(self):
        for riga in self.p.stdout:
            self.righe.put(riga)
        self.righe.put(None)   # pipe chiusa: il motore e' morto

    def _leggi_stderr(self):
        for riga in self.p.stderr:
            if "Exception" in riga or "Unhandled" in riga or "Traceback" in riga:
                self.errori.append(riga.strip())

    def manda(self, c):
        self.p.stdin.write(c + "\n")
        self.p.stdin.flush()

    def _prossima_riga(self, scadenza):
        """La prossima riga dalla coda, o None se scade il tempo (o il motore muore)."""
        rimasto = scadenza - time.time()
        if rimasto <= 0:
            return None
        try:
            return self.righe.get(timeout=rimasto)
        except queue.Empty:
            return None

    def cerca(self, comando_go, limite):
        """Ritorna (bestmove, pv, secondi) oppure (None, ..., secondi) se non risponde entro limite."""
        t0 = time.time()
        self.manda(comando_go)
        pv = []
        while True:
            riga = self._prossima_riga(t0 + limite)
            if riga is None:
                return None, pv, time.time() - t0
            if riga.startswith("info ") and " pv " in riga:
                pv = riga.split(" pv ", 1)[1].split()
            if riga.startswith("bestmove"):
                pezzi = riga.split()
                self.ultima_ponder = pezzi[3] if len(pezzi) > 3 else None
                return pezzi[1], pv, time.time() - t0

    def bestmove_arrivato(self, entro):
        """Ascolta per 'entro' secondi SENZA mandare nulla: serve a verificare che durante il
        pondering il motore stia ZITTO. Ritorna il bestmove se ne arriva uno (violazione), None
        se il motore tace come deve. Le righe "info" intanto si consumano regolarmente."""
        scadenza = time.time() + entro
        while True:
            riga = self._prossima_riga(scadenza)
            if riga is None:
                return None
            if riga.startswith("bestmove"):
                return riga.split()[1]

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


def prova_pondering(guasti):
    """Le tre sequenze che lichess-bot produce davvero quando "ponder: true".

    Il pondering e' l'unico percorso dell'orologio che non era mai stato esercitato, e tocca
    esattamente il meccanismo che ha causato i due piantamenti: la ricerca che deve fermarsi su
    comando. In piu' ha una regola sua, che e' quella che si rompe piu' facilmente: durante
    "go ponder" il motore NON deve annunciare bestmove NEMMENO SE HA GIA' FINITO DI CERCARE
    (uci.cpp:116-119), ma deve aspettare "ponderhit" o "stop".
    """
    m = Motore(1)
    b = chess.Board("r3r1k1/2p2ppp/p1p1bn2/8/1q2P3/2NPQN2/PPP3PP/R4RK1 b - - 2 15")

    # --- si gioca una mossa normale, per avere una previsione vera da ponderare -----------------
    m.manda("position fen " + b.fen())
    bm, _, _ = m.cerca("go wtime 60000 btime 60000 winc 1000 binc 1000", 40)
    controlla(guasti, bm is not None, "nessun bestmove nella mossa che precede il pondering")
    if bm is None:
        m.chiudi()
        return
    previsione = m.ultima_ponder
    controlla(guasti, previsione is not None,
              "nessuna mossa di ponder annunciata: senza previsione il pondering non parte mai")
    if previsione is None:
        m.chiudi()
        return

    b.push_uci(bm)
    controlla(guasti, previsione in [x.uci() for x in b.legal_moves],
              "la mossa di ponder '%s' NON e' legale nella posizione dopo '%s'" % (previsione, bm))
    b.push_uci(previsione)
    posizione_ponder = b.fen()
    print("   mossa %s, previsione %s -> si pondera su %s" % (bm, previsione, posizione_ponder))

    # --- 1. IL CASO SOSPETTO: la ricerca finisce PRIMA del ponderhit ---------------------------
    # Con un tetto di profondita' bassa la ricerca si esaurisce in un attimo; il motore deve
    # comunque restare zitto. Se qui esce un bestmove, in partita lichess-bot riceverebbe una
    # risposta a una mossa che l'avversario non ha ancora giocato.
    m.manda("position fen " + posizione_ponder)
    m.manda("go ponder depth 6")
    intruso = m.bestmove_arrivato(4)
    controlla(guasti, intruso is None,
              "bestmove '%s' annunciato DURANTE 'go ponder' (la ricerca era finita e non ha aspettato)" % intruso)
    bm2, _, dt = m.cerca("ponderhit", 30)
    print("   ricerca esaurita + ponderhit -> %s in %.2fs" % (bm2, dt))
    controlla(guasti, bm2 is not None, "nessun bestmove dopo 'ponderhit' su ricerca gia' esaurita")
    controlla(guasti, dt < 5, "'ponderhit' su ricerca esaurita ha impiegato %.1f s" % dt)
    controlla(guasti, bm2 in [x.uci() for x in b.legal_moves] if bm2 else False,
              "bestmove illegale '%s' dopo 'ponderhit'" % bm2)

    # --- 2. IL CASO NORMALE: si pondera, poi l'avversario indovina ------------------------------
    m.manda("position fen " + posizione_ponder)
    m.manda("go ponder wtime 60000 btime 60000 winc 1000 binc 1000")
    intruso = m.bestmove_arrivato(3)
    controlla(guasti, intruso is None, "bestmove '%s' annunciato durante 'go ponder' normale" % intruso)
    t0 = time.time()
    bm3, pv, dt = m.cerca("ponderhit", 60)
    print("   ponder 3s + ponderhit -> %s in %.2fs" % (bm3, dt))
    controlla(guasti, bm3 is not None, "nessun bestmove dopo 'ponderhit'")
    controlla(guasti, bm3 in [x.uci() for x in b.legal_moves] if bm3 else False,
              "bestmove illegale '%s' dopo 'ponderhit'" % bm3)
    # Il tempo gia' speso ponderando NON e' gratis: si somma al budget della mossa (engine.cpp:262).
    # Con 60 s di residuo, tre secondi di pondering piu' la coda non devono sforare il tetto solito.
    controlla(guasti, dt < 60 * FRAZIONE_MASSIMA,
              "dopo 'ponderhit' la mossa e' costata %.1f s su 60 s di residuo" % dt)
    if pv:
        bb = chess.Board(posizione_ponder)
        legale = True
        for mossa in pv:
            try:
                bb.push_uci(mossa)
            except ValueError:
                legale = False
                break
        controlla(guasti, legale, "PV illegale dopo 'ponderhit': %s da %s" % (pv[:6], posizione_ponder))

    # --- 3. IL CASO MANCATO: l'avversario gioca ALTRO, si butta via e si riparte ----------------
    m.manda("position fen " + posizione_ponder)
    m.manda("go ponder wtime 60000 btime 60000 winc 1000 binc 1000")
    time.sleep(2)
    bm4, _, dt = m.cerca("stop", 15)
    print("   ponder 2s + stop -> %s in %.2fs" % (bm4, dt))
    controlla(guasti, bm4 is not None, "nessun bestmove dopo 'stop' durante il pondering")
    controlla(guasti, dt < 5, "'stop' durante il pondering ha impiegato %.1f s" % dt)

    # E adesso il punto vero: il motore e' rimasto SANO? Una ricerca normale subito dopo, su una
    # posizione diversa. E' qui che si vedrebbe una posizione lasciata corrotta dall'arresto —
    # e' esattamente cosi' che si manifestavano i due piantamenti.
    b2 = chess.Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")
    m.manda("position fen " + b2.fen())
    bm5, pv5, dt = m.cerca("go wtime 30000 btime 30000 winc 0 binc 0", 60)
    print("   ricerca normale dopo lo stop -> %s in %.2fs" % (bm5, dt))
    controlla(guasti, bm5 is not None, "nessun bestmove nella ricerca dopo uno stop di pondering")
    controlla(guasti, bm5 in [x.uci() for x in b2.legal_moves] if bm5 else False,
              "bestmove ILLEGALE '%s' dopo uno stop di pondering: posizione corrotta" % bm5)

    controlla(guasti, not m.errori, "eccezioni su stderr durante il pondering: %s" % m.errori[:2])
    controlla(guasti, m.p.poll() is None, "il motore e' MORTO durante le prove di pondering")
    m.chiudi()


def main():
    guasti = []
    print("INVARIANTI DEL PERCORSO A OROLOGIO\n")
    print(" orologi limite:")
    prova_orologi_limite(guasti)
    print("\n 'stop' a meta' ricerca:")
    prova_stop(guasti, 0)
    print("\n pondering:")
    prova_pondering(guasti)
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

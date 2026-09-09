"""Watchdog per il PIANTAMENTO del motore StockfishSharp sul bot (notte 2026-09-08/09).

Il caso da diagnosticare: partita Dg8bxpWH contro TroutBot. Alle 02:58:09 il motore emette
"info depth 30 ... nodes 2867336" e poi PIU' NIENTE — nessuna riga info, nessun bestmove — per
oltre 9 minuti, fino alla bandierina. Il processo lichess-bot era vivo (il thread principale
continuava a loggare), a bloccarsi era solo il thread della partita in attesa del bestmove.
Nessun crash, niente su stderr, niente nel log eventi di Windows. Non riproducibile offline:
il replay fedele dell'intera sessione UCI (stesso processo, stesse "go", stessi orologi)
risponde in 15,7 s.

Un piantamento senza stack non e' analizzabile. Questo script NON tocca il motore e NON prova a
rimediare: osserva il log di lichess-bot e, quando una ricerca resta muta troppo a lungo,
fotografa il processo prima che il caso svanisca.

COSA CATTURA, e perche' proprio questo:
  * il TEMPO DI CPU del processo motore, campionato piu' volte -> e' la misura che DISCRIMINA le
    due ipotesi in piedi: se la CPU brucia, il thread sta girando dentro un ciclo (un singolo
    nodo, dove ne' la scadenza morbida ogni 2048 visite ne' il controllo fra iterazioni possono
    arrivare); se la CPU e' ferma, e' bloccato in attesa — tipicamente una scrittura su stdout
    verso una pipe che il lato python non sta drenando;
  * tre report di "dotnet-stack" a distanza di 15 s -> gli stack gestiti di TUTTI i thread. Se
    sono identici e la CPU e' ferma: attesa. Se cambiano: ciclo. Se sono identici ma la CPU
    brucia: ciclo stretto dentro un metodo solo.

COPIA IN ESERCIZIO: il watchdog gira da `lichess-bot/watchdog_stockfishsharp.py`, avviato da
`lichess-bot/start_bot_stockfishsharp_watchdog.bat` — quella cartella e' il checkout di
lichess-bot-devs, con un remoto che non e' nostro, quindi qui c'e' la copia VERSIONATA. Da
riunificare (il .bat puntera' direttamente a questo file) alla prima occasione in cui il bot e'
fermo: stanotte stava giocando e non valeva la pena riavviarlo.

Uso:
    python watchdog_stockfishsharp.py [secondi_di_silenzio]

Il valore predefinito e' 120 s: a 10+10 con l'orologio pieno il tetto massimo per mossa sta
sotto il minuto e mezzo, quindi due minuti di silenzio non sono lentezza, sono un piantamento.
"""
import os
import re
import shutil
import subprocess
import sys
import time

BASE = os.path.dirname(os.path.abspath(__file__))
LOG = os.environ.get("WATCHDOG_LOG",
                     os.path.join(BASE, "lichess_bot_auto_logs", "lichess-bot.log"))
USCITA = os.path.join(BASE, "watchdog")
SILENZIO = int(sys.argv[1]) if len(sys.argv) > 1 else 120
FULL_MAX = 2            # quanti dump COMPLETI al massimo per avvio (738 MB l'uno)
FULL_GB_MINIMI = 20     # sotto questo spazio libero il Full si salta e restano i Mini
full_presi = 0

RIGA = re.compile(r"<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")


def log(msg):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg), flush=True)


def cpu_secondi(pid):
    """Tempo di CPU consumato finora dal processo, in secondi. None se il processo non c'e' piu'.

    Il ToString invariante e' necessario: con le impostazioni italiane PowerShell stamperebbe
    "1,828125" e float() fallirebbe — proprio nel momento in cui la misura serve.
    """
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "(Get-Process -Id %d -ErrorAction Stop).TotalProcessorTime.TotalSeconds"
             ".ToString([System.Globalization.CultureInfo]::InvariantCulture)" % pid],
            capture_output=True, text=True, timeout=30)
        return float(out.stdout.strip())
    except Exception:
        return None


def stack(pid):
    """Stack gestiti di tutti i thread. Verificato su una ricerca vera: 0,4 s, mostra la
    ricorsione di Negamax fino a NnueEvaluate."""
    try:
        out = subprocess.run(["dotnet-stack", "report", "-p", str(pid)],
                             capture_output=True, text=True, timeout=180)
        return out.stdout + ("\n--- stderr ---\n" + out.stderr if out.stderr.strip() else "")
    except Exception as e:
        return "dotnet-stack fallito: %r" % (e,)


def gb_liberi():
    try:
        return shutil.disk_usage(USCITA).free / 1e9
    except Exception:
        return 0.0


def dump(pid, percorso, tipo):
    """Dump del processo. Si analizza con:  dotnet-dump analyze <file>  poi pstacks / clrstack -all.

    MISURATO su una ricerca vera (2026-09-09): Mini 2,1 MB in 0,2 s e contiene GIA' tutti gli stack
    gestiti (la ricorsione di Negamax fino a NnueEvaluate); Full 738 MB in 0,6 s, e in piu' ha lo
    stato degli oggetti — che per questo bug e' la domanda vera (quanto valeva _softDeadlineMs?
    la ricerca era ancora dentro un nodo?). Da qui la politica di CatturaDump: il Mini sempre, il
    Full solo le prime volte e solo con disco abbondante."""
    try:
        r = subprocess.run(["dotnet-dump", "collect", "-p", str(pid), "-o", percorso,
                            "--type", tipo], capture_output=True, text=True, timeout=600)
        if os.path.exists(percorso):
            return "dump %s: %s (%.1f MB)" % (tipo, percorso, os.path.getsize(percorso) / 1e6)
        return "dump %s NON creato: %s" % (tipo, (r.stdout + r.stderr).strip()[-300:])
    except Exception as e:
        return "dotnet-dump %s fallito: %r" % (tipo, e)


def cattura(pid, contesto, f, nome_base):
    """Prima il dump completo (istantaneo, e fotografa tutto), poi tre stack a 15 s di distanza
    con il tempo di CPU prima e dopo ognuno."""
    f.write("=== PIANTAMENTO RILEVATO %s ===\n" % time.strftime("%Y-%m-%d %H:%M:%S"))
    f.write("pid motore: %d   silenzio richiesto: %d s\n\n" % (pid, SILENZIO))
    f.write("--- ultime righe scambiate col motore ---\n")
    for r in contesto:
        f.write(r + "\n")
    f.write("\n")

    # Il Mini costa 2 MB: si prende SEMPRE, quante volte serva.
    esito = dump(pid, nome_base + ".mini.dmp", "Mini")
    log(esito)
    f.write(esito + "\n")

    # Il Full costa 738 MB: solo le prime FULL_MAX volte, e solo se restano almeno FULL_GB_MINIMI
    # di disco. Un watchdog che riempie il disco farebbe piu' danni del bug che deve diagnosticare.
    global full_presi
    if full_presi >= FULL_MAX:
        motivo = "Full saltato: gia' presi %d (bastano i Mini per gli stack)" % full_presi
    elif gb_liberi() < FULL_GB_MINIMI:
        motivo = "Full saltato: solo %.0f GB liberi (soglia %d GB)" % (gb_liberi(), FULL_GB_MINIMI)
    else:
        motivo = dump(pid, nome_base + ".full.dmp", "Full")
        full_presi += 1
    log(motivo)
    f.write(motivo + "\n")
    f.flush()

    for i in range(3):
        c0 = cpu_secondi(pid)
        if c0 is None:
            f.write("\n!!! il processo %d NON ESISTE PIU' (morto silenziosamente) !!!\n" % pid)
            f.flush()
            return
        s = stack(pid)
        time.sleep(15)
        c1 = cpu_secondi(pid)
        f.write("\n=== fotografia %d/3  %s ===\n" % (i + 1, time.strftime("%H:%M:%S")))
        f.write("CPU: %.2fs -> %.2fs in ~15s reali  =>  %s\n\n" % (
            c0, c1 if c1 is not None else -1,
            "BRUCIA CPU (ciclo)" if c1 is not None and c1 - c0 > 5 else "CPU FERMA (attesa/blocco)"))
        f.write(s)
        f.flush()
    f.write("\n=== fine cattura ===\n")
    f.flush()


def main():
    os.makedirs(USCITA, exist_ok=True)
    log("watchdog avviato — soglia di silenzio %d s, log %s" % (SILENZIO, LOG))
    while not os.path.exists(LOG):
        log("in attesa che il log esista...")
        time.sleep(5)

    f = open(LOG, "r", encoding="utf-8", errors="replace")
    f.seek(0, os.SEEK_END)      # solo il futuro: la storia gia' scritta non ci interessa
    pos = f.tell()

    pid = None                  # motore attualmente in ascolto
    in_ricerca = False          # una "go" e' partita e il bestmove non e' ancora arrivato
    t_ultima_uscita = time.time()
    t_go = 0.0
    contesto = []               # ultime righe scambiate, per il rapporto
    gia_catturato = False       # una sola cattura per ricerca
    base_cattura = None         # cattura in attesa di verdetto: piantamento vero o solo lentezza?

    while True:
        riga = f.readline()
        if not riga:
            # rotazione del log (lichess-bot ne apre uno nuovo a mezzanotte)
            try:
                if os.path.getsize(LOG) < pos:
                    log("log ruotato, riapro")
                    f.close()
                    f = open(LOG, "r", encoding="utf-8", errors="replace")
                    pos = 0
                    continue
            except OSError:
                pass
            # nessuna riga nuova: e' qui che si decide se siamo piantati
            if in_ricerca and not gia_catturato and pid:
                muto_da = time.time() - t_ultima_uscita
                if muto_da > SILENZIO:
                    base = os.path.join(USCITA, "piantamento_%s_pid%d" % (
                        time.strftime("%Y%m%d_%H%M%S"), pid))
                    nome = base + ".txt"
                    log("PIANTAMENTO: %d s senza una riga dal motore (pid %d). Catturo in %s"
                        % (muto_da, pid, nome))
                    with open(nome, "w", encoding="utf-8") as out:
                        out.write("silenzio: %.0f s   dalla 'go': %.0f s\n\n"
                                  % (muto_da, time.time() - t_go))
                        cattura(pid, contesto[-12:], out, base)
                    gia_catturato = True
                    base_cattura = base
                    log("cattura completata: %s" % nome)
            time.sleep(2)
            pos = f.tell()
            continue

        pos = f.tell()
        m = RIGA.search(riga)
        if not m:
            continue
        p, verso, testo = int(m.group(1)), m.group(2), m.group(3).strip()
        pid = p
        contesto.append("%s %s %s" % (time.strftime("%H:%M:%S"), verso, testo[:200]))
        if len(contesto) > 40:
            del contesto[:20]

        if verso == "<<":
            if testo.startswith("go"):
                in_ricerca, gia_catturato = True, False
                t_go = t_ultima_uscita = time.time()
        else:
            t_ultima_uscita = time.time()

            # Il motore ha ripreso a parlare DOPO una cattura: non era un piantamento, solo una
            # ricerca lenta. I dump non servono piu' e il Full pesa 665 MB — si cancellano subito.
            # Resta il rapporto .txt (pochi KB, con gli stack di dotnet-stack gia' dentro): documenta
            # la lentezza senza costare disco. Se la ricerca arriva fino al bestmove si riarma anche
            # la cattura, cosi' un piantamento vero piu' avanti nella stessa partita viene preso.
            if base_cattura:
                liberati = 0.0
                for suffisso in (".mini.dmp", ".full.dmp"):
                    p_dmp = base_cattura + suffisso
                    if os.path.exists(p_dmp):
                        liberati += os.path.getsize(p_dmp) / 1e6
                        try:
                            os.remove(p_dmp)
                        except OSError as e:
                            log("non riesco a cancellare %s: %s" % (p_dmp, e))
                with open(base_cattura + ".txt", "a", encoding="utf-8") as ann:
                    ann.write("\n=== RIPRESA: il motore ha ricominciato a parlare alle %s "
                              "(%.0f s dopo la 'go'). NON era un piantamento: dump cancellati, "
                              "%.0f MB liberati. ===\n"
                              % (time.strftime("%H:%M:%S"), time.time() - t_go, liberati))
                log("ricerca ripresa: non era un piantamento, %.0f MB di dump cancellati" % liberati)
                base_cattura = None

            if testo.startswith("bestmove"):
                gia_catturato = False
                # Una riga per ricerca conclusa: e' il BATTITO del watchdog. Serve a due cose —
                # dimostrare che sta davvero seguendo lo scambio col motore (un watchdog che non
                # riconosce le righe sarebbe muto esattamente come uno che non scatta mai), e
                # lasciare la tabella dei tempi per mossa senza doverla ricavare dopo dal log.
                if in_ricerca:
                    log("ricerca conclusa in %5.1f s  (pid %d)  %s"
                        % (time.time() - t_go, pid, testo[:40]))
                in_ricerca = False


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        log("watchdog fermato")

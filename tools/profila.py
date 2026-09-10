# -*- coding: utf-8 -*-
"""Profila una ricerca lunga: avvia il motore con stdin aperto, gli fa partire un 'go depth'
che dura decine di secondi, e ci attacca dotnet-trace per PID.

Non si puo' profilare 'bench' passandolo come argomento: il nostro Uci legge SOLO da stdin, quindi
'dotnet ... bench' resta li' fermo ad aspettare (provato: dieci minuti di traccia di un processo
inattivo).
"""
import os
import subprocess
import sys
import time

DLL = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll'
TRACE = os.path.expanduser(r'~\.dotnet\tools\dotnet-trace.exe')
OUT = sys.argv[1] if len(sys.argv) > 1 else 'profilo_mp.nettrace'
SECONDI = int(sys.argv[2]) if len(sys.argv) > 2 else 40
PROFONDITA = int(sys.argv[3]) if len(sys.argv) > 3 else 24

# Posizione FUORI LIBRO: con il libro acceso il motore risponde senza cercare e si profila il nulla
# (trappola gia' caduta due volte). Qui il libro e' spento a mano, ma la posizione e' comunque di
# mediogioco vero, per avere il mix di stadi del MovePicker che si vede in partita.
FEN = 'r1bq1rk1/pp2bppp/2n1pn2/3p4/3P4/2NBPN2/PP3PPP/R1BQ1RK1 w - - 0 9'

p = subprocess.Popen(['dotnet', DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, bufsize=1)
for c in ['uci', 'setoption name Threads value 1', 'setoption name Hash value 256',
          'setoption name OwnBook value false', 'ucinewgame', 'isready',
          'position fen ' + FEN, 'go depth %d' % PROFONDITA]:
    p.stdin.write(c + '\n')
p.stdin.flush()

time.sleep(3)   # lascia superare il caricamento della rete NNUE e le prime iterazioni
if p.poll() is not None:
    sys.exit('il motore e\' gia\' uscito')

print('motore pid %d, traccia per %d s...' % (p.pid, SECONDI))
subprocess.run([TRACE, 'collect', '-p', str(p.pid), '--profile', 'dotnet-sampled-thread-time',
                '--duration', '00:00:%02d' % SECONDI, '-o', OUT],
               stdout=subprocess.DEVNULL)

try:
    p.stdin.write('stop\nquit\n')
    p.stdin.flush()
except Exception:
    pass
p.kill()
print('scritto ' + OUT)

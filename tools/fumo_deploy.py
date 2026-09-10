# -*- coding: utf-8 -*-
"""Prova di fumo del binario che va sul bot, con le stesse opzioni della config del bot.

Non e' un test di correttezza (per quello ci sono i 151 test e l'accordo con l'oracolo): serve a
prendere le rotture da DEPLOY, quelle che i test non vedono perche' girano sul progetto e non sul
binario pubblicato — rete NNUE non trovata, libro non copiato, tablebase non caricate, e i due
percorsi dell'orologio che ci hanno gia' fatto perdere partite vere.
"""
import re
import subprocess
import sys

EXE = sys.argv[1]
SYZYGY = 'D:/Antcer/Documenti/ProgettiVS/ACMyChess/Syzygy'


def sessione(comandi, attesa=90):
    p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.STDOUT, text=True, bufsize=1)
    out, _ = p.communicate('\n'.join(comandi) + '\nquit\n', timeout=attesa)
    return out


BASE = ['uci', 'setoption name Threads value 1', 'setoption name Hash value 64',
        'setoption name SyzygyPath value ' + SYZYGY, 'isready']

esiti = []


def controlla(nome, ok, dettaglio=''):
    esiti.append(ok)
    print('  %-42s %s   %s' % (nome, 'OK ' if ok else 'ROTTO', dettaglio))


# 1. avvio, rete, libro, tablebase
out = sessione(BASE + ['ucinewgame', 'isready'])
controlla('rete NNUE caricata', 'nnue' in out.lower())
controlla('libro aperture caricato', 'book' in out.lower() or 'libro' in out.lower())
m = re.search(r'(\d+)\s*(?:WDL|tablebase)', out, re.I)
controlla('tablebase Syzygy trovate', m is not None, m.group(0) if m else out[-200:])

# 2. orologio normale, come in partita
out = sessione(BASE + ['position startpos moves e2e4 e7e5', 'go wtime 60000 btime 60000 winc 1000 binc 1000'])
m = re.search(r'bestmove (\w+)', out)
controlla('ricerca a orologio normale', m is not None, m.group(1) if m else '')

# 3. finale a 5 pezzi: deve interrogare le tablebase.
# ATTENZIONE AL FEN: con diritti di ARROCCO le Syzygy non sono interrogabili, e si vede "tbhits 0"
# che sembra una tablebase rotta ed e' invece il FEN sbagliato. Preso in castagna il 2026-09-10:
# la stessa posizione con "w K -" dava 0 e con "w - -" da' 15.
out = sessione(BASE + ['setoption name OwnBook value false',
                       'position fen 8/8/8/4k3/8/8/4P3/4K2R w - - 0 1',
                       'go wtime 60000 btime 60000'])
m = re.search(r'bestmove (\w+)', out)
tb = max([int(x) for x in re.findall(r'tbhits (\d+)', out)] or [0])
controlla('finale a 5 pezzi, tablebase interrogate', m is not None and tb > 0,
          '%s, tbhits %d' % (m.group(1) if m else '-', tb))

# 4. orologio a ZERO seguito da stop: e' il caso che uccideva il processo
out = sessione(BASE + ['position startpos moves e2e4', 'go wtime 0 btime 0', 'stop'], attesa=60)
m = re.search(r'bestmove (\w+)', out)
controlla('orologio a zero + stop', m is not None, m.group(1) if m else out[-200:])

# 5. bench, per la parita' di nodi
out = sessione(['bench'], attesa=180)
m = re.search(r'Nodes searched\s*:\s*(\d+)', out)
nodi = int(m.group(1)) if m else -1
controlla('bench identico al nodo', nodi == 2520660, str(nodi))

print('\n  %d su %d' % (sum(esiti), len(esiti)))
sys.exit(0 if all(esiti) else 1)

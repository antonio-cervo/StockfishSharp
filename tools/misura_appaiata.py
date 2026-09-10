# -*- coding: utf-8 -*-
"""Confronta la VELOCITA' di due build a coppie appaiate.

PERCHE' NON BASTA FARE LA MEDIA DI DUE SERIE. Questo portatile oscilla del 12% fra un giro e
l'altro (frequenza, temperatura, cos'altro gira): due serie indipendenti da 5 giri l'una possono
dire "nessuna differenza" mentre la differenza c'e' eccome. E' successo davvero il 2026-09-10 con
la passata NNUE fusa: medie indipendenti -> "uguale", coppie appaiate -> +3,8% e 8 volte su 8.

Il metodo: A e B girano SUBITO uno dopo l'altro, e la coppia e' l'unita' di misura. Ogni coppia
alterna anche l'ordine (A,B poi B,A) cosi' un eventuale riscaldamento progressivo non favorisce
sempre lo stesso. Si guarda soprattutto il CONTEGGIO DELLE DIREZIONI: 8 coppie su 8 e' un
risultato, +3% di media con 4 coppie su 8 e' rumore.

Serve anche la controprova sui NODI: se i due bench non danno lo stesso numero di nodi non stiamo
confrontando la stessa ricerca, e il rapporto di tempo non vuol dire niente.

Uso:
    python tools/misura_appaiata.py <vecchio.dll> <nuovo.dll> [coppie]
"""
import re
import statistics
import subprocess
import sys

DOTNET = 'dotnet'
COPPIE = 8


def bench(dll):
    """Un giro di bench: restituisce (millisecondi, nodi)."""
    p = subprocess.run([DOTNET, dll], input='bench\nquit\n',
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       universal_newlines=True)
    ms = re.search(r'Total time \(ms\)\s*:\s*(\d+)', p.stdout)
    nodi = re.search(r'Nodes searched\s*:\s*(\d+)', p.stdout)
    if not ms or not nodi:
        sys.exit('bench non riuscito su ' + dll + '\n' + p.stdout[-2000:])
    return int(ms.group(1)), int(nodi.group(1))


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    vecchio, nuovo = sys.argv[1], sys.argv[2]
    coppie = int(sys.argv[3]) if len(sys.argv) > 3 else COPPIE

    print('vecchio: ' + vecchio)
    print('nuovo:   ' + nuovo)
    print('%d coppie, ordine alternato\n' % coppie)

    guadagni = []
    nodi_visti = set()
    for i in range(coppie):
        if i % 2 == 0:                       # A poi B
            tv, nv = bench(vecchio)
            tn, nn = bench(nuovo)
        else:                                # B poi A, per non favorire sempre lo stesso
            tn, nn = bench(nuovo)
            tv, nv = bench(vecchio)
        nodi_visti.add(nv)
        nodi_visti.add(nn)
        g = (tv - tn) * 100.0 / tv           # positivo = il nuovo e' piu' veloce
        guadagni.append(g)
        print('  coppia %d:  vecchio %5d ms   nuovo %5d ms   %+6.1f%%' % (i + 1, tv, tn, g))

    a_favore = sum(1 for g in guadagni if g > 0)
    print('\n  media   %+.1f%%' % (sum(guadagni) / len(guadagni)))
    print('  mediana %+.1f%%' % statistics.median(guadagni))
    print('  a favore del nuovo: %d su %d' % (a_favore, len(guadagni)))

    if len(nodi_visti) == 1:
        print('\n  nodi identici in tutti i giri: %d — stessa ricerca, confronto valido'
              % nodi_visti.pop())
    else:
        print('\n  ATTENZIONE: conteggi di nodi DIVERSI %s' % sorted(nodi_visti))
        print('  I due build non cercano la stessa cosa: il rapporto di tempo non vuol dire nulla.')


if __name__ == '__main__':
    main()

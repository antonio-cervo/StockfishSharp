"""Confronta bench posizione-per-posizione fra il nostro motore e l'oracolo.

Per ogni posizione prende l'ULTIMA riga "info depth ..." prima del "bestmove" (cioe' il risultato
finale a profondita' 13) e ne estrae punteggio e mossa scelta. Allinea per indice di posizione.
"""
import subprocess, re, sys

ROOT = r'D:\Antcer\Documenti\ProgettiVS\StockfishSharp'
# Il muxer .NET del PATH: dal 2026-09-10 l'SDK 11 e' installato a livello di sistema, quindi
# esegue direttamente un binario net11.0.
DOTNET = 'dotnet'

OURS = [DOTNET, ROOT + r'\StockfishSharp.Uci\bin\Release\net11.0\StockfishSharpUci.dll']
ORACLE = [ROOT + r'\stockfish-reference-binary\stockfish\stockfish-windows-x86-64-universal.exe']


def run(cmd):
    r = subprocess.run(cmd, input="bench 16 1 13\nquit\n", capture_output=True, text=True, cwd=ROOT)
    out = r.stdout + r.stderr
    positions = []
    cur = None
    for line in out.splitlines():
        if line.startswith("info depth ") and " score " in line:
            cur = line
        elif line.startswith("bestmove"):
            mv = line.split()[1]
            sc = "?"
            if cur:
                m = re.search(r'score (cp -?\d+|mate -?\d+)', cur)
                if m:
                    sc = m.group(1)
            positions.append((sc, mv))
            cur = None
    return positions


a = run(OURS)
b = run(ORACLE)
print(f"nostro: {len(a)} posizioni   oracolo: {len(b)} posizioni\n")

n = min(len(a), len(b))
same_mv = same_sc = 0
for i in range(n):
    (sa, ma), (sb, mb) = a[i], b[i]
    okm = ma == mb
    oks = sa == sb
    same_mv += okm
    same_sc += oks
    if not (okm and oks):
        print(f"  #{i+1:2d}  nostro {sa:>10s} {ma:6s}   oracolo {sb:>10s} {mb:6s}"
              f"   {'' if okm else 'MOSSA DIVERSA'} {'' if oks else 'punteggio diverso'}")

print(f"\nmosse uguali: {same_mv}/{n} = {100*same_mv/n:.1f}%")
print(f"punteggi uguali: {same_sc}/{n} = {100*same_sc/n:.1f}%")

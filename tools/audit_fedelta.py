#!/usr/bin/env python3
"""Audit di fedelta' ISTRUZIONE PER ISTRUZIONE fra i sorgenti dell'oracolo e questo porting.

PERCHE' ESISTE
--------------
Un audit basato sulle sole costanti numeriche (fatto il 2026-09-07) ha trovato tre pezzi mancanti,
ma ne ha lasciato passare almeno uno grave: `depth = std::min(depth, MAX_PLY - 1);`
(search.cpp:733), che non contiene nessuna costante a piu' cifre. Serve quindi un confronto a
livello di ISTRUZIONE.

E serve anche controllare l'ORDINE: lo Step 6 era presente per intero ma collocato prima dello
Step 5 invece che dopo, e leggeva quindi una `depth` non ancora aggiustata. Presenza != fedelta'.

COME FUNZIONA
-------------
Per ogni riga "di logica" della fonte (niente commenti, niente parentesi sole, niente dichiarazioni
vuote) si costruisce una FIRMA: l'insieme dei token significativi (identificatori di almeno 3
caratteri, tradotti C++ -> C# con la tabella sotto, piu' i letterali numerici). Si cerca poi nel
nostro file la riga con la maggiore sovrapposizione di firma.

Si segnalano due cose:
  * ASSENTI  — nessuna riga nostra raggiunge la soglia di somiglianza: candidata a logica mancante.
  * ORDINE   — la riga combacia ma la sua posizione da noi va all'INDIETRO rispetto alla riga
               precedente gia' appaiata: candidata a codice presente ma nel punto sbagliato.

I falsi positivi sono attesi e normali (il porting riorganizza legittimamente alcune cose): l'esito
NON e' un verdetto, e' una CHECKLIST da vagliare a mano contro il sorgente. Ogni riga vagliata va
annotata in docs/audit-fedelta.md perche' le sessioni successive non la riesaminino da capo.

USO
---
    python tools/audit_fedelta.py search.cpp
    python tools/audit_fedelta.py --tutti
"""
import io, os, re, sys

SRC = r'D:\Antcer\Documenti\ProgettiVS\stockfish-upstream-reference\src'
DST = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Corrispondenze fra file della fonte e file del porting.
MAPPA = {
    "search.cpp":   ["StockfishSharp.Engine/Search.cs", "StockfishSharp.Engine/MovePick.cs",
                     "StockfishSharp.Engine/RootMove.cs", "StockfishSharp.Engine/TimeManagement.cs"],
    "search.h":     ["StockfishSharp.Engine/Search.cs", "StockfishSharp.Engine/RootMove.cs"],
    "movepick.cpp": ["StockfishSharp.Engine/MovePicker.cs", "StockfishSharp.Engine/MovePick.cs"],
    "history.h":    ["StockfishSharp.Engine/MovePick.cs"],
    "tt.cpp":       ["StockfishSharp.Engine/TranspositionTable.cs"],
    "tt.h":         ["StockfishSharp.Engine/TranspositionTable.cs"],
    "position.cpp": ["StockfishSharp.Engine/Position.cs", "StockfishSharp.Engine/Zobrist.cs"],
    "position.h":   ["StockfishSharp.Engine/Position.cs"],
    "movegen.cpp":  ["StockfishSharp.Engine/MoveGen.cs"],
    "evaluate.cpp": ["StockfishSharp.Engine/Evaluate.cs", "StockfishSharp.Engine/Nnue/NnueEvaluate.cs"],
    "timeman.cpp":  ["StockfishSharp.Engine/TimeManagement.cs"],
    "bitboard.cpp": ["StockfishSharp.Engine/Bitboards.cs", "StockfishSharp.Engine/Attacks.cs"],
    "uci.cpp":      ["StockfishSharp.Uci/Program.cs", "StockfishSharp.Uci/UciScore.cs",
                     "StockfishSharp.Uci/OptionsMap.cs"],
}

# C++ -> C#. Solo cio' che il porting rinomina davvero; il resto combacia gia'.
RINOMINA = {
    "max_ply": "maxply", "value_draw": "valuedraw", "value_none": "none", "value_mate": "mate",
    "value_infinite": "infinite", "depth_none": "depthnone", "depth_qs": "depthqs",
    "std": "math", "abs": "abs", "clamp": "clamp", "min": "min", "max": "max",
    "ss": "ply", "pos": "pos", "pvnode": "ispvnode", "rootnode": "ply",
    "ttdata": "probe", "ttmove": "ttmove", "ttvalue": "ttscore", "is_valid": "isvalid",
    "is_decisive": "isdecisive", "is_win": "iswin", "is_loss": "isloss",
    "bound_lower": "lower", "bound_upper": "upper", "bound_exact": "exact",
    "moved_piece": "movedpiece", "capture_stage": "capturestage", "gives_check": "giveschecK",
    "non_pawn_material": "hasnonpawnmaterial", "rule50_count": "rule50count",
    "see_ge": "seege", "do_move": "domove", "undo_move": "undomove",
    "static_eval": "staticeval", "staticeval": "staticeval", "bestvalue": "value",
    "newdepth": "newdepth", "excludedmove": "excludedmove", "movecount": "movecount",
    "correctionvalue": "correctionvalue", "singularbeta": "singularbeta",
}

RUMORE = {"the", "and", "for", "int", "auto", "const", "void", "bool", "return", "else",
          "this", "true", "false", "new", "var", "public", "private", "static", "readonly"}


# Righe che non sono logica: direttive, dichiarazioni, impalcatura del linguaggio.
SCARTA = re.compile(
    r'^\s*(#|using\s|namespace\s|template\s*<|typedef\s|friend\s|extern\s|static_assert|'
    r'assert\s*\(|enum\s|struct\s|class\s|public:|private:|protected:|\}|\{|'
    r'///|//|\[|@|constexpr\s+\w+\s+\w+\s*\[|'
    r'(public|private|internal|protected|static|readonly|sealed)\s)')

# Una riga e' "logica" se contiene un'assegnazione, un ritorno, un controllo o una chiamata.
LOGICA = re.compile(r'(=[^=]|return|if|else|while|for|<<|\+\+|--|\w\s*\()')


def logic_lines(text, is_cpp, lo=0, hi=10**9):
    text = re.sub(r'/\*.*?\*/', ' ', text, flags=re.S)
    out = []
    for n, raw in enumerate(text.splitlines(), 1):
        if not (lo <= n <= hi):
            continue
        line = re.sub(r'//.*$', '', raw).strip()
        if not line or set(line) <= set("{}();,"):
            continue
        if SCARTA.match(line) or not LOGICA.search(line):
            continue
        # firme di funzione (parametri su piu' righe) e dichiarazioni nude
        if re.match(r'^[A-Za-z_][\w:<>,\s\*&]*\([^;]*$', line) and '=' not in line:
            continue
        out.append((n, line))
    return out


def firma(line):
    toks = set()
    for m in re.finditer(r'[A-Za-z_][A-Za-z_0-9]*', line):
        t = m.group(0).lower()
        t = RINOMINA.get(t, t)
        if len(t) >= 3 and t not in RUMORE:
            toks.add(t)
    for m in re.finditer(r'(?<![\w.])(\d+)(?![\w.])', line):
        toks.add("#" + m.group(1))
    return toks


# Solo le zone algoritmiche: il resto dei file e' impalcatura (I/O, opzioni, thread nativi).
INTERVALLI = {
    "search.cpp": (715, 1660),  # corpo di search(): Step 1-24. E la zona dove la fedelta conta di piu.
    "movepick.cpp": (1, 400),
    "tt.cpp": (30, 300),
    "position.cpp": (700, 1400),  # do_move / undo_move / do_null_move / set_state
}


def audit(src_name, soglia=0.60):
    sp = os.path.join(SRC, src_name)
    if not os.path.exists(sp):
        print(f"!! manca {src_name}"); return
    lo, hi = INTERVALLI.get(src_name, (0, 10**9))
    src = logic_lines(io.open(sp, encoding='utf-8', errors='ignore').read(), True, lo, hi)

    dst = []
    for rel in MAPPA.get(src_name, []):
        p = os.path.join(DST, rel.replace('/', os.sep))
        if os.path.exists(p):
            for n, l in logic_lines(io.open(p, encoding='utf-8', errors='ignore').read(), False):
                dst.append((rel, n, l, firma(l)))
    if not dst:
        print(f"!! nessun file di destinazione per {src_name}"); return

    assenti, disordine = [], []
    ultima_pos = -1
    for n, line in src:
        f = firma(line)
        if len(f) < 2:
            continue
        best, bestpos, bestrel, bestn = 0.0, -1, "", 0
        for i, (rel, dn, dl, df) in enumerate(dst):
            if not df:
                continue
            score = len(f & df) / len(f)
            if score > best:
                best, bestpos, bestrel, bestn = score, i, rel, dn
        if best < soglia:
            assenti.append((n, line, best))
        else:
            if bestpos < ultima_pos:
                disordine.append((n, line, bestrel, bestn))
            ultima_pos = max(ultima_pos, bestpos)

    print(f"\n===== {src_name}: {len(src)} righe di logica")
    print(f"  ASSENTI (sotto soglia {soglia:.0%}): {len(assenti)}")
    for n, line, sc in assenti:
        print(f"    {src_name}:{n:<5} [{sc:.0%}] {line[:100]}")
    print(f"  POSSIBILE DISORDINE: {len(disordine)}")
    for n, line, rel, dn in disordine[:40]:
        print(f"    {src_name}:{n:<5} -> {os.path.basename(rel)}:{dn:<5} {line[:80]}")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--tutti" in sys.argv:
        args = list(MAPPA.keys())
    for a in args or ["search.cpp"]:
        audit(a)

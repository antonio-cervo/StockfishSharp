// Porting fedele di "struct RootMove", search.h:135-168 — un ingresso per ogni mossa legale alla
// radice: punteggio ed eventuale linea principale (PV, in realtà solo un "rifiuto" nel caso di
// mosse che falliscono basso) associati, più le statistiche che l'iterative deepening usa per
// l'ampiezza della finestra di aspiration (averageScore/meanSquaredScore, entrambe medie mobili
// pesate per "effort" — search.cpp:1437-1468) e per la gestione del tempo (bestMoveChanges, non
// ancora consumato: la gestione tempo adattiva reale, search.cpp:568-614, resta un pezzo separato
// non ancora portato).
//
// Semplificazioni deliberate, dichiarate esplicitamente (nessuna MultiPV/Skill/Lazy SMP portata
// ancora — vedi Search.cs):
// - TbRank/TbScore: AGGIORNATO 2026-09-06 — Tablebases::rank_root_moves E' portato e opera su
//   queste RootMove (legge/scrive Pv[0]/TbRank/TbScore), insieme al filtro radice per gruppo di
//   tbRank (pvFirst/pvLast). La nota precedente diceva "restano sempre 0": non e' piu' vero.
// - InexactLower/InexactUpper/PreviousScoreExact/UciScore sono già portati fedelmente (servono
//   anche a MultiPV=1 nel caso "ricerca interrotta a metà della prima PV", search.cpp:443-489) ma
//   quel ramo (pvIdx>0 durante uno stop) non può mai attivarsi finché multiPV resta fissato a 1.
using System.Collections.Generic;

namespace StockfishSharp.Engine;

public sealed class RootMove
{
    /// <summary><c>explicit RootMove(Move m)</c>, search.h:140.</summary>
    public RootMove(Move m) => Pv.Add(m);

    public readonly List<Move> Pv = [];
    public List<Move> PreviousPv = [];

    public ulong Effort;
    public int Score = -Values.Infinite;
    public int PreviousScore = -Values.Infinite;
    public int AverageScore = -Values.Infinite;
    public long MeanSquaredScore = -(long)Values.Infinite * Values.Infinite;
    public int UciScore = -Values.Infinite;
    public bool InexactLower;
    public bool InexactUpper;
    public bool PreviousScoreExact;
    public int SelDepth;
    public int TbRank;
    public int TbScore;

    /// <summary><c>is_inexact()</c>, search.h:142.</summary>
    public bool IsInexact => InexactLower || InexactUpper;

    /// <summary><c>is_exact_loss()</c>, search.h:143-145.</summary>
    public bool IsExactLoss => Score != -Values.Infinite && Values.IsLoss(Score) && !IsInexact;

    /// <summary><c>unset_inexact()</c>, search.h:146.</summary>
    public void UnsetInexact() => InexactLower = InexactUpper = false;

    /// <summary><c>operator==(const Move&amp;)</c>, search.h:147.</summary>
    public bool Matches(Move m) => Pv.Count > 0 && Pv[0] == m;

    /// <summary><c>operator&lt;</c>, search.h:149-151 — "Sort in descending order": usato con
    /// OrderByDescending/ThenByDescending (LINQ, stabile per specifica) al posto di
    /// std::stable_sort, così le mosse a pari punteggio mantengono l'ordine relativo che avevano
    /// nella lista.</summary>
    public static IEnumerable<RootMove> SortDescending(IEnumerable<RootMove> moves)
    {
        return System.Linq.Enumerable.ThenByDescending(
            System.Linq.Enumerable.OrderByDescending(moves, rm => rm.Score),
            rm => rm.PreviousScore);
    }

    /// <summary><c>std::find(rootMoves.begin(), rootMoves.end(), move)</c> — cerca l'ingresso la
    /// cui pv[0] è <paramref name="m"/> (ogni mossa legale alla radice compare esattamente una
    /// volta nella lista).</summary>
    public static RootMove Find(List<RootMove> moves, Move m)
    {
        foreach (var rm in moves)
            if (rm.Matches(m))
                return rm;

        throw new System.InvalidOperationException($"RootMove non trovata per la mossa {m}");
    }
}

// Corrisponde a win_rate_params/to_cp in src/uci.cpp. Piccolo pezzo di Flow A ("debito", vedi
// docs/porting-master-plan.md) portato in anticipo: serve a confrontare i valori interni delle
// fasi N3+ con la tabella in "pedoni" stampata dal comando UCI "eval" dell'oracolo, che passa
// sempre per questa normalizzazione dipendente dal materiale (non una divisione fissa per 100).

namespace StockfishSharp.Engine;

public static class WinRateModel
{
    /// <summary><c>win_rate_params</c>, uci.cpp:537-553. Il modello è tarato su conteggi di
    /// materiale in [17,78] e ancorato a 58 — <c>m</c> è la posizione clampata su questa scala.</summary>
    public static (double A, double B) Params(Position pos)
    {
        int material = pos.Count(PieceType.Pawn) + 3 * pos.Count(PieceType.Knight) + 3 * pos.Count(PieceType.Bishop)
            + 5 * pos.Count(PieceType.Rook) + 9 * pos.Count(PieceType.Queen);

        double m = Math.Clamp(material, 17, 78) / 58.0;

        double[] a = [-142.72052667, 372.35176398, -340.71073572, 415.23490212];
        double[] b = [5.93832785, 15.61267078, -30.57816876, 69.63866711];

        double pa = (((a[0] * m + a[1]) * m + a[2]) * m) + a[3];
        double pb = (((b[0] * m + b[1]) * m + b[2]) * m) + b[3];

        return (pa, pb);
    }

    /// <summary><c>UCIEngine::to_cp</c>, uci.cpp:583-594 — valore interno -> centipedoni, senza
    /// trattamento di scacco matto/tablebase (non serve qui, solo verifica NNUE).</summary>
    public static int ToCentipawns(int value, Position pos)
    {
        var (a, _) = Params(pos);
        return (int)Math.Round(100 * value / a);
    }
}

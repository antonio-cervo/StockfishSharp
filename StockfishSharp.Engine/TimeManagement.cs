// Corrisponde a src/timeman.h + src/timeman.cpp della fonte upstream. Vedi Types.cs per la nota
// generale sul porting.
//
// Semplificazioni deliberate: "nodes as time" (nodestime/useNodesTime) non portato — nessuno lo
// usa in pratica per questo motore, e richiederebbe threading il conteggio nodi nella ricerca
// come surrogato del tempo; scaleFactor resta sempre 1. "Ponder" è portato come parametro booleano
// semplice, non un'opzione UCI vera (Flow A4, ucioption.cpp, non ancora portato).
// "originalTimeAdjust" nella fonte vive nel SearchManager (calcolato una volta per PARTITA, non
// per mossa) e viene passato per riferimento a init(); qui è un campo di questa stessa classe con
// NewGame() a resettarlo — stesso effetto, incapsulamento diverso per non dover portare
// SearchManager per intero.

namespace StockfishSharp.Engine;

public sealed class TimeManagement
{
    private const long NoBound = long.MaxValue / 2;

    public long OptimumTime { get; private set; } = NoBound;
    public long MaximumTime { get; private set; } = NoBound;

    private double _originalTimeAdjust = -1;

    public void NewGame() => _originalTimeAdjust = -1;

    /// <summary><c>TimeManagement::init</c>, timeman.cpp:46-142 — calcola quanto tempo dedicare
    /// alla mossa corrente. <paramref name="myTime"/>/<paramref name="myInc"/> in millisecondi,
    /// <paramref name="movesToGo"/> 0 se non specificato dalla GUI (modalità "x basetime + z
    /// incremento" invece di "x mosse in y secondi").</summary>
    public void Init(long myTime, long myInc, int movesToGo, int ply, long moveOverhead = 10, bool ponder = false)
    {
        if (myTime <= 0)
        {
            OptimumTime = MaximumTime = NoBound;
            return;
        }

        long scaledTime = Math.Max(1, myTime);

        // Massimo orizzonte di mosse da qui alla fine (della partita o del prossimo controllo).
        int mtg = movesToGo > 0 ? Math.Min(movesToGo, 50) : 50;

        // Se resta meno di un secondo, riduce gradualmente l'orizzonte.
        if (scaledTime < 1000 && movesToGo == 0)
            mtg = (int)(scaledTime * 0.05);

        long timeLeft = Math.Max(1, myTime + (myInc * (mtg - 1)) - (moveOverhead * (2 + mtg)));

        double optScale, maxScale;

        // x basetime (+ z incremento)
        if (movesToGo == 0)
        {
            if (_originalTimeAdjust < 0)
                _originalTimeAdjust = (0.3272 * Math.Log10(timeLeft)) - 0.4141;

            double logTimeInSec = Math.Log10(scaledTime / 1000.0);
            double optConstant = Math.Min(0.0029869 + (0.00033554 * logTimeInSec), 0.004905);
            double maxConstant = Math.Max(3.3744 + (3.0608 * logTimeInSec), 3.1441);

            optScale = Math.Min(0.012112 + (Math.Pow(ply + 3.22713, 0.46866) * optConstant),
                0.19404 * myTime / timeLeft) * _originalTimeAdjust;

            maxScale = Math.Min(6.873, maxConstant + (ply / 12.352));
        }
        // x mosse in y secondi (+ z incremento)
        else
        {
            optScale = Math.Min((0.88 + (ply / 116.4)) / mtg, 0.88 * myTime / timeLeft);
            maxScale = 1.3 + (0.11 * mtg);
        }

        OptimumTime = (long)Math.Max(1.0, optScale * timeLeft);
        MaximumTime = (long)Math.Max(OptimumTime, Math.Min((0.8097 * myTime) - moveOverhead, maxScale * OptimumTime));

        if (ponder)
            OptimumTime += OptimumTime / 4;
    }
}

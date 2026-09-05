// Utilità di conteggio nodi per verificare la correttezza di Position/MoveGen — non corrisponde a
// un singolo file della fonte (Stockfish ha perft dentro benchmark.cpp, qui è isolato perché serve
// da SOLO strumento di verifica per questa fase del porting, non da comando UCI/benchmark vero).

namespace StockfishSharp.Engine;

public static class Perft
{
    /// <summary>Conta tutti i nodi foglia a profondità <paramref name="depth"/> dalla posizione
    /// data, generando le mosse LEGALI a ogni nodo (non pseudo-legali + verifica dopo: più
    /// semplice da verificare, anche se un filo più lento — la stessa scelta della modalità
    /// "perft" più semplice di Stockfish, non quella ottimizzata con bulk-counting all'ultimo
    /// livello).</summary>
    public static long Run(Position pos, int depth)
    {
        if (depth == 0) return 1;

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        if (depth == 1) return moves.Count;

        long nodes = 0;
        foreach (var m in moves)
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            nodes += Run(pos, depth - 1);
            pos.UndoMove(m);
        }

        return nodes;
    }
}

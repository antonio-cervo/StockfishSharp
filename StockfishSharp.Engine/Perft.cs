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
    public static long Run(Position pos, int depth) => Run(pos, depth, root: false, null);

    /// <summary><c>perft&lt;Root&gt;</c>, perft.h:35-57 — con <paramref name="root"/> stampa la
    /// ripartizione per mossa ("divide"), come fa la fonte al livello radice, e usa il conteggio
    /// in blocco all'ultimo livello (<c>leaf = (depth == 2)</c>). Serve anche a confrontare
    /// l'ORDINE DI GENERAZIONE delle mosse con quello della fonte: l'ordine rompe tutti i pareggi
    /// dell'ordinamento a valle, quindi una differenza qui si propaga a tutta la ricerca.</summary>
    public static long Run(Position pos, int depth, bool root, Func<Move, string>? formatMove)
    {
        if (depth == 0) return 1;

        // Buffer per livello, allocati UNA volta qui e riusati da tutta la ricorsione. Non è una
        // ottimizzazione fine a se stessa: il perft è anche il metro con cui si misurano i byte
        // allocati per nodo del motore, e con un "new List<Move>() + new StateInfo()" per nodo
        // misurava soprattutto se stesso (81,1 byte/nodo, tutti suoi).
        var listPool = new List<Move>[depth + 1];
        var statePool = new StateInfo[depth + 1];
        for (int i = 0; i <= depth; i++)
        {
            listPool[i] = new List<Move>(Ply.MaxMoves);
            statePool[i] = new StateInfo();
        }

        return Run(pos, depth, root, formatMove, listPool, statePool);
    }

    private static long Run(Position pos, int depth, bool root, Func<Move, string>? formatMove,
        List<Move>[] listPool, StateInfo[] statePool)
    {
        if (depth == 0) return 1;

        var moves = listPool[depth];
        moves.Clear();
        MoveGen.Generate(GenType.Legal, pos, moves);

        bool leaf = depth == 2;
        long nodes = 0;

        foreach (var m in moves)
        {
            long cnt;
            if (root && depth <= 1)
            {
                cnt = 1;
                nodes++;
            }
            else
            {
                pos.DoMove(m, statePool[depth]);
                if (leaf)
                {
                    // depth==2 qui, quindi listPool[1] non è mai in uso: la ricorsione si ferma prima.
                    var leafMoves = listPool[depth - 1];
                    leafMoves.Clear();
                    MoveGen.Generate(GenType.Legal, pos, leafMoves);
                    cnt = leafMoves.Count;
                }
                else
                {
                    cnt = Run(pos, depth - 1, root: false, null, listPool, statePool);
                }
                nodes += cnt;
                pos.UndoMove(m);
            }

            if (root && formatMove != null)
                Console.WriteLine($"{formatMove(m)}: {cnt}");
        }

        return nodes;
    }
}

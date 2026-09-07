// Porting di src/uci.cpp:530-605 (win_rate_params / win_rate_model / to_cp / format_score) e di
// src/score.cpp (la classificazione di un Value in "matto", "tablebase" o "unita' interne").
//
// PORTATO IL 2026-09-07. Fino a quel momento il layer UCI di questo porting stampava sempre
// "score cp <valore interno grezzo>", con DUE divergenze reali dalla fonte:
//
//  1. NESSUNA normalizzazione. Da Stockfish 16 il punteggio mostrato non e' il valore interno del
//     motore: viene diviso per il parametro "a" del modello WDL (github.com/official-stockfish/
//     WDL_model), calibrato in modo che "+1.00" significhi davvero "un pedone di vantaggio" in
//     termini di probabilita' di vittoria. Il fattore vale circa 3,0-3,4 e DIPENDE DAL MATERIALE
//     sulla scacchiera. Senza, ogni valutazione che pubblichiamo (GUI, lichess, log del bot) e'
//     gonfiata di circa 3,3 volte — misurato su 40 delle 49 posizioni di bench, dove il rapporto
//     fra il nostro punteggio e quello dell'oracolo era costantemente ~3x.
//
//  2. Nessun "score mate N": i punteggi di matto uscivano come centipawn enormi, quindi nessuna
//     GUI poteva mostrare l'annuncio di matto. Idem per i punteggi da tablebase, che nella fonte
//     hanno una loro codifica convenzionale (+/-20000 meno la distanza).

using StockfishSharp.Engine;

namespace StockfishSharp.Uci;

internal static class UciScore
{
    /// <summary><c>WinRateParams</c> + <c>win_rate_params</c>, uci.cpp:532-553. I due polinomi
    /// sono i coefficienti fittati sulle statistiche fishtest a cadenza lunga.</summary>
    private static (double A, double B) WinRateParams(Position pos)
    {
        int material = pos.Count(PieceType.Pawn)
                     + (3 * pos.Count(PieceType.Knight))
                     + (3 * pos.Count(PieceType.Bishop))
                     + (5 * pos.Count(PieceType.Rook))
                     + (9 * pos.Count(PieceType.Queen));

        // Il modello e' fittato solo su conteggi di materiale in [17, 78], ancorato a 58.
        double m = Math.Clamp(material, 17, 78) / 58.0;

        ReadOnlySpan<double> aS = [-142.72052667, 372.35176398, -340.71073572, 415.23490212];
        ReadOnlySpan<double> bS = [5.93832785, 15.61267078, -30.57816876, 69.63866711];

        double a = (((aS[0] * m) + aS[1]) * m + aS[2]) * m + aS[3];
        double b = (((bS[0] * m) + bS[1]) * m + bS[2]) * m + bS[3];
        return (a, b);
    }

    /// <summary><c>win_rate_model</c>, uci.cpp:557-563 — probabilita' di vittoria in millesimi.
    /// Serve all'output WDL.</summary>
    public static int WinRateModel(int v, Position pos)
    {
        var (a, b) = WinRateParams(pos);
        return (int)(0.5 + (1000 / (1 + Math.Exp((a - v) / b))));
    }

    /// <summary><c>UCIEngine::to_cp</c>, uci.cpp:585-594 — converte un valore interno in centipawn
    /// "normalizzati", senza trattare matti e punteggi speciali (se ne occupa <see
    /// cref="Format"/>).</summary>
    public static int ToCp(int v, Position pos)
    {
        var (a, _) = WinRateParams(pos);
        return (int)Math.Round(100 * (double)v / a);
    }

    /// <summary><c>Score::Score</c> (score.cpp:29-46) + <c>UCIEngine::format_score</c>
    /// (uci.cpp:566-581) fusi in un'unica funzione: restituisce direttamente il campo "score ..."
    /// della riga info, cioe' "mate N", oppure "cp ±20000-distanza" per un punteggio di tablebase,
    /// oppure "cp <normalizzato>".</summary>
    public static string Format(int v, Position pos)
    {
        const int TbCp = 20000;

        if (!Values.IsDecisive(v))
            return "cp " + ToCp(v, pos);

        if (Math.Abs(v) <= Values.Tb)
        {
            int plies = Values.Tb - Math.Abs(v);
            return "cp " + ((v > 0 ? TbCp : -TbCp) - plies);
        }

        // Matto: la fonte converte la distanza in ply in un numero di MOSSE, arrotondando per
        // eccesso quando siamo noi a dare matto (uci.cpp:570).
        int matePlies = Values.Mate - Math.Abs(v);
        if (v < 0) matePlies = -matePlies;
        return "mate " + ((matePlies > 0 ? matePlies + 1 : matePlies) / 2);
    }
}

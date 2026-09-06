using StockfishSharp.Engine;
using File = StockfishSharp.Engine.File;

// Punto di ingresso UCI. Non un porting di src/uci.cpp+ucioption.cpp (~1500 righe insieme, con
// tutta l'infrastruttura di opzioni generiche, benchmark, comandi di debug) — un layer minimo ma
// funzionante che copre i comandi che servono a giocare davvero una partita: uci/isready/
// ucinewgame/position/go/setoption Hash/quit. Il porting fedele di uci.cpp resta un obiettivo
// per la Fase 4 del piano.

Attacks.EnsureInitialized();
Position.Init();

// Percorso della rete di default, non ancora configurabile via UCI (l'opzione "EvalFile" fa
// parte di ucioption.cpp, non ancora portato — Flow A "debito" in docs/porting-master-plan.md).
// Se il file non c'è (~100MB, gitignored — vedi docs/nnue-porting-plan.md per l'URL), si continua
// con il placeholder materiale+PSQT invece di rifiutarsi di avviarsi come fa la fonte reale: qui
// serve poter lavorare anche senza il file scaricato.
const string DefaultNetworkPath = @"D:\Antcer\Documenti\ProgettiVS\StockfishSharp\nnue-networks\nn-1a298aa575a0.nnue";
if (System.IO.File.Exists(DefaultNetworkPath))
{
    Evaluate.NnueNetwork = StockfishSharp.Engine.Nnue.NnueNetwork.Load(DefaultNetworkPath);
    Console.WriteLine($"info string NNUE evaluation using {System.IO.Path.GetFileName(DefaultNetworkPath)}");
}
else
{
    Console.WriteLine("info string NNUE network not found, using placeholder material+PSQT evaluation");
}

var position = new Position();
position.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);

var search = new Search();
search.Resize(16);
search.NewGame();
var timeManagement = new TimeManagement();
int maxDepth = 30;

while (Console.ReadLine() is { } line)
{
    var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length == 0) continue;

    switch (tokens[0])
    {
        case "uci":
            Console.WriteLine("id name StockfishSharp (porting in corso)");
            Console.WriteLine("id author Antonio Cervo, porting da Stockfish (GPLv3)");
            Console.WriteLine("option name Hash type spin default 16 min 1 max 4096");
            Console.WriteLine("uciok");
            break;

        case "isready":
            Console.WriteLine("readyok");
            break;

        case "ucinewgame":
            search.NewGame();
            timeManagement.NewGame();
            break;

        case "setoption":
            HandleSetOption(tokens);
            break;

        case "position":
            HandlePosition(tokens);
            break;

        case "go":
            HandleGo(tokens);
            break;

        case "quit":
            return;
    }
}

void HandleSetOption(string[] toks)
{
    int nameIdx = Array.IndexOf(toks, "name");
    int valueIdx = Array.IndexOf(toks, "value");
    if (nameIdx < 0 || valueIdx < 0 || valueIdx <= nameIdx) return;

    string name = string.Join(' ', toks[(nameIdx + 1)..valueIdx]);
    string value = string.Join(' ', toks[(valueIdx + 1)..]);

    if (string.Equals(name, "Hash", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int mb))
        search.Resize(mb);
}

void HandlePosition(string[] toks)
{
    int idx = 1;
    if (idx >= toks.Length) return;

    if (toks[idx] == "startpos")
    {
        position.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);
        idx++;
    }
    else if (toks[idx] == "fen")
    {
        idx++;
        int fenStart = idx;
        while (idx < toks.Length && toks[idx] != "moves") idx++;
        string fen = string.Join(' ', toks[fenStart..idx]);
        position.Set(fen, isChess960: false);
    }
    else
    {
        return;
    }

    if (idx < toks.Length && toks[idx] == "moves")
    {
        idx++;
        for (; idx < toks.Length; idx++)
        {
            Move? m = ParseUciMove(toks[idx]);
            if (m == null) continue;
            var st = new StateInfo();
            position.DoMove(m.Value, st);
        }
    }
}

Move? ParseUciMove(string uci)
{
    if (uci.Length < 4) return null;

    Square from = ParseSquare(uci[..2]);
    Square to = ParseSquare(uci.Substring(2, 2));
    if (from == Square.None || to == Square.None) return null;

    var legalMoves = new List<Move>();
    MoveGen.Generate(GenType.Legal, position, legalMoves);

    foreach (var m in legalMoves)
    {
        if (m.FromSq != from || m.ToSq != to) continue;
        if (uci.Length == 5 && m.TypeOf == MoveType.Promotion)
        {
            PieceType promo = char.ToLowerInvariant(uci[4]) switch
            {
                'q' => PieceType.Queen,
                'r' => PieceType.Rook,
                'b' => PieceType.Bishop,
                'n' => PieceType.Knight,
                _ => PieceType.None,
            };
            if (m.PromotionType != promo) continue;
        }

        return m;
    }

    return null;
}

Square ParseSquare(string s)
{
    if (s.Length != 2) return Square.None;
    int file = s[0] - 'a';
    int rank = s[1] - '1';
    if (file is < 0 or > 7 || rank is < 0 or > 7) return Square.None;
    return Types.MakeSquare((File)file, (Rank)rank);
}

void HandleGo(string[] toks)
{
    long? GetLong(string key)
    {
        int i = Array.IndexOf(toks, key);
        return i >= 0 && i + 1 < toks.Length && long.TryParse(toks[i + 1], out long v) ? v : null;
    }

    var movetime = GetLong("movetime");
    var depthArg = GetLong("depth");

    TimeSpan budget;
    if (movetime.HasValue)
    {
        budget = TimeSpan.FromMilliseconds(Math.Max(50, movetime.Value - 50));
    }
    else
    {
        Color us = position.SideToMove;
        long? myTime = us == Color.White ? GetLong("wtime") : GetLong("btime");
        long myInc = (us == Color.White ? GetLong("winc") : GetLong("binc")) ?? 0;
        int movesToGo = (int)(GetLong("movestogo") ?? 0);

        if (myTime.HasValue)
        {
            timeManagement.Init(myTime.Value, myInc, movesToGo, position.GamePly);
            budget = TimeSpan.FromMilliseconds(timeManagement.OptimumTime);
        }
        else
        {
            budget = TimeSpan.FromSeconds(10); // "go depth N"/"go infinite" senza orologio: budget fisso ragionevole
        }
    }

    int depth = depthArg.HasValue ? (int)Math.Min(depthArg.Value, maxDepth) : maxDepth;

    var result = search.Search_(position, depth, budget);

    if (result.BestMove.HasValue)
    {
        Console.WriteLine($"info depth {result.Depth} score cp {result.ScoreCp} nodes {result.Nodes}");
        Console.WriteLine($"bestmove {MoveToUci(result.BestMove.Value)}");
    }
    else
    {
        Console.WriteLine("bestmove 0000");
    }
}

string MoveToUci(Move m)
{
    string s = SquareToString(m.FromSq) + SquareToString(m.ToSq);
    if (m.TypeOf == MoveType.Promotion)
    {
        s += m.PromotionType switch
        {
            PieceType.Queen => "q",
            PieceType.Rook => "r",
            PieceType.Bishop => "b",
            PieceType.Knight => "n",
            _ => "",
        };
    }

    return s;
}

string SquareToString(Square s) => $"{(char)('a' + (byte)Types.FileOf(s))}{(char)('1' + (byte)Types.RankOf(s))}";

using StockfishSharp.Engine;

// Punto di ingresso UCI — al momento solo uno stub che inizializza le tabelle di attacco e
// risponde "uciok"/"readyok". La vera gestione del protocollo UCI (posizione, go, opzioni) arriva
// in una fase successiva del porting (vedi docs/porting-plan.md), quando ci sarà una Position su
// cui operare.

Attacks.EnsureInitialized();

while (Console.ReadLine() is { } line)
{
    var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length == 0) continue;

    switch (tokens[0])
    {
        case "uci":
            Console.WriteLine("id name StockfishSharp (porting in corso)");
            Console.WriteLine("id author Antonio Cervo, porting da Stockfish (GPLv3)");
            Console.WriteLine("uciok");
            break;
        case "isready":
            Console.WriteLine("readyok");
            break;
        case "quit":
            return;
    }
}

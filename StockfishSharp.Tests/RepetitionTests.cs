using StockfishSharp.Engine;
using Xunit;
using File = StockfishSharp.Engine.File;

namespace StockfishSharp.Tests;

public class RepetitionTests
{
    public RepetitionTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Move Uci(string s) =>
        new(Types.MakeSquare((File)(s[0] - 'a'), (Rank)(s[1] - '1')),
            Types.MakeSquare((File)(s[2] - 'a'), (Rank)(s[3] - '1')));

    [Fact]
    public void DetectsThreefoldRepetitionByKnightShuffle()
    {
        // Cavalli avanti e indietro: la posizione iniziale si ripete dopo Ng1-f3 Ng8-f6 Nf3-g1
        // Nf6-g8 (2a occorrenza) e di nuovo dopo lo stesso ciclo (3a occorrenza) — position.cpp
        // conta le occorrenze allo STESSO lato al tratto, quindi ogni ciclo di 4 mezze mosse.
        var pos = new Position();
        pos.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);

        string[] cycle = ["g1f3", "g8f6", "f3g1", "f6g8"];
        var states = new List<StateInfo>();

        // Dopo un ciclo completo (4 mezze mosse) la posizione iniziale è la 2a occorrenza.
        foreach (var uci in cycle)
        {
            var st = new StateInfo();
            states.Add(st);
            pos.DoMove(Uci(uci), st);
        }

        Assert.False(pos.IsDraw(ply: 1)); // solo la 2a occorrenza, non ancora patta

        // Un secondo ciclo completo porta alla 3a occorrenza — patta per ripetizione.
        foreach (var uci in cycle)
        {
            var st = new StateInfo();
            states.Add(st);
            pos.DoMove(Uci(uci), st);
        }

        Assert.True(pos.IsDraw(ply: 1));
    }

    [Fact]
    public void UpcomingRepetitionPredictsTheDrawOneMoveEarly()
    {
        var pos = new Position();
        pos.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);

        // Dopo queste 7 mezze mosse: bianco è di nuovo in g1 (a casa), nero è in f6 (fuori). La
        // posizione dopo la 4a mezza mossa (f6g8) era già la 2a occorrenza della posizione
        // iniziale — se il nero gioca ORA Nf6-g8, si torna esattamente a quella posizione per la
        // 3a volta: una patta imminente rilevabile PRIMA di giocare la mossa.
        string[] setup = ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1"];
        foreach (var uci in setup)
        {
            var st = new StateInfo();
            pos.DoMove(Uci(uci), st);
        }

        Assert.True(pos.UpcomingRepetition(ply: 1));
    }

    [Fact]
    public void UpcomingRepetitionMatchesIsDrawOverAllLegalMoves()
    {
        // Stesso invariante usato dalla fonte per verificare upcoming_repetition: deve combaciare
        // ESATTAMENTE con "esiste una mossa legale dopo la quale IsDraw diventa vera" — qui
        // verificato per esaustione su tutte le mosse legali di una posizione con una patta
        // imminente reale.
        var pos = new Position();
        pos.Set("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", isChess960: false);

        string[] setup = ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1"];
        foreach (var uci in setup)
        {
            var st = new StateInfo();
            pos.DoMove(Uci(uci), st);
        }

        const int ply = 1;
        bool upcoming = pos.UpcomingRepetition(ply);

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        bool anyMoveDraws = false;
        foreach (var m in moves)
        {
            var st = new StateInfo();
            pos.DoMove(m, st);
            if (pos.IsDraw(ply + 1)) anyMoveDraws = true;
            pos.UndoMove(m);
            if (anyMoveDraws) break;
        }

        Assert.True(upcoming, "Attesa una patta imminente vera (f6g8 del nero la realizzerebbe).");
        Assert.Equal(anyMoveDraws, upcoming);
    }
}

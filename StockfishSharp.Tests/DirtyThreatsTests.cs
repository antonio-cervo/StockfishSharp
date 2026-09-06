using StockfishSharp.Engine;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica indipendente di Position.UpdatePieceThreats (porting di
/// Position::update_piece_threats, position.cpp:1193-1291 — la feature NNUE FullThreats) — stesso
/// principio già usato per SEE/perft/l'accumulatore NNUE in questo progetto: un'implementazione
/// "brute force" strutturalmente indipendente (nessun raggio/scoperto, solo verifica diretta
/// pezzo-per-pezzo con gli attacchi standard) calcola l'insieme COMPLETO delle relazioni di
/// minaccia prima e dopo una mossa; applicando il diff dei DirtyThreat generati durante la mossa
/// all'insieme "prima" si deve ottenere esattamente l'insieme "dopo" ricalcolato da zero.
///
/// La regola dichiarativa "chi può minacciare chi" (CanThreaten sotto) è la STESSA specifica della
/// feature che UpdatePieceThreats implementa in modo ottimizzato — non è un dettaglio arbitrario
/// di questo test, quindi trascriverla una seconda volta qui non è ridondante: verifica che
/// l'ottimizzazione a raggi/scoperti produca lo stesso risultato della definizione diretta.</summary>
public class DirtyThreatsTests
{
    public DirtyThreatsTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();
    }

    private static Position MakePosition(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    /// <summary>position.cpp:1189-1191 (can_slider_threat) generalizzata all'insieme completo
    /// delle regole di soglia della feature (threatTargets per ogni tipo attaccante,
    /// position.cpp:1239-1241): i re non minacciano né sono mai minacciati; un pedone minaccia
    /// solo cavallo/torre; alfiere/torre minacciano solo pedone/cavallo/alfiere/torre (mai una
    /// donna); cavallo/donna minacciano qualunque cosa tranne un re.</summary>
    private static bool CanThreaten(PieceType attacker, PieceType target)
    {
        if (attacker == PieceType.King || target == PieceType.King) return false;
        if (attacker == PieceType.Pawn) return target is PieceType.Knight or PieceType.Rook;
        if (attacker is PieceType.Bishop or PieceType.Rook)
            return target is PieceType.Pawn or PieceType.Knight or PieceType.Bishop or PieceType.Rook;
        return true; // Knight o Queen: qualunque cosa tranne un re, già escluso sopra
    }

    private readonly record struct ThreatRelation(Piece Attacker, Piece Target, Square AttackerSq, Square TargetSq);

    private static HashSet<ThreatRelation> ComputeAllThreats(Position pos)
    {
        var result = new HashSet<ThreatRelation>();
        ulong occupied = pos.Pieces();

        for (Square s1 = Square.A1; s1 <= Square.H8; s1++)
        {
            Piece attacker = pos.PieceOn(s1);
            if (attacker == Piece.None) continue;

            PieceType apt = Types.TypeOf(attacker);
            if (apt == PieceType.King) continue; // i re non generano mai minacce dirette

            ulong attacks = apt == PieceType.Pawn
                ? Attacks.PawnAttacksBb(s1, Types.ColorOf(attacker))
                : Attacks.AttacksBb(apt, s1, occupied);

            ulong targets = attacks & occupied;
            while (targets != 0)
            {
                Square s2 = Bitboards.PopLsb(ref targets);
                Piece target = pos.PieceOn(s2);
                PieceType tpt = Types.TypeOf(target);
                if (!CanThreaten(apt, tpt)) continue;

                result.Add(new ThreatRelation(attacker, target, s1, s2));
            }
        }

        return result;
    }

    private static void ApplyDirtyThreats(HashSet<ThreatRelation> threats, List<DirtyThreat> dts)
    {
        foreach (var dt in dts)
        {
            var rel = new ThreatRelation(dt.Pc, dt.ThreatenedPc, dt.PcSq, dt.ThreatenedSq);
            if (dt.Add) threats.Add(rel);
            else threats.Remove(rel);
        }
    }

    /// <summary>Per ogni mossa legale da ciascuna posizione: calcola le minacce PRIMA (brute
    /// force), gioca la mossa raccogliendo i DirtyThreat, calcola le minacce DOPO (brute force di
    /// nuovo, sulla nuova posizione) e verifica che applicare il diff dei DirtyThreat a PRIMA dia
    /// esattamente DOPO.</summary>
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")]
    [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8")]
    [InlineData("2rr3k/pp3pp1/1nnqbN1p/3p4/2pP4/2P3Q1/PPB4P/R3R1K1 b - - 0 1")]
    public void DirtyThreatsMatchBruteForceRecomputation(string fen)
    {
        var pos = MakePosition(fen);
        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        Assert.NotEmpty(moves);

        foreach (var m in moves)
        {
            var before = ComputeAllThreats(pos);

            var dts = new List<DirtyThreat>();
            var st = new StateInfo();
            bool givesCheck = pos.GivesCheck(m);
            pos.DoMove(m, st, givesCheck, dts);

            var after = ComputeAllThreats(pos);

            var predicted = new HashSet<ThreatRelation>(before);
            ApplyDirtyThreats(predicted, dts);

            Assert.True(predicted.SetEquals(after),
                $"FEN={fen}, mossa={m.FromSq}{m.ToSq}: mismatch fra DirtyThreat e ricalcolo brute-force. " +
                $"Mancanti in predicted: {string.Join(", ", after.Except(predicted))}. " +
                $"In eccesso in predicted: {string.Join(", ", predicted.Except(after))}.");

            pos.UndoMove(m);
        }
    }

    /// <summary>Come sopra ma ricorsiva fino a profondità 2 (in stile perft) — verifica ANCHE i
    /// nodi raggiunti dopo la prima mossa, dove possono comparire configurazioni non presenti alla
    /// radice (scoperte doppie, un secondo arrocco, una cattura subito dopo una promozione).</summary>
    [Theory]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 2)]
    public void DirtyThreatsMatchBruteForceRecomputationRecursive(string fen, int depth)
    {
        var pos = MakePosition(fen);
        Verify(pos, depth);
    }

    private static void Verify(Position pos, int depth)
    {
        if (depth == 0) return;

        var moves = new List<Move>();
        MoveGen.Generate(GenType.Legal, pos, moves);

        foreach (var m in moves)
        {
            var before = ComputeAllThreats(pos);

            var dts = new List<DirtyThreat>();
            var st = new StateInfo();
            bool givesCheck = pos.GivesCheck(m);
            pos.DoMove(m, st, givesCheck, dts);

            var after = ComputeAllThreats(pos);
            var predicted = new HashSet<ThreatRelation>(before);
            ApplyDirtyThreats(predicted, dts);

            Assert.True(predicted.SetEquals(after),
                $"mossa={m.FromSq}{m.ToSq} a profondità residua {depth}: mismatch fra DirtyThreat e ricalcolo brute-force. " +
                $"Mancanti in predicted: {string.Join(", ", after.Except(predicted))}. " +
                $"In eccesso in predicted: {string.Join(", ", predicted.Except(after))}.");

            Verify(pos, depth - 1);

            pos.UndoMove(m);
        }
    }
}

using StockfishSharp.Engine;
using StockfishSharp.Engine.Tablebases;
using Xunit;

namespace StockfishSharp.Tests;

/// <summary>Verifica il porting di Syzygy (C2, tbprobe.cpp) contro valori noti indipendentemente
/// (teoria scacchistica elementare + l'oracolo Stockfish reale con la stessa SyzygyPath, più
/// python-chess come secondo oracolo indipendente usato per isolare un bug — vedi
/// docs/syzygy-porting-plan.md). Richiede le tabelle a 5 pezzi già scaricate in
/// ../ACMyChess/Syzygy/ (sorella di questo repo) — se assenti i test falliscono con un messaggio
/// chiaro invece di dare un falso "superato" silenzioso.</summary>
public class TablebaseTests
{
    private static readonly string SyzygyPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ACMyChess", "Syzygy"));

    public TablebaseTests()
    {
        Attacks.EnsureInitialized();
        Position.Init();

        Assert.True(Directory.Exists(SyzygyPath) && Directory.GetFiles(SyzygyPath, "*.rtbw").Length > 0,
            $"Tabelle Syzygy non trovate in {SyzygyPath} — vedi [[acmychess-tablebase-plan]] in memoria per come ottenerle.");

        Tablebase.Init(SyzygyPath);
    }

    private static Position MakePosition(string fen)
    {
        var pos = new Position();
        pos.Set(fen, isChess960: false);
        return pos;
    }

    [Fact]
    public void KQvKWhiteToMoveIsWin()
    {
        var pos = MakePosition("4k3/8/4K3/8/8/8/8/4Q3 w - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Win, wdl);
    }

    [Fact]
    public void KQvKBlackToMoveIsLoss()
    {
        var pos = MakePosition("4k3/8/4K3/8/8/8/8/4Q3 b - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Loss, wdl);
    }

    [Fact]
    public void KvKIsAlwaysDraw()
    {
        var pos = MakePosition("8/8/8/8/8/4k3/8/4K3 w - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Draw, wdl);
    }

    /// <summary>KPvK con pedone pronto a promuovere, nero di turno — esercita il ramo
    /// <c>hasPawns</c> di <c>do_probe_table</c> (leadPawns, LeadPawnIdx, tbFile per colonna),
    /// il più delicato insieme al ramo senza pedoni. Confermato con l'oracolo reale: mossa
    /// e2e1q, punteggio cp 20000 (vittoria) dal punto di vista del nero.</summary>
    [Fact]
    public void KPvKPromotingPawnIsWin()
    {
        var pos = MakePosition("8/2K5/8/8/8/4k3/4p3/8 b - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Win, wdl);
    }

    /// <summary><c>KRvK</c> — esercita un materiale diverso da KQvK (e la sua tabella DTZ
    /// specifica) senza pedoni. Confermato con l'oracolo reale: mate in 1 (Ra1-a8#).</summary>
    [Fact]
    public void KRvKMateIn1()
    {
        var pos = MakePosition("6k1/8/6K1/8/8/8/8/R7 w - - 0 1");
        int dtz = Tablebase.ProbeDtz(pos, out var result);

        Assert.NotEqual(ProbeState.Fail, result);
        Assert.Equal(1, dtz);
    }

    /// <summary><c>KNNvK</c> — due pezzi duplicati dello stesso tipo/colore (non
    /// <c>hasUniquePieces</c>): esercita il ramo di indicizzazione via <c>MapKK</c> in
    /// <c>do_probe_table</c>, mai toccato dagli altri test (KQvK/KRvK/KPvK hanno tutti un pezzo
    /// "unico"). Due cavalli soli non bastano a dare matto: patta teorica, confermata dall'oracolo.</summary>
    [Fact]
    public void KNNvKIsDraw()
    {
        var pos = MakePosition("6k1/8/5NK1/8/8/4N3/8/8 b - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Draw, wdl);
    }

    /// <summary><c>KBBvK</c> — stesso ramo <c>MapKK</c> di <see cref="KNNvKIsDraw"/> ma con esito
    /// opposto: due alfieri (di colore diverso) danno matto, confermato dall'oracolo (score
    /// cp -20000 dal punto di vista del nero).</summary>
    [Fact]
    public void KBBvKBlackToMoveIsLoss()
    {
        var pos = MakePosition("6k1/8/4B1K1/8/8/2B5/8/8 b - - 0 1");
        var wdl = Tablebase.ProbeWdl(pos, out var result);

        Assert.Equal(ProbeState.Ok, result);
        Assert.Equal(WdlScore.Loss, wdl);
    }

    /// <summary>Verifica indipendente delle tabelle combinatorie costruite da
    /// <see cref="TbConstants.BuildCombinatorialTables"/> (tbprobe.cpp:1545-1637): proprietà
    /// strutturali indipendenti dall'implementazione (Binomial = coefficienti binomiali standard,
    /// MapA1D1D4/MapB1H1H7 = biiezioni sulle rispettive triangolazioni) invece di ricontrollare i
    /// valori uno per uno — stesso principio delle verifiche "brute force" usate altrove nel
    /// porting (SEE, perft, accumulatore NNUE).</summary>
    [Fact]
    public void CombinatorialTablesAreConsistent()
    {
        Assert.Equal(5, TbConstants.Binomial[1, 5]);
        Assert.Equal(10, TbConstants.Binomial[2, 5]);
        Assert.Equal(120, TbConstants.Binomial[3, 10]);

        // MapA1D1D4: biiezione su 10 valori (0..9) per le 10 case del triangolo a1-d1-d4.
        var seen = new HashSet<int>();
        int count = 0;
        for (Square s = Square.A1; s <= Square.D4; s++)
        {
            if (TbConstants.OffA1H8(s) > 0 || Types.FileOf(s) > StockfishSharp.Engine.File.D)
                continue;
            count++;
            Assert.True(seen.Add(TbConstants.MapA1D1D4[(byte)s]), $"Valore duplicato per {s}");
        }
        Assert.Equal(10, count);
        Assert.Equal(10, seen.Count);
        Assert.Equal(0, TbConstants.MapA1D1D4[(byte)Square.B1]);

        // MapB1H1H7: biiezione su 28 valori (le case sotto la diagonale a1-h8).
        var seenB = new HashSet<int>();
        int countB = 0;
        for (Square s = Square.A1; s <= Square.H8; s++)
        {
            if (TbConstants.OffA1H8(s) >= 0)
                continue;
            countB++;
            Assert.True(seenB.Add(TbConstants.MapB1H1H7[(byte)s]));
        }
        Assert.Equal(28, countB);
        Assert.Equal(28, seenB.Count);
    }

    /// <summary>Verifica indipendente di DTZ: partendo da KQvK (matto forzato), seguire ad ogni
    /// mossa del lato vincente la mossa che rende il DTZ dell'avversario più vicino a zero (senza
    /// alcuna euristica di scacchi, solo <see cref="Tablebase.ProbeDtz"/> applicato a ogni mossa
    /// legale) deve raggiungere scacco matto entro la distanza dichiarata da DTZ alla radice — il
    /// lato perdente gioca la prima mossa legale disponibile: DTZ è già la difesa migliore
    /// possibile per lui, quindi qualunque altra mossa porta al matto entro lo stesso limite o
    /// prima. Stesso principio delle verifiche "gioca fino in fondo" già usate per SEE/perft in
    /// altre fasi del porting.</summary>
    [Fact]
    public void KQvKDtzReachesMateWithinDeclaredDistance()
    {
        var pos = MakePosition("4k3/8/4K3/8/8/8/8/4Q3 w - - 0 1");
        Color winningSide = pos.SideToMove;
        int rootDtz = Tablebase.ProbeDtz(pos, out var rootResult);
        Assert.NotEqual(ProbeState.Fail, rootResult);
        Assert.True(rootDtz > 0, "La posizione e' teoricamente vinta: DTZ deve essere positivo");

        int maxPlies = rootDtz + 4; // margine di sicurezza, DTZ e' gia' espresso in ply
        for (int ply = 0; ply < maxPlies; ply++)
        {
            List<Move> moves = [];
            MoveGen.Generate(GenType.Legal, pos, moves);
            if (moves.Count == 0)
                break; // matto raggiunto

            Move chosen;
            if (pos.SideToMove == winningSide)
            {
                // Convenzione DTZ (probe_dtz, tbprobe.cpp:1699-1722 + root_probe:1826-1831): dopo
                // la mossa tocca all'avversario, quindi un oppDtz NEGATIVO significa "l'avversario
                // sta ancora perdendo" (mossa che mantiene la vittoria). Fra queste, la mossa che
                // porta al matto piu' in fretta e' quella con oppDtz PIU' VICINO A ZERO (meno
                // negativo): il "mio" dtz continuando da li' e' -oppDtz, minimizzarlo equivale a
                // massimizzare oppDtz fra i candidati negativi.
                Move? best = null;
                int bestOpponentDtz = int.MinValue;
                var trialSt = new StateInfo(); // riusata solo per le prove do/undo immediate (come search<>() nella fonte)
                foreach (var m in moves)
                {
                    pos.DoMove(m, trialSt);
                    int oppDtz = Tablebase.ProbeDtz(pos, out var r);
                    pos.UndoMove(m);

                    // ZeroingBestMove e' un esito valido di ProbeDtz (search<true> ha trovato una
                    // mossa vincente che azzera il conteggio, tbprobe.cpp:1735-1736) — solo FAIL
                    // e' un vero fallimento del probe (file mancante/corrotto).
                    Assert.NotEqual(ProbeState.Fail, r);
                    if (oppDtz < 0 && oppDtz > bestOpponentDtz)
                    {
                        bestOpponentDtz = oppDtz;
                        best = m;
                    }
                }
                Assert.NotNull(best);
                chosen = best!.Value;
            }
            else
            {
                // Il lato perdente non ha bisogno di giocare "bene" per questa verifica: DTZ
                // rappresenta gia' la difesa migliore possibile (il caso peggiore per chi vince),
                // quindi qualunque mossa legale del perdente porta al matto entro lo stesso limite
                // o prima — non serve applicare qui lo stesso criterio di ottimizzazione.
                chosen = moves[0];
            }

            pos.DoMove(chosen, new StateInfo()); // mossa reale, resta nella catena: serve un oggetto dedicato
        }

        List<Move> finalMoves = [];
        MoveGen.Generate(GenType.Legal, pos, finalMoves);
        Assert.True(finalMoves.Count == 0 && pos.Checkers() != 0,
            "Doveva essere raggiunto scacco matto entro la distanza dichiarata da DTZ");
    }
}

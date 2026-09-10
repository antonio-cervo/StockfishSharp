namespace StockfishSharp.Engine;

/// <summary><c>std::atomic&lt;bool&gt; stop</c> di <c>ThreadPool</c> (thread.h) — il modo in cui la
/// FONTE ferma una ricerca: un flag condiviso da tutti i worker, alzato dal thread principale
/// (<c>check_time</c>, search.cpp:2129) o dalla GUI ("stop"/"quit"), e controllato nei nodi.
///
/// Il punto non e' il flag in se': e' che fermarsi con un flag fa RIENTRARE la ricorsione, che
/// chiama tutte le <c>undo_move</c> in sospeso e riconsegna la posizione alla radice. Fermarsi con
/// un'eccezione, come faceva questo porting fino al 2026-09-10, salta quelle undo_move e lascia la
/// posizione profonda N mosse nell'albero — vedi il commento su <c>Search._segnaleStop</c> per il
/// piantamento reale che ne e' derivato sul bot.</summary>
public sealed class SegnaleStop
{
    private volatile bool _alzato;

    /// <summary><c>threads.stop.load(std::memory_order_relaxed)</c>.</summary>
    public bool Alzato => _alzato;

    /// <summary><c>threads.stop = true</c>.</summary>
    public void Alza() => _alzato = true;

    /// <summary><c>threads.stop = false</c> — solo in start_thinking, prima di lanciare i worker.</summary>
    public void Azzera() => _alzato = false;
}

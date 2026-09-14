namespace StockfishSharp.Engine;

/// <summary><c>std::atomic_bool stop, increaseDepth;</c> — i DUE flag condivisi di
/// <c>ThreadPool</c> (thread.h:157). Nella fonte sono dichiarati sulla stessa riga e azzerati
/// insieme in <c>start_thinking</c> (thread.cpp:304-307), quindi qui vivono nello stesso oggetto:
/// uno solo per il pool, agganciato a ogni worker da <c>Search.SetSegnaliPool</c>.
///
/// <para><b>stop</b> e' il modo in cui la FONTE ferma una ricerca: alzato dal thread principale
/// (<c>check_time</c>, search.cpp:2129) o dalla GUI ("stop"/"quit"), e controllato nei nodi da
/// tutti. Il punto non e' il flag in se': e' che fermarsi con un flag fa RIENTRARE la ricorsione,
/// che chiama tutte le <c>undo_move</c> in sospeso e riconsegna la posizione alla radice. Fermarsi
/// con un'eccezione, come faceva questo porting fino al 2026-09-10, salta quelle undo_move e lascia
/// la posizione profonda N mosse nell'albero — vedi il commento su <c>Search._segnali</c> per il
/// piantamento reale che ne e' derivato sul bot.</para>
///
/// <para><b>increaseDepth</b> lo scrive SOLO il thread principale (search.cpp:613, dentro il blocco
/// a lui riservato) e lo leggono TUTTI (search.cpp:356): quando il principale decide che il tempo
/// stringe, anche gli helper smettono di cercare a profondita' piena e fanno ricerche piu'
/// economiche che riempiono la TT — e' parte di come il Lazy SMP guadagna. Tenerlo LOCALE a ogni
/// worker, come faceva questo porting fino al 2026-09-14, lo rendeva sempre <c>true</c> per gli
/// helper (vedi docs/audit-multithread.md, divario 1). Invisibile a Threads=1, dove scrittore e
/// lettore sono lo stesso thread.</para></summary>
public sealed class SegnaliPool
{
    private volatile bool _stop;
    private volatile bool _aumentaProfondita = true;

    /// <summary><c>threads.stop.load(std::memory_order_relaxed)</c>.</summary>
    public bool StopAlzato => _stop;

    /// <summary><c>threads.stop = true</c>.</summary>
    public void AlzaStop() => _stop = true;

    /// <summary><c>threads.increaseDepth</c> — search.cpp:356 in lettura (tutti i worker),
    /// search.cpp:613 in scrittura (solo il principale).</summary>
    public bool AumentaProfondita
    {
        get => _aumentaProfondita;
        set => _aumentaProfondita = value;
    }

    /// <summary><c>ThreadPool::start_thinking</c>, thread.cpp:304-307 — "stop = false" e
    /// "increaseDepth = true" insieme, una volta sola prima di avviare i worker.</summary>
    public void AzzeraPerNuovaRicerca()
    {
        _stop = false;
        _aumentaProfondita = true;
    }
}

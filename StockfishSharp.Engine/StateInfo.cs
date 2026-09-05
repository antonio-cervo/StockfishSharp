// Corrisponde a struct StateInfo in src/position.h (righe 45-67). Vedi Types.cs per la nota
// generale sul porting.

namespace StockfishSharp.Engine;

/// <summary>Informazioni necessarie per ripristinare una <see cref="Position"/> al suo stato
/// precedente quando si disfa una mossa — <c>StateInfo</c>, position.h:45-67. Classe (non struct)
/// perché la fonte la collega in una catena tramite puntatore <c>previous</c>: qui
/// <see cref="Previous"/> è una vera referenza allo StateInfo del ply precedente, stesso schema.
/// NON portati (rimandati alla fase di ricerca, non servono a perft/do_move/undo_move da soli):
/// nulla per ora — tutti i campi della fonte sono presenti.</summary>
public sealed class StateInfo
{
    // Copiati quando si fa una mossa (position.cpp:832, memcpy fino a "key" escluso) — qui
    // copiati esplicitamente campo per campo in Position.DoMove invece che con un memcpy con
    // offset magico: stesso risultato, più leggibile e non fragile all'ordine di dichiarazione.
    public ulong MaterialKey;
    public ulong PawnKey;
    public ulong MinorPieceKey;
    public readonly ulong[] NonPawnKey = new ulong[Colors.Nb];
    public readonly int[] NonPawnMaterial = new int[Colors.Nb];
    public CastlingRights CastlingRights;
    public int Rule50;
    public int PliesFromNull;
    public Square EpSquare = Square.None;

    // NON copiati quando si fa una mossa (ricalcolati comunque da set_check_info/do_move stesso).
    public ulong Key;
    public ulong CheckersBB;
    public StateInfo? Previous;
    public readonly ulong[] BlockersForKing = new ulong[Colors.Nb];
    public readonly ulong[] Pinners = new ulong[Colors.Nb];
    public readonly ulong[] CheckSquares = new ulong[PieceTypes.Nb];
    public Piece CapturedPiece;
    public int Repetition;

    /// <summary>Copia i campi "Copiati quando si fa una mossa" da un altro StateInfo — usato da
    /// <see cref="Position.DoMove"/> al posto del memcpy con offset della fonte.</summary>
    public void CopyMoveFieldsFrom(StateInfo other)
    {
        MaterialKey = other.MaterialKey;
        PawnKey = other.PawnKey;
        MinorPieceKey = other.MinorPieceKey;
        Array.Copy(other.NonPawnKey, NonPawnKey, Colors.Nb);
        Array.Copy(other.NonPawnMaterial, NonPawnMaterial, Colors.Nb);
        CastlingRights = other.CastlingRights;
        Rule50 = other.Rule50;
        PliesFromNull = other.PliesFromNull;
        EpSquare = other.EpSquare;
    }
}

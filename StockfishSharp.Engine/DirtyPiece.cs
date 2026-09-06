// src/types.h:296-306 — "Keep track of what a move changes on the board (used by NNUE)". Classe
// (non struct) perché la fonte la passa per puntatore e la popola incrementalmente in più punti
// sparsi di do_move/do_castling — un solo record per mossa, non una lista come DirtyThreat.

namespace StockfishSharp.Engine;

public sealed class DirtyPiece
{
    /// <summary>Il pezzo che si è mosso — mai <see cref="Piece.None"/> nella fonte.</summary>
    public Piece Pc;

    public Square From;

    /// <summary><see cref="Square.None"/> per le promozioni (la fonte: "to should be SQ_NONE for
    /// promotions" — il pezzo promosso è tracciato a parte via <see cref="AddSq"/>/<see
    /// cref="AddPc"/>, dato che cambia identità).</summary>
    public Square To;

    /// <summary><see cref="Square.None"/> se la mossa non rimuove un secondo pezzo (nessuna
    /// cattura). L'arrocco usa questo campo per la torre rimossa dalla sua casa di partenza.</summary>
    public Square RemoveSq;

    /// <summary><see cref="Square.None"/> se la mossa non aggiunge un secondo pezzo (nessuna
    /// promozione). L'arrocco usa questo campo per la torre aggiunta alla sua casa di arrivo.</summary>
    public Square AddSq;

    public Piece RemovePc;
    public Piece AddPc;
}

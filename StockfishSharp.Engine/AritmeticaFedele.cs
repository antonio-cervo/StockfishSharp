// Operazioni aritmetiche che in C# NON si comportano come nella fonte, raccolte in un posto solo
// perche' sono una CLASSE di errori, non casi isolati: due istanze reali trovate il 2026-09-08, e
// nessuna delle due era visibile leggendo il codice riga per riga, perche' le righe combaciano —
// e' la semantica del linguaggio a differire.
//
// IL PROBLEMA. In C++ le "usual arithmetic conversions" fanno vincere il tipo SENZA SEGNO: se un
// `int` negativo compare in un'espressione con un `u64`, viene convertito a u64 (cioe' 2^64 + x) e
// tutta l'espressione diventa unsigned. Una divisione successiva e' quindi una divisione senza
// segno; se il divisore e' una potenza di due (e divide quindi 2^64 esattamente), il risultato
// riconvertito a 32 bit vale esattamente floor(x / d) — arrotondamento verso MENO INFINITO.
// In C# non esistono conversioni implicite di questo tipo e `/` tronca sempre verso ZERO: i due
// risultati differiscono di 1 ogni volta che il valore e' negativo e non e' divisibile per d.
//
// PERCHE' CONTA. Un'unita' di scarto non resta locale: in entrambi i casi trovati alimenta un
// parametro di potatura o di finestra (la media mobile che decide la finestra di aspirazione; il
// bonus di history che decide l'ordinamento delle mosse), quindi cambia l'albero. Misurato: su
// 8/2p5/3p4/KP5r/5R1k/8/4P1P1/8 b - - 0 11 bastava a far divergere il punteggio dall'oracolo a
// profondita' 5, con l'albero bit-identico fino a 4. La fonte stessa e' consapevole che questa
// aritmetica e' significativa: sopra search.cpp:1979 c'e' scritto "don't remove the cast to a
// 64-bit number else the multiplication can overflow [...] which would change the bench signature".
//
// COME SI CERCANO ALTRE ISTANZE: `grep -n "u64\|usize\|unsigned" src/*.cpp` e, per ogni riga
// trovata, chiedersi se un operando puo' essere negativo e se c'e' una divisione a valle.

namespace StockfishSharp.Engine;

internal static class AritmeticaFedele
{
    /// <summary>Divisione intera con arrotondamento verso meno infinito — quello che ottiene la
    /// fonte quando un valore con segno finisce in un'espressione <c>u64</c> e viene poi diviso per
    /// una potenza di due. Vedi la nota in testa al file.</summary>
    internal static long DivisionePavimento(long a, long b) =>
        (a / b) - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);
}

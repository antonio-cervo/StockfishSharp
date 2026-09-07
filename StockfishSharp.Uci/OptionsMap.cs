// Porting fedele di ucioption.h + ucioption.cpp — infrastruttura generica delle opzioni UCI.
// A differenza del resto di StockfishSharp.Uci (layer pratico, non un porting — vedi Program.cs),
// questo pezzo specifico è un porting vero: sostituisce la catena if/else ad-hoc di
// HandleSetOption con lo stesso meccanismo generico della fonte (tipo/min/max/on_change,
// ordine di stampa per indice di inserimento).

using System.Text;

namespace StockfishSharp.Uci;

/// <summary><c>Option::OnChange</c>, ucioption.h:41 — ritorna un messaggio informativo
/// facoltativo da inoltrare a <see cref="OptionsMap.Info"/> (es. "Threads: usando N thread"),
/// oppure null se non c'è nulla da segnalare.</summary>
public delegate string? OptionOnChange(Option option);

/// <summary><c>class Option</c>, ucioption.h:39-70 + ucioption.cpp:89-185.</summary>
public sealed class Option
{
    public string DefaultValue = "";
    public string CurrentValue = "";
    public string Type = "";
    public int Min;
    public int Max;
    public int Idx;
    public readonly OptionOnChange? OnChangeCallback;
    internal OptionsMap? Parent;

    /// <summary><c>Option(OnChange)</c> — bottone ("button"), es. "Clear Hash".</summary>
    public Option(OptionOnChange? onChange = null)
    {
        Type = "button";
        OnChangeCallback = onChange;
    }

    /// <summary><c>Option(bool, OnChange)</c> — casella di spunta ("check").</summary>
    public Option(bool v, OptionOnChange? onChange = null)
    {
        Type = "check";
        OnChangeCallback = onChange;
        DefaultValue = CurrentValue = v ? "true" : "false";
    }

    /// <summary><c>Option(const char*, OnChange)</c> — stringa libera ("string").</summary>
    public Option(string v, OptionOnChange? onChange = null)
    {
        Type = "string";
        OnChangeCallback = onChange;
        DefaultValue = CurrentValue = v;
    }

    /// <summary><c>Option(int, int, int, OnChange)</c> — intero con estremi ("spin").</summary>
    public Option(int v, int minv, int maxv, OptionOnChange? onChange = null)
    {
        Type = "spin";
        Min = minv;
        Max = maxv;
        OnChangeCallback = onChange;
        DefaultValue = CurrentValue = v.ToString();
    }

    /// <summary><c>Option(const char*, const char*, OnChange)</c> — scelta multipla ("combo").</summary>
    public Option(string v, string cur, OptionOnChange? onChange = null)
    {
        Type = "combo";
        OnChangeCallback = onChange;
        DefaultValue = v;
        CurrentValue = cur;
    }

    /// <summary><c>Option::operator int()</c>, ucioption.cpp:120-123.</summary>
    public static explicit operator int(Option o) =>
        o.Type == "spin" ? int.Parse(o.CurrentValue) : (o.CurrentValue == "true" ? 1 : 0);

    /// <summary>Comodo alias booleano dello stesso cast — C# non converte int→bool implicitamente
    /// come fa C++ nei siti di chiamata della fonte (es. "bool(options[...])").</summary>
    public static explicit operator bool(Option o) => (int)o != 0;

    /// <summary><c>Option::operator std::string()</c>, ucioption.cpp:125-128.</summary>
    public static explicit operator string(Option o) => o.CurrentValue;

    /// <summary><c>Option::operator==(const char*)</c>, ucioption.cpp:130-133 — confronto
    /// case-insensitive per le opzioni "combo".</summary>
    public bool Equals(string s) =>
        string.Equals(CurrentValue, s, StringComparison.OrdinalIgnoreCase);

    private static bool ValueInRange(string v, int min, int max) =>
        v.Length != 0 && long.TryParse(v, out long result) && result >= min && result <= max;

    /// <summary><c>Option::operator=(const std::string&)</c>, ucioption.cpp:151-185 — aggiorna
    /// il valore corrente e invoca <see cref="OnChangeCallback"/>.</summary>
    public void Set(string v)
    {
        if ((Type != "button" && Type != "string" && v.Length == 0)
            || (Type == "check" && v != "true" && v != "false")
            || (Type == "spin" && !ValueInRange(v, Min, Max)))
            return;

        if (Type == "combo")
        {
            var comboMap = new OptionsMap(); // solo per il confronto case-insensitive dei token
            foreach (var token in DefaultValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                comboMap.Add(token, new Option());
            if (!comboMap.Contains(v) || v == "var")
                return;
        }

        if (Type == "string")
            CurrentValue = v == "<empty>" ? "" : v;
        else if (Type != "button")
            CurrentValue = v;

        if (OnChangeCallback != null)
        {
            string? ret = OnChangeCallback(this);
            if (ret != null && Parent?.Info != null)
                Parent.Info(ret);
        }
    }
}

/// <summary><c>class OptionsMap</c>, ucioption.h:72-103 + ucioption.cpp:41-87,187-212.</summary>
public sealed class OptionsMap
{
    public delegate void InfoListener(string message);

    // std::map<string, Option, CaseInsensitiveLess> — un Dictionary case-insensitive replica la
    // stessa semantica di lookup; l'ORDINE DI STAMPA (operator<<) non dipende dall'ordinamento
    // della mappa ma dal campo Idx (ordine di inserimento), esattamente come nella fonte.
    private readonly Dictionary<string, Option> _options = new(StringComparer.OrdinalIgnoreCase);
    private int _insertOrder;

    public InfoListener? Info { get; private set; }

    public void AddInfoListener(InfoListener listener) => Info = listener;

    /// <summary><c>OptionsMap::setoption</c>, ucioption.cpp:43-60 — qui il parsing di
    /// nome/valore (che può contenere spazi) resta in <c>Program.cs</c> (HandleSetOption, già
    /// corretto), questo metodo copre solo la parte dopo: applica il valore o segnala l'errore.</summary>
    public void SetOption(string name, string value)
    {
        if (_options.TryGetValue(name, out var option))
            option.Set(value);
        else
            Console.WriteLine($"No such option: {name}");
    }

    /// <summary><c>OptionsMap::operator[]</c>, ucioption.cpp:62-66.</summary>
    public Option this[string name] => _options[name];

    /// <summary><c>OptionsMap::add</c>, ucioption.cpp:69-84.</summary>
    public void Add(string name, Option option)
    {
        if (!_options.ContainsKey(name))
        {
            option.Parent = this;
            option.Idx = _insertOrder++;
            _options[name] = option;
        }
        else
        {
            Console.Error.WriteLine($"Option \"{name}\" was already added!");
            Environment.Exit(1);
        }
    }

    /// <summary><c>OptionsMap::count</c>, ucioption.cpp:87.</summary>
    public bool Contains(string name) => _options.ContainsKey(name);

    /// <summary><c>operator&lt;&lt;(ostream&amp;, const OptionsMap&amp;)</c>, ucioption.cpp:187-212 —
    /// usato dal comando "uci" per annunciare le opzioni nell'ordine in cui sono state
    /// registrate (non l'ordine alfabetico della mappa).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int idx = 0; idx < _options.Count; idx++)
            foreach (var (name, o) in _options)
                if (o.Idx == idx)
                {
                    sb.Append($"\noption name {name} type {o.Type}");

                    if (o.Type is "check" or "combo")
                        sb.Append($" default {o.DefaultValue}");
                    else if (o.Type == "string")
                        sb.Append($" default {(o.DefaultValue.Length == 0 ? "<empty>" : o.DefaultValue)}");
                    else if (o.Type == "spin")
                        sb.Append($" default {o.DefaultValue} min {o.Min} max {o.Max}");

                    break;
                }
        return sb.ToString();
    }
}

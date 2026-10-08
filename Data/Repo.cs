using System.Globalization;
using System.Reflection;

namespace ControleTecnico.Data;

/// <summary>Base das entidades persistidas. Todas as propriedades públicas
/// (string, números, bool, DateTime e anuláveis) viram colunas VARCHAR.</summary>
public abstract class Entity
{
    public string Id { get; set; } = "";
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public string AtualizadoPor { get; set; } = "";

    public T Copia<T>() where T : Entity => (T)MemberwiseClone();
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class NaoPersistirAttribute : Attribute { }

/// <summary>
/// Repositório genérico sobre o ParquetStore: cache em memória (write-through),
/// leitura consolidada por id e gravação em lote. Devolve cópias — quem recebe
/// pode alterar sem corromper o cache.
/// </summary>
public sealed class Repo<T> where T : Entity, new()
{
    private static readonly PropertyInfo[] Props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<NaoPersistirAttribute>() is null).ToArray();

    private readonly ParquetStore _store;
    private readonly string _entity;
    private readonly object _lock = new();
    private Dictionary<string, T>? _cache;

    public Repo(ParquetStore store, string entity) { _store = store; _entity = entity; }

    public string Entidade => _entity;

    private Dictionary<string, T> Cache()
    {
        if (_cache is not null) return _cache;
        var d = new Dictionary<string, T>();
        foreach (var row in _store.ReadAll(_entity))
        {
            var e = new T();
            foreach (var p in Props)
                if (row.TryGetValue(p.Name, out var v)) Set(p, e, v);
            if (!string.IsNullOrEmpty(e.Id)) d[e.Id] = e;
        }
        return _cache = d;
    }

    public static string NovoId() => Guid.NewGuid().ToString("N")[..12];

    public List<T> Todos() { lock (_lock) return Cache().Values.Select(x => x.Copia<T>()).ToList(); }

    public T? Obter(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return Cache().TryGetValue(id, out var e) ? e.Copia<T>() : null;
    }

    public bool Existe(string? id) { lock (_lock) return !string.IsNullOrEmpty(id) && Cache().ContainsKey(id); }

    public int Contar() { lock (_lock) return Cache().Count; }

    public List<T> Onde(Func<T, bool> filtro)
    {
        lock (_lock) return Cache().Values.Where(filtro).Select(x => x.Copia<T>()).ToList();
    }

    public T Salvar(T e, string? por = null) => SalvarVarios(new[] { e }, por)[0];

    public List<T> SalvarVarios(IEnumerable<T> itens, string? por = null)
    {
        var lista = itens.ToList();
        if (lista.Count == 0) return lista;
        lock (_lock)
        {
            var cache = Cache();
            var agora = DateTime.UtcNow;
            foreach (var e in lista)
            {
                if (string.IsNullOrEmpty(e.Id)) e.Id = NovoId();
                if (cache.TryGetValue(e.Id, out var antigo)) e.CriadoEm = antigo.CriadoEm;
                if (e.CriadoEm == default) e.CriadoEm = agora;
                e.AtualizadoEm = agora;
                if (por is not null) e.AtualizadoPor = por;
            }
            var rows = lista.Select(e => (IReadOnlyList<KeyValuePair<string, string?>>)
                Props.Select(p => new KeyValuePair<string, string?>(p.Name, Get(p, e))).ToList()).ToList();
            _store.WriteBatch(_entity, rows);   // primeiro o disco; só então o cache
            foreach (var e in lista) cache[e.Id] = e.Copia<T>();
        }
        return lista;
    }

    public void Apagar(string id)
    {
        lock (_lock)
        {
            var cache = Cache();
            if (!cache.TryGetValue(id, out var e)) return;
            var rows = new List<IReadOnlyList<KeyValuePair<string, string?>>>
                { Props.Select(p => new KeyValuePair<string, string?>(p.Name, Get(p, e))).ToList() };
            _store.WriteBatch(_entity, rows, deleted: true);
            cache.Remove(id);
        }
    }

    /// <summary>Esquece o cache e apaga os arquivos (reset de demonstração).</summary>
    public void Limpar()
    {
        lock (_lock) { _store.Clear(_entity); _cache = new Dictionary<string, T>(); }
    }

    public void Recarregar() { lock (_lock) _cache = null; }

    // ---- serialização --------------------------------------------------
    private static string? Get(PropertyInfo p, object o)
    {
        var v = p.GetValue(o);
        return v switch
        {
            null => null,
            DateTime dt => (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                .ToString("o", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
    }

    private static void Set(PropertyInfo p, object o, string? s)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (string.IsNullOrEmpty(s))
        {
            if (t == typeof(string)) p.SetValue(o, "");
            else if (Nullable.GetUnderlyingType(p.PropertyType) is not null) p.SetValue(o, null);
            return;
        }
        object? v = null;
        if (t == typeof(string)) v = s;
        else if (t == typeof(int)) v = int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        else if (t == typeof(long)) v = long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;
        else if (t == typeof(double)) v = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
        else if (t == typeof(bool)) v = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        else if (t == typeof(DateTime))
            v = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : default(DateTime);
        if (v is not null) p.SetValue(o, v);
    }
}

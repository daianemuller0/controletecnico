using System.Data;
using DuckDB.NET.Data;

namespace ControleTecnico.Data;

/// <summary>
/// Camada de dados no mesmo padrão do Previsão: DuckDB como MOTOR sobre arquivos
/// Parquet. Cada gravação cria um pequeno .parquet novo na subpasta da entidade;
/// a leitura consolida (versão mais recente por id, ignorando os apagados).
/// Sem arquivo de banco compartilhado e sem servidor de banco.
/// </summary>
public sealed class ParquetStore
{
    public string Folder { get; }
    private readonly HashSet<string> _dirsOk = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private long _lastTick;

    public ParquetStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    private string EntityDir(string entity)
    {
        var dir = Path.Combine(Folder, entity);
        lock (_dirsOk) { if (_dirsOk.Add(dir)) Directory.CreateDirectory(dir); }
        return dir;
    }

    private static DuckDBConnection Open()
    {
        var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static string Duck(string path) => path.Replace('\\', '/').Replace("'", "''");

    // Ticks estritamente crescentes: duas gravações no mesmo instante não empatam.
    private long NextTick()
    {
        lock (_writeLock)
        {
            var t = DateTime.UtcNow.Ticks;
            if (t <= _lastTick) t = _lastTick + 1;
            _lastTick = t;
            return t;
        }
    }

    /// <summary>Grava várias linhas (mesmas colunas) num único Parquet.</summary>
    public void WriteBatch(string entity, IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> rows, bool deleted = false)
    {
        if (rows.Count == 0) return;
        using var conn = Open();
        var first = rows[0];
        var colDefs = string.Join(", ", first.Select(kv => $"\"{kv.Key}\" VARCHAR")) + ", _ts BIGINT, _deleted BOOLEAN";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE t ({colDefs});";
            cmd.ExecuteNonQuery();
        }
        var placeholders = string.Join(", ", first.Select(_ => "?")) + ", ?, ?";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO t VALUES ({placeholders});";
            foreach (var row in rows)
            {
                cmd.Parameters.Clear();
                foreach (var kv in row) AddParam(cmd, kv.Value);
                AddParam(cmd, NextTick());
                AddParam(cmd, deleted);
                cmd.ExecuteNonQuery();
            }
        }
        var dir = EntityDir(entity);
        var temp = Path.Combine(dir, $"_w_{Guid.NewGuid():N}.tmp");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"COPY t TO '{Duck(temp)}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            cmd.ExecuteNonQuery();
        }
        // Grava em .tmp e promove: leitores nunca veem arquivo pela metade.
        File.Move(temp, Path.Combine(dir, $"{NextTick():D19}_{Guid.NewGuid():N}.parquet"));
    }

    private static void AddParam(IDbCommand cmd, object? value)
    {
        var p = cmd.CreateParameter();
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Lê a entidade consolidada: versão mais recente por id, sem apagados.</summary>
    public List<Dictionary<string, string?>> ReadAll(string entity)
    {
        var dir = EntityDir(entity);
        var files = Directory.GetFiles(dir, "*.parquet");
        var result = new List<Dictionary<string, string?>>();
        if (files.Length == 0) return result;

        using var conn = Open();
        var glob = Duck(Path.Combine(dir, "*.parquet"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT * EXCLUDE (_rn, _ts, _deleted)
FROM (
    SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
    FROM read_parquet('{glob}', union_by_name=true)
)
WHERE _rn = 1 AND NOT _deleted;";
        using var r = cmd.ExecuteReader();
        var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        while (r.Read())
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length; i++) d[names[i]] = r.IsDBNull(i) ? null : r.GetValue(i)?.ToString();
            result.Add(d);
        }
        return result;
    }

    public int FileCount(string entity) => Directory.GetFiles(EntityDir(entity), "*.parquet").Length;

    public IEnumerable<string> Entities() =>
        Directory.GetDirectories(Folder).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('_') && n != "arquivos")!;

    /// <summary>Compacta: um único Parquet com a versão mais recente de cada id.</summary>
    public int Compact(string entity)
    {
        var dir = EntityDir(entity);
        var old = Directory.GetFiles(dir, "*.parquet");
        if (old.Length <= 1) return old.Length;
        var tmp = Path.Combine(dir, $"_compact_{Guid.NewGuid():N}.tmp");
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
COPY (
    SELECT * EXCLUDE (_rn) FROM (
        SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
        FROM read_parquet('{Duck(Path.Combine(dir, "*.parquet"))}', union_by_name=true)
    ) WHERE _rn = 1 AND NOT _deleted
) TO '{Duck(tmp)}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            cmd.ExecuteNonQuery();
        }
        File.Move(tmp, Path.Combine(dir, $"{NextTick():D19}_{Guid.NewGuid():N}.parquet"));
        foreach (var f in old) { try { File.Delete(f); } catch { } }
        return 1;
    }

    /// <summary>Remove todos os arquivos da entidade (reset do ambiente de demonstração).</summary>
    public void Clear(string entity)
    {
        var dir = EntityDir(entity);
        foreach (var f in Directory.GetFiles(dir, "*.parquet")) File.Delete(f);
    }
}

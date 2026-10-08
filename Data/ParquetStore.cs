using System.Data;
using DuckDB.NET.Data;

namespace ControleTecnico.Data;

/// <summary>
/// Camada de dados no padrão do Previsão: DuckDB como MOTOR sobre arquivos Parquet numa pasta —
/// aqui, a pasta de rede compartilhada (padrão: \\BZVCPFIL003\proj_ramires$\DB\tec).
///
///   &lt;raiz&gt;/&lt;entidade&gt;/*.parquet   cada gravação é um arquivo novo e imutável (nome único por máquina/instante)
///   &lt;raiz&gt;/arquivos/yyyyMM/*.bin    anexos (nome opaco; acesso só pelo servidor)
///   &lt;raiz&gt;/_historico/              arquivos retirados pela compactação ou por "limpar" (recuperáveis por 30 dias)
///   &lt;raiz&gt;/_locks/                  trava da compactação entre instâncias
///
/// Vários usuários/instâncias gravam ao mesmo tempo sem disputar um arquivo de banco: cada um cria o seu .parquet.
/// A leitura consolida (versão mais recente por id, ignorando apagados) sobre um ESPELHO LOCAL dos arquivos —
/// ler Parquet direto da rede é lento por causa do vaivém; copiar arquivos inteiros (que nunca mudam) é barato
/// e só copia o que ainda não está no espelho.
/// </summary>
public sealed class ParquetStore
{
    public string Folder { get; }
    public string? EspelhoDir { get; }
    public bool Remoto => EspelhoDir is not null;

    private readonly HashSet<string> _dirsOk = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private readonly object _syncLock = new();
    private long _lastTick;
    private readonly string _tmpLocal;

    /// <param name="folder">Pasta de dados (UNC ou local).</param>
    /// <param name="espelho">Pasta local de espelho. Vazio = automático: usa espelho só se a pasta for de rede.</param>
    /// <param name="forcarEspelho">true = usa o espelho mesmo em pasta local (testes).</param>
    public ParquetStore(string folder, string? espelho = null, bool forcarEspelho = false)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && folder.StartsWith(@"\\", StringComparison.Ordinal))
                throw new PlatformNotSupportedException("Caminhos UNC (\\\\servidor\\compartilhamento) só funcionam no Windows. Em Linux/macOS monte o compartilhamento e informe o ponto de montagem em Data:Folder.");
            Folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível acessar a pasta de dados '{folder}'. Verifique a rede/VPN e se a conta que executa o programa tem permissão de leitura e escrita. ({ex.Message})", ex);
        }

        var ehRede = EhRede(Folder);
        if (ehRede || forcarEspelho)
        {
            EspelhoDir = Path.GetFullPath(string.IsNullOrWhiteSpace(espelho)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControleTecnico", "espelho")
                : espelho);
            Directory.CreateDirectory(EspelhoDir);
        }
        _tmpLocal = Path.Combine(Path.GetTempPath(), "ControleTecnico_tmp");
        Directory.CreateDirectory(_tmpLocal);
    }

    private static bool EhRede(string caminho)
    {
        try
        {
            if (caminho.StartsWith(@"\\", StringComparison.Ordinal) || caminho.StartsWith("//", StringComparison.Ordinal)) return true;
            var raiz = Path.GetPathRoot(caminho);
            return !string.IsNullOrEmpty(raiz) && new DriveInfo(raiz).DriveType == DriveType.Network;
        }
        catch { return false; }
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

    /// <summary>Repete uma operação de arquivo em rede (quedas breves, antivírus, arquivo em uso).</summary>
    public static T ComRetentativa<T>(Func<T> op, int tentativas = 4)
    {
        for (var i = 1; ; i++)
        {
            try { return op(); }
            catch (IOException) when (i < tentativas) { Thread.Sleep(150 * i * i); }
            catch (UnauthorizedAccessException) when (i < tentativas) { Thread.Sleep(150 * i * i); }
        }
    }

    // ---------------------------------------------------------------- escrita
    /// <summary>Grava várias linhas (mesmas colunas) num único Parquet. Atômico: o arquivo só aparece na pasta
    /// compartilhada completo (copiado como .tmp e renomeado), então ninguém lê um arquivo pela metade.</summary>
    public void WriteBatch(string entity, IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> rows, bool deleted = false)
    {
        if (rows.Count == 0) return;
        var local = Path.Combine(_tmpLocal, $"w_{Guid.NewGuid():N}.parquet");
        try
        {
            using (var conn = Open())
            {
                var first = rows[0];
                var colDefs = string.Join(", ", first.Select(kv => $"\"{kv.Key}\" VARCHAR")) + ", _ts BIGINT, _deleted BOOLEAN";
                using (var cmd = conn.CreateCommand()) { cmd.CommandText = $"CREATE TABLE t ({colDefs});"; cmd.ExecuteNonQuery(); }
                var placeholders = string.Join(", ", first.Select(_ => "?")) + ", ?, ?";
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"INSERT INTO t VALUES ({placeholders});";
                    foreach (var row in rows)
                    {
                        cmd.Parameters.Clear();
                        foreach (var kv in row) AddParam(cmd, kv.Value);
                        AddParam(cmd, NextTick()); AddParam(cmd, deleted);
                        cmd.ExecuteNonQuery();
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"COPY t TO '{Duck(local)}' (FORMAT PARQUET, COMPRESSION ZSTD);";
                    cmd.ExecuteNonQuery();
                }
            }
            var dir = EntityDir(entity);
            var nome = $"{NextTick():D19}_{Environment.MachineName.ToLowerInvariant()}_{Guid.NewGuid():N}.parquet";
            var tmpRemoto = Path.Combine(dir, $"_w_{Guid.NewGuid():N}.tmp");
            ComRetentativa(() => { File.Copy(local, tmpRemoto, true); return 0; });
            ComRetentativa(() => { File.Move(tmpRemoto, Path.Combine(dir, nome)); return 0; });
            // já deixa a cópia local pronta: lemos o que acabamos de gravar sem baixar de novo
            if (EspelhoDir is not null)
            {
                var ed = Path.Combine(EspelhoDir, entity); Directory.CreateDirectory(ed);
                File.Copy(local, Path.Combine(ed, nome), true);
            }
        }
        finally { try { File.Delete(local); } catch { } }
    }

    private static void AddParam(IDbCommand cmd, object? value)
    {
        var p = cmd.CreateParameter(); p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p);
    }

    // ---------------------------------------------------------------- leitura
    /// <summary>Assinatura barata da pasta da entidade (uma listagem): muda a cada gravação/compactação, de qualquer máquina.</summary>
    public string Assinatura(string entity)
    {
        var arquivos = new DirectoryInfo(EntityDir(entity)).GetFiles("*.parquet");
        return arquivos.Length == 0 ? "0" : $"{arquivos.Length}|{arquivos.Max(f => f.LastWriteTimeUtc.Ticks)}|{arquivos.Sum(f => f.Length)}";
    }

    /// <summary>Sincroniza o espelho local (copia o que falta, remove o que saiu) e devolve a pasta a consultar.</summary>
    private string PastaDeLeitura(string entity)
    {
        var remoto = EntityDir(entity);
        if (EspelhoDir is null) return remoto;
        lock (_syncLock)
        {
            var destino = Path.Combine(EspelhoDir, entity);
            Directory.CreateDirectory(destino);
            var rem = new DirectoryInfo(remoto).GetFiles("*.parquet");
            var locais = new DirectoryInfo(destino).GetFiles("*.parquet").ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var f in rem)
            {
                if (locais.TryGetValue(f.Name, out var len) && len == f.Length) continue;
                var tmp = Path.Combine(destino, $"_c_{Guid.NewGuid():N}.tmp");
                try
                {
                    ComRetentativa(() => { File.Copy(f.FullName, tmp, true); return 0; });
                    File.Move(tmp, Path.Combine(destino, f.Name), true);
                }
                catch (FileNotFoundException) { /* compactada por outro usuário no meio: a próxima leitura reconcilia */ }
                finally { try { File.Delete(tmp); } catch { } }
            }
            var nomes = rem.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var n in locais.Keys.Where(n => !nomes.Contains(n))) { try { File.Delete(Path.Combine(destino, n)); } catch { } }
            return destino;
        }
    }

    public List<Dictionary<string, string?>> ReadAll(string entity)
    {
        var dir = PastaDeLeitura(entity);
        var result = new List<Dictionary<string, string?>>();
        if (Directory.GetFiles(dir, "*.parquet").Length == 0) return result;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT * EXCLUDE (_rn, _ts, _deleted)
FROM (
    SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
    FROM read_parquet('{Duck(Path.Combine(dir, "*.parquet"))}', union_by_name=true)
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

    public int FileCount(string entity) => new DirectoryInfo(EntityDir(entity)).GetFiles("*.parquet").Length;

    public IEnumerable<string> Entities() =>
        Directory.GetDirectories(Folder).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('_') && n != "arquivos")!;

    // ------------------------------------------------------------- compactação
    private const string HistDir = "_historico";
    private static readonly TimeSpan HistRetencao = TimeSpan.FromDays(30);

    /// <summary>Consolida a entidade num único Parquet. Os arquivos antigos NÃO são apagados: vão para
    /// _historico (recuperáveis por 30 dias). Usa trava na pasta para que duas máquinas não compactem juntas.</summary>
    public int Compact(string entity, int minimoArquivos = 2)
    {
        var dir = EntityDir(entity);
        var capturados = new DirectoryInfo(dir).GetFiles("*.parquet");
        if (capturados.Length < Math.Max(2, minimoArquivos)) return capturados.Length;
        using var trava = TentarTravar(entity);
        if (trava is null) return capturados.Length;      // outra máquina já está compactando

        var leitura = PastaDeLeitura(entity);               // garante que o espelho tem exatamente o que será consolidado
        var nomes = capturados.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tmpLocal = Path.Combine(_tmpLocal, $"c_{Guid.NewGuid():N}.parquet");
        try
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                var lista = string.Join(", ", nomes.Where(n => File.Exists(Path.Combine(leitura, n))).Select(n => $"'{Duck(Path.Combine(leitura, n))}'"));
                if (lista.Length == 0) return capturados.Length;
                cmd.CommandText = $@"
COPY (
    SELECT * EXCLUDE (_rn) FROM (
        SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
        FROM read_parquet([{lista}], union_by_name=true)
    ) WHERE _rn = 1 AND NOT _deleted
) TO '{Duck(tmpLocal)}' (FORMAT PARQUET, COMPRESSION ZSTD);";
                cmd.ExecuteNonQuery();
            }
            var nome = $"{NextTick():D19}_{Environment.MachineName.ToLowerInvariant()}_{Guid.NewGuid():N}.parquet";
            var tmpRemoto = Path.Combine(dir, $"_w_{Guid.NewGuid():N}.tmp");
            ComRetentativa(() => { File.Copy(tmpLocal, tmpRemoto, true); return 0; });
            ComRetentativa(() => { File.Move(tmpRemoto, Path.Combine(dir, nome)); return 0; });
            Arquivar(entity, capturados.Where(f => nomes.Contains(f.Name)).Select(f => f.FullName).ToList());
            return 2;
        }
        finally { try { File.Delete(tmpLocal); } catch { } }
    }

    private void Arquivar(string entity, List<string> arquivos)
    {
        string? destino = null;
        try { destino = Path.Combine(Folder, HistDir, entity, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(destino); } catch { }
        foreach (var f in arquivos)
        {
            try
            {
                if (destino is not null) ComRetentativa(() => { File.Move(f, Path.Combine(destino, Path.GetFileName(f)), true); return 0; });
                else File.Delete(f);
            }
            catch { /* em uso por outra máquina: a próxima rodada limpa */ }
        }
        try
        {
            var raiz = Path.Combine(Folder, HistDir, entity);
            if (!Directory.Exists(raiz)) return;
            foreach (var d in Directory.EnumerateDirectories(raiz))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow - HistRetencao) try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>Retira todos os arquivos da entidade (vão para _historico; nada é perdido de imediato).</summary>
    public void Clear(string entity)
    {
        var dir = EntityDir(entity);
        Arquivar(entity, Directory.GetFiles(dir, "*.parquet").ToList());
        if (EspelhoDir is not null)
        {
            var ed = Path.Combine(EspelhoDir, entity);
            if (Directory.Exists(ed)) foreach (var f in Directory.GetFiles(ed)) try { File.Delete(f); } catch { }
        }
    }

    private IDisposable? TentarTravar(string entity)
    {
        var dir = Path.Combine(Folder, "_locks"); Directory.CreateDirectory(dir);
        var arq = Path.Combine(dir, $"compactar_{entity}.lock");
        try
        {
            if (File.Exists(arq) && File.GetLastWriteTimeUtc(arq) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(arq);   // trava velha
            var fs = new FileStream(arq, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return fs;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---------------------------------------------------------- diagnóstico
    public sealed record Diagnostico(bool Ok, string Pasta, bool Rede, string? Espelho, bool Leitura, bool Escrita, long LatenciaMs, string Mensagem, Dictionary<string, int> Arquivos);

    /// <summary>Teste real de leitura e escrita na pasta compartilhada (cria e apaga um arquivo-sonda).</summary>
    public Diagnostico Diagnosticar()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); bool le = false, es = false; string msg = "";
        var arqs = new Dictionary<string, int>();
        try
        {
            var sonda = Path.Combine(Folder, $"_sonda_{Environment.MachineName}_{Guid.NewGuid():N}.tmp");
            try { File.WriteAllText(sonda, "ok"); es = true; le = File.ReadAllText(sonda) == "ok"; }
            finally { try { File.Delete(sonda); } catch { } }
            foreach (var e in Entities()) arqs[e] = FileCount(e);
        }
        catch (Exception ex) { msg = ex.Message; }
        sw.Stop();
        if (msg == "" && !(le && es)) msg = "Sem permissão de leitura/escrita na pasta.";
        return new Diagnostico(le && es, Folder, Remoto, EspelhoDir, le, es, sw.ElapsedMilliseconds, msg, arqs);
    }
}

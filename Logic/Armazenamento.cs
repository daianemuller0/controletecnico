using System.Security.Cryptography;
using ControleTecnico.Data;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public sealed class ValidacaoException : Exception
{
    public List<string> Erros { get; }
    public ValidacaoException(IEnumerable<string> erros) : base(string.Join(" ", erros)) => Erros = erros.ToList();
    public ValidacaoException(string erro) : this(new[] { erro }) { }
}

/// <summary>Armazenamento persistente dos anexos: arquivos em disco com nome opaco
/// (nunca o nome enviado) e metadados no Parquet. O acesso é sempre mediado pelo
/// servidor (endpoint autenticado), jamais por URL pública.</summary>
public sealed class Armazenamento
{
    public const long TamanhoMaximo = 15L * 1024 * 1024;
    private static readonly Dictionary<string, string> Tipos = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
    };

    private readonly Db _db;
    private readonly string _dir;

    public Armazenamento(Db db)
    {
        _db = db;
        _dir = Path.Combine(db.Store.Folder, "arquivos");
        Directory.CreateDirectory(_dir);
    }

    public static string TiposAceitos => ".pdf, .jpg, .jpeg, .png";

    /// <summary>Valida extensão, tamanho e assinatura do arquivo (o conteúdo precisa
    /// ser do tipo que a extensão diz).</summary>
    public static void Validar(string nome, long tamanho, ReadOnlySpan<byte> inicio)
    {
        var ext = Path.GetExtension(nome ?? "");
        if (!Tipos.TryGetValue(ext, out _))
            throw new ValidacaoException($"Formato não aceito ({(string.IsNullOrEmpty(ext) ? "sem extensão" : ext)}). Use {TiposAceitos}.");
        if (tamanho <= 0) throw new ValidacaoException("O arquivo está vazio.");
        if (tamanho > TamanhoMaximo) throw new ValidacaoException($"O arquivo excede o limite de {TamanhoMaximo / 1024 / 1024} MB.");
        var ok = ext.ToLowerInvariant() switch
        {
            ".pdf" => inicio.Length >= 4 && inicio[0] == 0x25 && inicio[1] == 0x50 && inicio[2] == 0x44 && inicio[3] == 0x46,
            ".png" => inicio.Length >= 4 && inicio[0] == 0x89 && inicio[1] == 0x50 && inicio[2] == 0x4E && inicio[3] == 0x47,
            _ => inicio.Length >= 3 && inicio[0] == 0xFF && inicio[1] == 0xD8 && inicio[2] == 0xFF,
        };
        if (!ok) throw new ValidacaoException($"O conteúdo de \"{nome}\" não corresponde ao formato {ext}.");
    }

    public async Task<Anexo> SalvarAsync(Ator ator, string donoTipo, string donoId, string nome, Stream conteudo)
    {
        nome = Path.GetFileName(nome ?? "arquivo");
        using var ms = new MemoryStream();
        var buffer = new byte[81920]; int n;
        while ((n = await conteudo.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > TamanhoMaximo) throw new ValidacaoException($"O arquivo excede o limite de {TamanhoMaximo / 1024 / 1024} MB.");
        }
        var bytes = ms.ToArray();
        Validar(nome, bytes.Length, bytes.AsSpan(0, Math.Min(8, bytes.Length)));

        var chave = $"{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}.bin";
        var caminho = Path.Combine(_dir, chave.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        // pasta de rede: grava como .tmp e renomeia, com retentativa — ninguém enxerga arquivo pela metade
        var tmp = caminho + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        ParquetStore.ComRetentativa(() => { File.Move(tmp, caminho, true); return 0; });

        var ext = Path.GetExtension(nome);
        return _db.Anexos.Salvar(new Anexo
        {
            DonoTipo = donoTipo, DonoId = donoId, NomeOriginal = nome, ContentType = Tipos[ext], Tamanho = bytes.Length,
            Chave = chave, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), EnviadoPor = ator.Login,
        }, ator.Login);
    }

    public Stream? Abrir(Anexo a)
    {
        var caminho = Path.GetFullPath(Path.Combine(_dir, a.Chave.Replace('/', Path.DirectorySeparatorChar)));
        if (!caminho.StartsWith(Path.GetFullPath(_dir), StringComparison.Ordinal)) return null;   // sem path traversal
        return File.Exists(caminho) ? ParquetStore.ComRetentativa(() => (Stream)new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) : null;
    }

    public void Remover(Anexo a)
    {
        var caminho = Path.GetFullPath(Path.Combine(_dir, a.Chave.Replace('/', Path.DirectorySeparatorChar)));
        if (caminho.StartsWith(Path.GetFullPath(_dir), StringComparison.Ordinal) && File.Exists(caminho))
        {
            // não apaga na hora: vai para _historico/arquivos (recuperável por engano de exclusão)
            try
            {
                var h = Path.Combine(_db.Store.Folder, "_historico", "arquivos", DateTime.UtcNow.ToString("yyyyMMdd"));
                Directory.CreateDirectory(h);
                File.Move(caminho, Path.Combine(h, Path.GetFileName(caminho)), true);
            }
            catch { File.Delete(caminho); }
        }
        _db.Anexos.Apagar(a.Id);
    }

    public void LimparTudo()
    {
        _db.Anexos.Limpar();
        if (Directory.Exists(_dir))
        {
            var h = Path.Combine(_db.Store.Folder, "_historico", "arquivos", "limpeza_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(h);
            foreach (var f in Directory.GetFiles(_dir, "*.bin", SearchOption.AllDirectories)) try { File.Move(f, Path.Combine(h, Path.GetFileName(f)), true); } catch { }
        }
    }
}

using System.Security.Claims;
using ControleTecnico.Models;

namespace ControleTecnico.Logic;

public enum Perm
{
    VerEquipe, VerContatos, EditarTecnicos, EditarClientes, EditarViagens, VerReservas, VerAnexosViagem,
    EditarAgenda, VerDocsConteudo, VerDocsStatus, EditarDocs, EditarRequisitos, ConfirmarLocal,
    Administrar, VerAuditoria,
}

/// <summary>Quem está operando: papel + (para o técnico) a que ficha está ligado.
/// Todas as regras de permissão do servidor passam por aqui.</summary>
public sealed class Ator
{
    public string Login { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Papel { get; init; } = Roles.Consulta;
    public string TecnicoId { get; init; } = "";

    public static readonly Ator Sistema = new() { Login = "sistema", Nome = "Sistema", Papel = Roles.Admin };

    private static readonly Dictionary<Perm, string[]> Matriz = new()
    {
        [Perm.VerEquipe] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria, Roles.Consulta },
        [Perm.VerContatos] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.EditarTecnicos] = new[] { Roles.Admin, Roles.Gestao },
        [Perm.EditarClientes] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.EditarViagens] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.VerReservas] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.VerAnexosViagem] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.EditarAgenda] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.VerDocsConteudo] = new[] { Roles.Admin, Roles.Gestao },
        [Perm.VerDocsStatus] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria, Roles.Consulta },
        [Perm.EditarDocs] = new[] { Roles.Admin, Roles.Gestao },
        [Perm.EditarRequisitos] = new[] { Roles.Admin, Roles.Gestao },
        [Perm.ConfirmarLocal] = new[] { Roles.Admin, Roles.Gestao, Roles.Controladoria },
        [Perm.Administrar] = new[] { Roles.Admin },
        [Perm.VerAuditoria] = new[] { Roles.Admin, Roles.Gestao },
    };

    /// <summary>Permissão geral (sem referência a um técnico específico).</summary>
    public bool Pode(Perm p) => Matriz.TryGetValue(p, out var papeis) && papeis.Contains(Papel);

    /// <summary>Permissão sobre os dados de UM técnico: o papel Técnico só vale na própria ficha.</summary>
    public bool Pode(Perm p, string? tecnicoId)
    {
        if (Pode(p)) return true;
        if (Papel != Roles.Tecnico || string.IsNullOrEmpty(TecnicoId) || TecnicoId != tecnicoId) return false;
        return p is Perm.VerEquipe or Perm.VerContatos or Perm.VerDocsConteudo or Perm.VerDocsStatus
            or Perm.EditarDocs or Perm.ConfirmarLocal or Perm.VerReservas or Perm.VerAnexosViagem;
    }

    /// <summary>Lança se não puder — usado pelas operações de escrita.</summary>
    public void Exigir(Perm p, string? tecnicoId = null)
    {
        if (!Pode(p, tecnicoId))
            throw new UnauthorizedAccessException($"O perfil {Roles.Label(Papel)} não tem permissão para esta operação.");
    }

    /// <summary>O técnico enxerga só a própria agenda; os demais perfis enxergam a equipe.</summary>
    public bool VeTodaEquipe => Pode(Perm.VerEquipe);

    public bool VeTecnico(string tecnicoId) => VeTodaEquipe || (Papel == Roles.Tecnico && TecnicoId == tecnicoId);

    public static Ator De(ClaimsPrincipal? u)
    {
        if (u?.Identity is not { IsAuthenticated: true }) return new Ator();
        return new Ator
        {
            Login = u.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
            Nome = u.Identity?.Name ?? "",
            Papel = Roles.Normalize(u.FindFirst(ClaimTypes.Role)?.Value),
            TecnicoId = u.FindFirst("tecnico")?.Value ?? "",
        };
    }
}

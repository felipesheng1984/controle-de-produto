using Microsoft.EntityFrameworkCore;

namespace Produtos.Data;

// Banco simples do "Controle de Produtos":
//  - Colunas  = os itens com "打勾" (checkbox) que o usuário pode adicionar/remover.
//  - Coletas  = as linhas (Gru / Cidade / Data são texto livre).
//  - Checks   = quais caixas estão marcadas (existe linha = marcado ✓).
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Coluna> Colunas => Set<Coluna>();
    public DbSet<Coleta> Coletas => Set<Coleta>();
    public DbSet<Check> Checks => Set<Check>();
    public DbSet<CellText> CellTexts => Set<CellText>();
    public DbSet<Pessoa> Pessoas => Set<Pessoa>();
    public DbSet<ColetaPessoa> ColetaPessoas => Set<ColetaPessoa>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Pessoa>().ToTable("pessoas");
        b.Entity<ColetaPessoa>().ToTable("coleta_pessoas");
        b.Entity<ColetaPessoa>().HasIndex(c => new { c.ColetaId, c.PessoaId }).IsUnique();
        b.Entity<Coluna>().ToTable("colunas");
        b.Entity<Coleta>().ToTable("coletas");
        b.Entity<Check>().ToTable("checks");
        b.Entity<CellText>().ToTable("cell_texts");
        // Marca é única por (coleta, coluna): presença = ✓.
        b.Entity<Check>().HasIndex(c => new { c.ColetaId, c.ColunaId }).IsUnique();
        // Texto é único por (coleta, coluna).
        b.Entity<CellText>().HasIndex(c => new { c.ColetaId, c.ColunaId }).IsUnique();
    }
}

// Coluna definida pelo usuário. Type = "check" (打勾) ou "text" (填字, ex.:备注).
public class Coluna
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "check";
    public int SortOrder { get; set; }
    public string CreatedAt { get; set; } = "";
}

// Uma linha da tabela. Empresa(廠區) / Gru / Cidade / Data são texto livre.
public class Coleta
{
    public string Id { get; set; } = "";
    public string Empresa { get; set; } = "";   // 廠區 / fábrica de onde veio a coleta
    public string Gru { get; set; } = "";
    public string Cidade { get; set; } = "";
    public string Data { get; set; } = "";
    public int SortOrder { get; set; }
    public string CreatedAt { get; set; } = "";
}

// Marca de uma caixa: se existir a linha (ColetaId+ColunaId), está ✓.
public class Check
{
    public int Id { get; set; }
    public string ColetaId { get; set; } = "";
    public string ColunaId { get; set; } = "";
}

// Valor de texto de uma célula (para colunas do tipo "text", ex.: 备注).
public class CellText
{
    public int Id { get; set; }
    public string ColetaId { get; set; } = "";
    public string ColunaId { get; set; } = "";
    public string Valor { get; set; } = "";
}

// Pessoa (op / operador de viagem). Usada para atribuir a coletas e estatísticas.
public class Pessoa
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
    public bool Ativo { get; set; } = true;
    public int SortOrder { get; set; }
    public string CreatedAt { get; set; } = "";
}

// Vínculo N:N entre coleta e pessoa (quem foi nesta viagem).
public class ColetaPessoa
{
    public int Id { get; set; }
    public string ColetaId { get; set; } = "";
    public string PessoaId { get; set; } = "";
}

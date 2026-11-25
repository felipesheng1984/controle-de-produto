using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Produtos.Data;

var builder = WebApplication.CreateBuilder(args);

// ---- Banco SQLite em AppData (sobrevive a atualizações do app) ----
// %LOCALAPPDATA%\Re-Teck\ControleProdutos\produtos.db  (ou DATABASE_URL).
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
string dbPath;
if (!string.IsNullOrEmpty(dbUrl))
{
    dbPath = dbUrl.StartsWith("file:") ? dbUrl["file:".Length..] : dbUrl;
    if (!Path.IsPathRooted(dbPath))
        dbPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, dbPath));
}
else
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    dbPath = Path.Combine(appData, "Re-Teck", "ControleProdutos", "produtos.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
// Pasta das fotos das coletas: <pasta do db>/photos/<coletaId>/
string photosRoot = Path.Combine(Path.GetDirectoryName(dbPath)!, "photos");
Directory.CreateDirectory(photosRoot);

var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "5011");
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

// JSON camelCase (o front usa gru, cidade, sort_order… em camelCase).
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Cria tabelas (idempotente) + semeia colunas de exemplo na 1ª vez.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // Upgrade de bancos ANTIGOS (EnsureCreated não altera tabelas já existentes):
    //  - coluna colunas.Type (default 'check')
    //  - tabela cell_texts (valores de texto das colunas tipo "text")
    var cols = db.Database.SqlQueryRaw<string>(
        "SELECT name AS Value FROM pragma_table_info('colunas')").ToList();
    if (!cols.Contains("Type"))
        db.Database.ExecuteSqlRaw("ALTER TABLE \"colunas\" ADD COLUMN \"Type\" TEXT NOT NULL DEFAULT 'check'");
    var colColetas = db.Database.SqlQueryRaw<string>(
        "SELECT name AS Value FROM pragma_table_info('coletas')").ToList();
    if (!colColetas.Contains("Empresa"))
        db.Database.ExecuteSqlRaw("ALTER TABLE \"coletas\" ADD COLUMN \"Empresa\" TEXT NOT NULL DEFAULT ''");
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"cell_texts\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_cell_texts\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ColetaId\" TEXT NOT NULL, \"ColunaId\" TEXT NOT NULL, \"Valor\" TEXT NOT NULL); " +
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_cell_texts_ColetaId_ColunaId\" " +
        "ON \"cell_texts\" (\"ColetaId\", \"ColunaId\");");
    // Pessoas (op) e vínculo N:N com coletas.
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"pessoas\" (" +
        "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_pessoas\" PRIMARY KEY, " +
        "\"Nome\" TEXT NOT NULL, \"Ativo\" INTEGER NOT NULL DEFAULT 1, " +
        "\"SortOrder\" INTEGER NOT NULL DEFAULT 0, \"CreatedAt\" TEXT NOT NULL DEFAULT ''); " +
        "CREATE TABLE IF NOT EXISTS \"coleta_pessoas\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_coleta_pessoas\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ColetaId\" TEXT NOT NULL, \"PessoaId\" TEXT NOT NULL); " +
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_coleta_pessoas_ColetaId_PessoaId\" " +
        "ON \"coleta_pessoas\" (\"ColetaId\", \"PessoaId\");");

    // Converte datas antigas "dd/mm" (sem ano) para ISO "yyyy-MM-dd" (ano atual),
    // para o seletor de data e as estatísticas funcionarem. Idempotente.
    var anoAtual = DateTime.Now.Year;
    var comData = db.Coletas.Where(c => c.Data != "").ToList();
    var mudou = false;
    foreach (var c in comData)
    {
        var m = c.Data.Split('/');
        if (m.Length == 2 && int.TryParse(m[0], out var dd) && int.TryParse(m[1], out var mm)
            && dd >= 1 && dd <= 31 && mm >= 1 && mm <= 12)
        {
            c.Data = $"{anoAtual:D4}-{mm:D2}-{dd:D2}";
            mudou = true;
        }
    }
    if (mudou) db.SaveChanges();

    if (!db.Colunas.Any())
    {
        var exemplos = new[] { "Memoria", "CPU", "Placa", "SFP", "Outros" };
        for (var i = 0; i < exemplos.Length; i++)
            db.Colunas.Add(new Coluna { Id = NewId(), Name = exemplos[i], Type = "check", SortOrder = i, CreatedAt = Now() });
        db.SaveChanges();
    }
}

// Erros viram JSON legível pelo front.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
});

// HTML sem cache (pega sempre a versão nova); resto é estático normal.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});

var api = app.MapGroup("/api");

// ---- Estado completo (colunas + linhas com suas marcas) ----
api.MapGet("/state", async (AppDbContext db) =>
{
    var colunas = await db.Colunas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
        .Select(c => new { c.Id, c.Name, c.Type, sortOrder = c.SortOrder }).ToListAsync();
    var coletas = await db.Coletas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();
    var checks = await db.Checks.ToListAsync();
    var byColeta = checks.GroupBy(c => c.ColetaId)
        .ToDictionary(g => g.Key, g => g.Select(x => x.ColunaId).ToList());
    var texts = await db.CellTexts.ToListAsync();
    var textByColeta = texts.GroupBy(t => t.ColetaId)
        .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.ColunaId, x => x.Valor));
    var links = await db.ColetaPessoas.ToListAsync();
    var pessoasByColeta = links.GroupBy(l => l.ColetaId)
        .ToDictionary(g => g.Key, g => g.Select(x => x.PessoaId).ToList());
    var pessoas = await db.Pessoas.Where(p => p.Ativo).OrderBy(p => p.SortOrder).ThenBy(p => p.Nome)
        .Select(p => new { p.Id, p.Nome }).ToListAsync();
    var linhas = coletas.Select(c =>
    {
        var pdir = Path.Combine(photosRoot, SafeId(c.Id));
        var fotos = Directory.Exists(pdir) ? Directory.GetFiles(pdir).Length : 0;
        return new
        {
            c.Id, c.Empresa, c.Gru, c.Cidade, c.Data, sortOrder = c.SortOrder,
            checks = byColeta.TryGetValue(c.Id, out var list) ? list : new List<string>(),
            texts = textByColeta.TryGetValue(c.Id, out var tmap) ? tmap : new Dictionary<string, string>(),
            pessoas = pessoasByColeta.TryGetValue(c.Id, out var plist) ? plist : new List<string>(),
            fotos
        };
    });
    return Results.Ok(new { colunas, coletas = linhas, pessoas });
});

// ---- Colunas (打勾 ou 填字) ----
api.MapPost("/colunas", async (AppDbContext db, ColunaBody body) =>
{
    var name = (body.Name ?? "").Trim();
    if (name.Length == 0) return Results.BadRequest(new { message = "Nome vazio." });
    var type = body.Type == "text" ? "text" : "check";
    var next = (db.Colunas.Any() ? db.Colunas.Max(c => c.SortOrder) : -1) + 1;
    var col = new Coluna { Id = NewId(), Name = name, Type = type, SortOrder = next, CreatedAt = Now() };
    db.Colunas.Add(col);
    await db.SaveChangesAsync();
    return Results.Ok(new { col.Id, col.Name, col.Type, sortOrder = col.SortOrder });
});

api.MapPost("/colunas/rename", async (AppDbContext db, RenameBody body) =>
{
    var col = await db.Colunas.FindAsync(body.Id);
    if (col == null) return Results.NotFound();
    col.Name = (body.Name ?? "").Trim();
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Reordenar colunas (arrastar): recebe os ids na ordem desejada.
api.MapPost("/colunas/reorder", async (AppDbContext db, ReorderBody body) =>
{
    var cols = await db.Colunas.ToListAsync();
    var map = cols.ToDictionary(c => c.Id);
    for (var i = 0; i < body.Ids.Length; i++)
        if (map.TryGetValue(body.Ids[i], out var c)) c.SortOrder = i;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Trocar o tipo de uma coluna: "check" (打勾) <-> "text" (填字).
api.MapPost("/colunas/type", async (AppDbContext db, ColunaTypeBody body) =>
{
    var col = await db.Colunas.FindAsync(body.Id);
    if (col == null) return Results.NotFound();
    col.Type = body.Type == "text" ? "text" : "check";
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

api.MapPost("/colunas/remove", async (AppDbContext db, IdBody body) =>
{
    var col = await db.Colunas.FindAsync(body.Id);
    if (col == null) return Results.NotFound();
    db.Colunas.Remove(col);
    db.Checks.RemoveRange(db.Checks.Where(c => c.ColunaId == body.Id));
    db.CellTexts.RemoveRange(db.CellTexts.Where(c => c.ColunaId == body.Id));
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---- Coletas (linhas) ----
api.MapPost("/coletas", async (AppDbContext db, ColetaNewBody? body) =>
{
    // Nova coleta vai para o TOPO (SortOrder menor que todas), para não precisar rolar.
    // Se um filtro de廠區 estiver ativo, já nasce com essa empresa.
    var top = (db.Coletas.Any() ? db.Coletas.Min(c => c.SortOrder) : 0) - 1;
    var c = new Coleta { Id = NewId(), Empresa = body?.Empresa ?? "", Gru = "", Cidade = "", Data = "", SortOrder = top, CreatedAt = Now() };
    db.Coletas.Add(c);
    await db.SaveChangesAsync();
    return Results.Ok(new { c.Id, c.Empresa, c.Gru, c.Cidade, c.Data, sortOrder = c.SortOrder, checks = new List<string>() });
});

// Reordenar linhas (arrastar): recebe os ids na ordem desejada.
api.MapPost("/coletas/reorder", async (AppDbContext db, ReorderBody body) =>
{
    var rows = await db.Coletas.ToListAsync();
    var map = rows.ToDictionary(c => c.Id);
    for (var i = 0; i < body.Ids.Length; i++)
        if (map.TryGetValue(body.Ids[i], out var c)) c.SortOrder = i;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

api.MapPost("/coletas/update", async (AppDbContext db, ColetaUpdateBody body) =>
{
    var c = await db.Coletas.FindAsync(body.Id);
    if (c == null) return Results.NotFound();
    c.Empresa = body.Empresa ?? "";
    c.Gru = body.Gru ?? "";
    c.Cidade = body.Cidade ?? "";
    c.Data = body.Data ?? "";
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

api.MapPost("/coletas/remove", async (AppDbContext db, IdBody body) =>
{
    var c = await db.Coletas.FindAsync(body.Id);
    if (c == null) return Results.NotFound();
    db.Coletas.Remove(c);
    db.Checks.RemoveRange(db.Checks.Where(x => x.ColetaId == body.Id));
    db.CellTexts.RemoveRange(db.CellTexts.Where(x => x.ColetaId == body.Id));
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---- Marcar/desmarcar uma caixa (✓) — colunas tipo "check" ----
api.MapPost("/toggle", async (AppDbContext db, ToggleBody body) =>
{
    var existing = await db.Checks
        .FirstOrDefaultAsync(c => c.ColetaId == body.ColetaId && c.ColunaId == body.ColunaId);
    bool chec;
    if (existing != null) { db.Checks.Remove(existing); chec = false; }
    else { db.Checks.Add(new Check { ColetaId = body.ColetaId, ColunaId = body.ColunaId }); chec = true; }
    await db.SaveChangesAsync();
    return Results.Ok(new { marcado = chec });
});

// ---- Gravar o texto de uma célula — colunas tipo "text" (备注) ----
api.MapPost("/cell", async (AppDbContext db, CellBody body) =>
{
    var valor = body.Valor ?? "";
    var existing = await db.CellTexts
        .FirstOrDefaultAsync(c => c.ColetaId == body.ColetaId && c.ColunaId == body.ColunaId);
    if (string.IsNullOrEmpty(valor))
    {
        if (existing != null) db.CellTexts.Remove(existing);
    }
    else if (existing != null) { existing.Valor = valor; }
    else { db.CellTexts.Add(new CellText { ColetaId = body.ColetaId, ColunaId = body.ColunaId, Valor = valor }); }
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---- Pessoas (op / lista de pessoal) ----
api.MapGet("/pessoas", async (AppDbContext db) =>
    Results.Ok(await db.Pessoas.OrderBy(p => p.SortOrder).ThenBy(p => p.Nome)
        .Select(p => new { p.Id, p.Nome, p.Ativo }).ToListAsync()));

api.MapPost("/pessoas", async (AppDbContext db, NameBody body) =>
{
    var nome = (body.Name ?? "").Trim();
    if (nome.Length == 0) return Results.BadRequest(new { message = "Nome vazio." });
    var next = (db.Pessoas.Any() ? db.Pessoas.Max(p => p.SortOrder) : -1) + 1;
    var p = new Pessoa { Id = NewId(), Nome = nome, Ativo = true, SortOrder = next, CreatedAt = Now() };
    db.Pessoas.Add(p);
    await db.SaveChangesAsync();
    return Results.Ok(new { p.Id, p.Nome, p.Ativo });
});

api.MapPost("/pessoas/rename", async (AppDbContext db, RenameBody body) =>
{
    var p = await db.Pessoas.FindAsync(body.Id);
    if (p == null) return Results.NotFound();
    p.Nome = (body.Name ?? "").Trim();
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

api.MapPost("/pessoas/remove", async (AppDbContext db, IdBody body) =>
{
    var p = await db.Pessoas.FindAsync(body.Id);
    if (p == null) return Results.NotFound();
    db.Pessoas.Remove(p);
    db.ColetaPessoas.RemoveRange(db.ColetaPessoas.Where(x => x.PessoaId == body.Id));
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Definir quais pessoas (op) foram numa coleta (substitui a seleção).
api.MapPost("/coletas/pessoas", async (AppDbContext db, ColetaPessoasBody body) =>
{
    db.ColetaPessoas.RemoveRange(db.ColetaPessoas.Where(x => x.ColetaId == body.ColetaId));
    foreach (var pid in (body.PessoaIds ?? Array.Empty<string>()).Distinct())
        db.ColetaPessoas.Add(new ColetaPessoa { ColetaId = body.ColetaId, PessoaId = pid });
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---- Estatísticas: nº de viagens por op no ciclo (dia 16 → dia 15 do mês seguinte) ----
api.MapGet("/stats", async (AppDbContext db, int year, int month) =>
{
    var start = new DateTime(year, month, 16);
    var end = start.AddMonths(1).AddDays(-1); // dia 15 do mês seguinte
    var coletas = await db.Coletas.ToListAsync();
    var inRange = new HashSet<string>();
    foreach (var c in coletas)
        if (DateTime.TryParseExact(c.Data, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt) && dt >= start && dt <= end)
            inRange.Add(c.Id);
    var links = await db.ColetaPessoas.ToListAsync();
    var pessoas = await db.Pessoas.OrderBy(p => p.SortOrder).ThenBy(p => p.Nome).ToListAsync();
    var rows = pessoas.Select(p => new
    {
        p.Id, p.Nome,
        count = links.Count(l => l.PessoaId == p.Id && inRange.Contains(l.ColetaId))
    });
    return Results.Ok(new
    {
        start = start.ToString("yyyy-MM-dd"),
        end = end.ToString("yyyy-MM-dd"),
        total = inRange.Count,
        pessoas = rows
    });
});

// ---- Análise: por Empresa(廠區) ou por Gru, quantas coletas trouxeram cada item ----
api.MapGet("/analysis", async (AppDbContext db, string by) =>
{
    var coletas = await db.Coletas.ToListAsync();
    var checkCols = await db.Colunas.Where(c => c.Type == "check")
        .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();
    var checks = await db.Checks.ToListAsync();
    var checksByColeta = checks.GroupBy(c => c.ColetaId)
        .ToDictionary(g => g.Key, g => g.Select(x => x.ColunaId).ToHashSet());

    Func<Coleta, string> keyOf = by == "gru"
        ? (c => (c.Gru ?? "").Trim())
        : (c => (c.Empresa ?? "").Trim());

    var grupos = coletas.GroupBy(keyOf).OrderBy(g => g.Key).Select(g => new
    {
        key = g.Key,
        total = g.Count(),
        counts = checkCols.ToDictionary(col => col.Id,
            col => g.Count(x => checksByColeta.TryGetValue(x.Id, out var s) && s.Contains(col.Id)))
    });
    return Results.Ok(new
    {
        by,
        colunas = checkCols.Select(c => new { c.Id, c.Name }),
        grupos
    });
});

// ---- Fotos da coleta (1 a 10 por linha), gravadas em <db>/photos/<coletaId>/ ----
static string SafeId(string? id) =>
    new string((id ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());

api.MapGet("/photos", (HttpContext ctx, string coletaId) =>
{
    var dir = Path.Combine(photosRoot, SafeId(coletaId));
    if (SafeId(coletaId).Length == 0 || !Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
    var files = Directory.GetFiles(dir).OrderBy(x => x)
        .Select(x => new { name = Path.GetFileName(x) }).ToList();
    return Results.Ok(files);
});

api.MapGet("/photos/file", (string coletaId, string name) =>
{
    var path = Path.Combine(photosRoot, SafeId(coletaId), Path.GetFileName(name ?? ""));
    if (!File.Exists(path)) return Results.NotFound();
    var ext = Path.GetExtension(path).ToLowerInvariant();
    var ct = ext == ".png" ? "image/png" : (ext == ".webp" ? "image/webp" : "image/jpeg");
    return Results.File(path, ct);
});

api.MapPost("/photos", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var safeId = SafeId(form["coletaId"].ToString());
    var file = form.Files.Count > 0 ? form.Files[0] : null;
    if (safeId.Length == 0 || file == null || file.Length == 0)
        return Results.BadRequest(new { message = "coletaId e foto obrigatórios." });
    var dir = Path.Combine(photosRoot, safeId);
    Directory.CreateDirectory(dir);
    if (Directory.GetFiles(dir).Length >= 10)
        return Results.BadRequest(new { message = "Máximo de 10 fotos por coleta." });
    var ext = new string((Path.GetExtension(file.FileName ?? "") ?? "").Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());
    if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
    var pname = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ext;
    await using (var fs = File.Create(Path.Combine(dir, pname))) await file.CopyToAsync(fs);
    return Results.Ok(new { ok = true, name = pname });
}).DisableAntiforgery();

api.MapPost("/photos/remove", (PhotoRemoveBody body) =>
{
    var path = Path.Combine(photosRoot, SafeId(body.ColetaId), Path.GetFileName(body.Name ?? ""));
    if (File.Exists(path)) File.Delete(path);
    return Results.Ok(new { ok = true });
});

// ---- Relatório mensal (ciclo dia 16 → dia 15) ----
// Junta num só JSON tudo que a página de relatório precisa: resumo, por empresa,
// por item, por pessoa, comparação com o ciclo anterior e o detalhe das coletas.
api.MapGet("/report", async (AppDbContext db, int year, int month) =>
{
    var start = new DateTime(year, month, 16);
    var end = start.AddMonths(1).AddDays(-1);          // dia 15 do mês seguinte
    var prevStart = start.AddMonths(-1);
    var prevEnd = start.AddDays(-1);

    var colunas = await db.Colunas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();
    var checkCols = colunas.Where(c => c.Type == "check").ToList();
    var textCols = colunas.Where(c => c.Type == "text").ToList();
    var todas = await db.Coletas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();

    static DateTime? Parse(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;

    var coletas = todas.Where(c => Parse(c.Data) is DateTime d && d >= start && d <= end)
                       .OrderBy(c => c.Data).ToList();
    var anteriores = todas.Where(c => Parse(c.Data) is DateTime d && d >= prevStart && d <= prevEnd).ToList();

    var ids = coletas.Select(c => c.Id).ToHashSet();
    var checksByColeta = (await db.Checks.ToListAsync())
        .GroupBy(c => c.ColetaId).ToDictionary(g => g.Key, g => g.Select(x => x.ColunaId).ToHashSet());
    var textMap = (await db.CellTexts.ToListAsync())
        .ToDictionary(t => t.ColetaId + "|" + t.ColunaId, t => t.Valor);
    var pessoas = await db.Pessoas.OrderBy(p => p.SortOrder).ThenBy(p => p.Nome).ToListAsync();
    var pessoasById = pessoas.ToDictionary(p => p.Id, p => p.Nome);
    var links = await db.ColetaPessoas.ToListAsync();
    var pessoasByColeta = links.GroupBy(l => l.ColetaId)
        .ToDictionary(g => g.Key, g => g.Select(x => pessoasById.TryGetValue(x.PessoaId, out var n) ? n : "")
                                        .Where(n => n != "").ToList());

    int Marcados(Coleta c) => checksByColeta.TryGetValue(c.Id, out var s)
        ? checkCols.Count(col => s.Contains(col.Id)) : 0;
    bool Tem(Coleta c, string colunaId) => checksByColeta.TryGetValue(c.Id, out var s) && s.Contains(colunaId);

    // Agrupamento genérico (por empresa ou por gru), com total do ciclo anterior para comparar.
    object[] Agrupar(Func<Coleta, string> keyOf) =>
        coletas.GroupBy(keyOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (object)new
        {
            key = g.Key,
            total = g.Count(),
            itens = g.Sum(Marcados),
            prevTotal = anteriores.Count(c => keyOf(c) == g.Key),
            counts = checkCols.ToDictionary(col => col.Id, col => g.Count(x => Tem(x, col.Id)))
        }).ToArray();

    var porEmpresa = Agrupar(c => (c.Empresa ?? "").Trim());
    var porGru = Agrupar(c => (c.Gru ?? "").Trim());

    var totalItens = coletas.Sum(Marcados);
    var porItem = checkCols.Select(col => new
    {
        col.Id, col.Name,
        count = coletas.Count(c => Tem(c, col.Id)),
        prev = anteriores.Count(c => Tem(c, col.Id))
    }).OrderByDescending(x => x.count).ToArray();

    var porPessoa = pessoas.Select(p => new
    {
        p.Nome,
        count = links.Count(l => l.PessoaId == p.Id && ids.Contains(l.ColetaId)),
        prev = links.Count(l => l.PessoaId == p.Id && anteriores.Any(c => c.Id == l.ColetaId))
    }).Where(x => x.count > 0 || x.prev > 0).OrderByDescending(x => x.count).ToArray();

    var detalhe = coletas.Select(c => new
    {
        c.Empresa, c.Gru, c.Cidade, c.Data,
        pessoas = pessoasByColeta.TryGetValue(c.Id, out var ps) ? ps : new List<string>(),
        itens = checkCols.Where(col => Tem(c, col.Id)).Select(col => col.Name).ToArray(),
        textos = textCols.Select(col => new
                 { col.Name, valor = textMap.TryGetValue(c.Id + "|" + col.Id, out var v) ? v : "" })
                 .Where(x => x.valor != "").ToArray()
    }).ToArray();

    return Results.Ok(new
    {
        year, month,
        start = start.ToString("yyyy-MM-dd"),
        end = end.ToString("yyyy-MM-dd"),
        prevStart = prevStart.ToString("yyyy-MM-dd"),
        prevEnd = prevEnd.ToString("yyyy-MM-dd"),
        geradoEm = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        resumo = new
        {
            totalColetas = coletas.Count,
            prevColetas = anteriores.Count,
            empresas = coletas.Select(c => (c.Empresa ?? "").Trim()).Where(v => v != "").Distinct().Count(),
            cidades = coletas.Select(c => (c.Cidade ?? "").Trim()).Where(v => v != "").Distinct().Count(),
            pessoas = porPessoa.Count(p => p.count > 0),
            itens = totalItens,
            prevItens = anteriores.Sum(Marcados)
        },
        colunas = checkCols.Select(c => new { c.Id, c.Name }),
        porEmpresa, porGru, porItem, porPessoa, detalhe
    });
});

// ---- Exportar para Excel ----
api.MapGet("/export", async (AppDbContext db) =>
{
    var colunas = await db.Colunas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();
    var coletas = await db.Coletas.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync();
    var checkSet = (await db.Checks.ToListAsync())
        .Select(c => c.ColetaId + "|" + c.ColunaId).ToHashSet();
    var textMap = (await db.CellTexts.ToListAsync())
        .ToDictionary(t => t.ColetaId + "|" + t.ColunaId, t => t.Valor);
    var pessoasById = (await db.Pessoas.ToListAsync()).ToDictionary(p => p.Id, p => p.Nome);
    var pessoasByColeta = (await db.ColetaPessoas.ToListAsync())
        .GroupBy(l => l.ColetaId)
        .ToDictionary(g => g.Key, g => string.Join(", ",
            g.Select(x => pessoasById.TryGetValue(x.PessoaId, out var n) ? n : "").Where(n => n != "")));

    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("Coletas");
    string[] fixos = { "Empresa", "Gru", "Cidade", "Data", "Pessoas" };
    for (var i = 0; i < fixos.Length; i++) ws.Cell(1, i + 1).Value = fixos[i];
    var baseCol = fixos.Length; // colunas de itens começam depois dos fixos
    for (var i = 0; i < colunas.Count; i++) ws.Cell(1, baseCol + 1 + i).Value = colunas[i].Name;
    var r = 2;
    foreach (var c in coletas)
    {
        ws.Cell(r, 1).Value = c.Empresa;
        ws.Cell(r, 2).Value = c.Gru;
        ws.Cell(r, 3).Value = c.Cidade;
        ws.Cell(r, 4).Value = c.Data;
        ws.Cell(r, 5).Value = pessoasByColeta.TryGetValue(c.Id, out var pn) ? pn : "";
        for (var i = 0; i < colunas.Count; i++)
        {
            var key = c.Id + "|" + colunas[i].Id;
            ws.Cell(r, baseCol + 1 + i).Value = colunas[i].Type == "text"
                ? (textMap.TryGetValue(key, out var v) ? v : "")
                : (checkSet.Contains(key) ? "✓" : "");
        }
        r++;
    }
    ws.Row(1).Style.Font.Bold = true;
    ws.Columns().AdjustToContents();
    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    return Results.File(ms.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "coletas.xlsx");
});

app.Run();
return 0;

static string NewId() => Guid.NewGuid().ToString("n");
static string Now() => DateTime.UtcNow.ToString("o");

// ---- Corpos de requisição (camelCase JSON) ----
public record NameBody(string Name);
public record IdBody(string Id);
public record RenameBody(string Id, string Name);
public record ColunaBody(string Name, string? Type);
public record ColunaTypeBody(string Id, string Type);
public record ReorderBody(string[] Ids);
public record ColetaNewBody(string? Empresa);
public record ColetaUpdateBody(string Id, string Empresa, string Gru, string Cidade, string Data);
public record ToggleBody(string ColetaId, string ColunaId);
public record CellBody(string ColetaId, string ColunaId, string Valor);
public record ColetaPessoasBody(string ColetaId, string[] PessoaIds);
public record PhotoRemoveBody(string ColetaId, string Name);

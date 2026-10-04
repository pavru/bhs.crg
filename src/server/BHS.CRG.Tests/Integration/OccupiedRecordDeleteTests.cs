using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Data;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Удалить занятое нельзя (задача G2, ТЗ CORE-34.1, CORE-34.2; issue #1094, #1168): запись ядра, на
/// которую ссылаются данные модуля, не удаляется — и отказ называет, кто держит.
///
/// <para>Проверяется на настоящих счетах — путь человека, от адреса до отказа, — и на подставной
/// схеме: держателей находит скан базы, и о модулях по имени он знать не должен.</para>
/// </summary>
[Collection("Integration")]
public class OccupiedRecordDeleteTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    // ── Сторож задачи: позиция номенклатуры в строке счёта ─────────────────────

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Позицию номенклатуры, стоящую в строке счёта, удалить нельзя, и отказ называет,
    /// кто держит: модуль, что именно, сколько и в каком счёте. Убрали строку — удаление проходит:
    /// иначе правило значило бы «не удаляется никогда».
    /// </summary>
    [Fact]
    public async Task Позицию_номенклатуры_в_строке_счёта_удалить_нельзя()
    {
        var (client, _) = await SignInAsync("Admin");
        var position = await OwnPositionAsync();
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(position, quantity: 40, price: 10)]);
        var number = (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Номер").GetString();

        var refusal = await client.DeleteAsync($"/api/common-data/{position}");

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var text = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        Assert.Contains("«Счета и накладные»", text);
        Assert.Contains("строки счетов с этой позицией номенклатуры — 1", text);
        Assert.Contains(number!, text); // право читать счета есть — счёт назван
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{position}")).StatusCode);

        await LinesAsync(client, invoice, []);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/common-data/{position}")).StatusCode);
    }

    /// <summary>
    /// Число держателей видно всем, а номер счёта и адрес таблицы — нет (решение владельца
    /// 04.10.2026): номер — содержимое модуля, адрес — устройство базы. Здесь спрашивают вне запроса,
    /// то есть без единого права: ответ «держат» от прав не зависит, слова — зависят.
    /// </summary>
    [Fact]
    public async Task Без_прав_отказ_называет_число_но_не_счёт_и_не_таблицу()
    {
        var (client, _) = await SignInAsync("Admin");
        var position = await OwnPositionAsync();
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(position, quantity: 1, price: 10)]);
        var number = (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Номер").GetString()!;

        using var scope = host.Services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IRecordHolders>().FindAsync([position]);

        var line = Assert.Single(found.Lines);
        Assert.Contains("строки счетов с этой позицией номенклатуры — 1", line);
        Assert.DoesNotContain(number, line);
        Assert.DoesNotContain("invoice_lines", line);
    }

    /// <summary>
    /// При ВЫКЛЮЧЕННОМ модуле ответ тот же (ТЗ AUTH-19, CORE-34.2): его данные на месте и держат так
    /// же. Модуль выключается здесь составом реестра, а не перезапуском хоста: проверяется, что ответ
    /// не зависит от того, зарегистрированы ли службы модуля, — держателей находит скан базы.
    /// </summary>
    [Fact]
    public async Task Выключенный_модуль_держит_так_же()
    {
        var (client, _) = await SignInAsync("Admin");
        var position = await OwnPositionAsync();
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(position, quantity: 3, price: 10)]);

        using var scope = host.Services.CreateScope();
        var switchedOff = HoldersWith(scope, new ModuleRegistry([], [new CostsModule()]));

        var found = await switchedOff.FindAsync([position]);

        Assert.Equal([position], (await switchedOff.HeldAsync([position])).Ids);
        Assert.Contains("«Счета и накладные» (модуль выключен)", Assert.Single(found.Lines));
        Assert.Throws<ConflictException>(() => found.EnsureNone("запись"));
    }

    // ── Остальные пути удаления ────────────────────────────────────────────────

    /// <summary>
    /// Стройку и раздел, на которые разнесён счёт, удалить нельзя: каскад уровня уносил бы их из-под
    /// разноски тем же флангом, каким однажды обходил поштучный отказ для объектов уровня (#739).
    /// </summary>
    [Fact]
    public async Task Стройку_и_раздел_с_разноской_счёта_удалить_нельзя()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 10)]);
        var (site, sections) = await SiteAsync("Занятая", "1 эт.");
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 10, section: sections[0])]);

        var bySection = await client.DeleteAsync($"/api/sections/{sections[0]}");
        Assert.Equal(HttpStatusCode.Conflict, bySection.StatusCode);
        Assert.Contains("части разноски счетов на этот раздел — 1", await bySection.Content.ReadAsStringAsync());

        var bySite = await client.DeleteAsync($"/api/constructions/{site}");
        Assert.Equal(HttpStatusCode.Conflict, bySite.StatusCode);
        var text = await bySite.Content.ReadAsStringAsync();
        Assert.Contains("части разноски счетов на эту стройку — 1", text);
        // Раздел уходит со стройкой — и держат его так же: спрашивается всё, что уносит каскад.
        Assert.Contains("части разноски счетов на этот раздел — 1", text);
    }

    /// <summary>
    /// Тип, по которому заведены счета, занят данными модуля — и это видно и удалению, и заранее,
    /// на экране типа: причины у них общие (issue #275), и разойтись им нельзя.
    /// </summary>
    [Fact]
    public async Task Тип_счёта_занят_данными_модуля()
    {
        var (client, _) = await SignInAsync("Admin");
        await CreateAsync(client);
        var type = await TypeAsync(CostsRecordTypes.InvoiceCode, "Счёт на оплату");

        using var scope = host.Services.CreateScope();
        var usage = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new GetDocumentTypeUsageQuery(type));

        var reason = Assert.Single(usage.Reasons, r => r.Kind.StartsWith("modules"));
        Assert.Contains("счета этого типа", reason.Label);
    }

    // ── Скан: о модулях по имени не знает ──────────────────────────────────────

    /// <summary>
    /// Держит колонка ЛЮБОГО вида, где встречается идентификатор, — и в схеме, чьего модуля в сборке
    /// нет вовсе. Объявлений у такой схемы нет, и обнаружение по объявлениям её бы отпустило: ссылки
    /// модуля, который вернут следующей поставкой, потерялись бы молча.
    /// <c>§</c> — идентификатор, <c>¤</c> — он же прописными.
    /// </summary>
    [Theory]
    [InlineData("uuid", "§")]
    [InlineData("uuid[]", "{§}")]
    [InlineData("jsonb", "{\"Поставщик\":{\"$ref\":\"catalog\",\"entryId\":\"§\"}}")]
    [InlineData("json", "{\"entryId\":\"¤\"}")]
    [InlineData("jsonb[]", "{\"{\\\"entryId\\\":\\\"§\\\"}\"}")]
    public async Task Держит_колонка_любого_вида_в_схеме_без_модуля(string columnType, string template)
    {
        var record = await OwnPositionAsync();
        var value = template.Replace("§", record.ToString()).Replace("¤", record.ToString().ToUpperInvariant());

        await using var probe = await ProbeAsync($"id uuid PRIMARY KEY, held {columnType}");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0::{columnType})", value);

        using var scope = host.Services.CreateScope();
        var holders = scope.ServiceProvider.GetRequiredService<IRecordHolders>();
        var found = await holders.FindAsync([record]);

        Assert.Equal([record], (await holders.HeldAsync([record])).Ids);
        Assert.Contains("данные модуля, которого нет в этой сборке: 1", Assert.Single(found.Lines));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            scope.ServiceProvider.GetRequiredService<IMediator>().Send(new DeleteCommonDataEntryCommand(record)));
        Assert.Contains("ссылаются данные модулей", refusal.Message);
    }

    /// <summary>
    /// Колонку, которую назвать нечем (модуль о ней промолчал или модуля нет), администратору отказ
    /// называет адресом таблицы — ему с этим разбираться. Остальным адрес не показывается: см.
    /// <see cref="Без_прав_отказ_называет_число_но_не_счёт_и_не_таблицу" />.
    /// </summary>
    [Fact]
    public async Task Администратору_отказ_называет_адрес_необъявленной_колонки()
    {
        var (client, _) = await SignInAsync("Admin");
        var record = await OwnPositionAsync();
        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, held uuid");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0)", record);

        var refusal = await client.DeleteAsync($"/api/common-data/{record}");

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains($"{probe.Schema}.things.held", await refusal.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Колонка, которую модуль объявил «помнит, но не держит», удалению не мешает — это единственное,
    /// чем объявление влияет на ответ. А та же колонка без объявления — держит: молчание модуля
    /// запись не освобождает.
    /// </summary>
    [Fact]
    public async Task Объявление_не_держит_освобождает_колонку_а_молчание_нет()
    {
        var record = await OwnPositionAsync();
        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, seen uuid");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0)", record);

        using var scope = host.Services.CreateScope();

        var silent = HoldersWith(scope, new ModuleRegistry([new ProbeModule(probe.Schema, [])], []));
        var held = await silent.FindAsync([record]);
        Assert.Contains("«Проба»: записей — 1", Assert.Single(held.Lines));

        var remembering = HoldersWith(scope, new ModuleRegistry([new ProbeModule(probe.Schema,
            [ModuleReference.Remembering("things", "seen", ReferenceTarget.Record, "история просмотров")])], []));
        Assert.False((await remembering.FindAsync([record])).Any);
        Assert.Empty((await remembering.HeldAsync([record])).Ids);
    }

    /// <summary>
    /// Число в отказе — строки модуля, а не попадания: удаление уровня спрашивает о многих записях
    /// разом, и счёт, в чьих полях стоят две из них, — один счёт, а не два (ревью PR #1188).
    /// </summary>
    [Fact]
    public async Task Строка_с_двумя_спрошенными_записями_считается_один_раз()
    {
        var first = await OwnPositionAsync();
        var second = await OwnPositionAsync();
        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, held jsonb");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0::jsonb)",
            $$"""{"a":"{{first}}","b":"{{second}}","again":"{{first}}"}""");

        using var scope = host.Services.CreateScope();
        var holders = scope.ServiceProvider.GetRequiredService<IRecordHolders>();

        Assert.Contains(": 1", Assert.Single((await holders.FindAsync([first, second])).Lines));
        Assert.Equal(new[] { first, second }.Order(), (await holders.HeldAsync([first, second])).Ids.Order());
    }

    /// <summary>
    /// Остальные пути удаления: документ комплекта, документ качества и уборка сирот. Вызов у каждого
    /// свой, и перепись по исходникам стережёт только то, что файл спрашивает ХОТЬ ГДЕ-ТО, — убранный
    /// вызов при оставленной зависимости она не заметит. Поэтому здесь — поведением.
    /// </summary>
    [Fact]
    public async Task Документ_и_документ_качества_занятые_данными_модуля_не_удаляются()
    {
        using var scope = host.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var code = $"T{Guid.NewGuid():N}"[..12];
        var type = await m.Send(new CreateDocumentTypeCommand(
            $"Акт {code}", code, BHS.CRG.Domain.Documents.DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}""")));
        var (site, sections) = await SiteAsync("Под документы", "ЭОМ");
        var set = await m.Send(new CreateDocumentSetCommand(sections[0], "ЭОМ-1"));
        var document = await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));
        var certificate = await m.Send(new BHS.CRG.Application.QualityDocs.CreateQualityDocumentCommand(
            type.Id, "Сертификат", JsonDocument.Parse("{}"), BHS.CRG.Domain.Catalog.CatalogScope.System, null,
            BHS.CRG.Domain.Documents.QualityDocSource.Manual, null, null, null));

        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, held uuid");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0)", document.Id);
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0)", certificate.Id);

        await Assert.ThrowsAsync<ConflictException>(() => m.Send(new DeleteDocumentInstanceCommand(document.Id)));
        await Assert.ThrowsAsync<ConflictException>(() =>
            m.Send(new BHS.CRG.Application.QualityDocs.DeleteQualityDocumentCommand(certificate.Id)));
        // И комплект, и стройка над документом: каскад уровня спрашивает о содержимом.
        await Assert.ThrowsAsync<ConflictException>(() => m.Send(new DeleteDocumentSetCommand(set.Id)));
        await Assert.ThrowsAsync<ConflictException>(() => m.Send(new DeleteConstructionCommand(site)));
    }

    /// <summary>
    /// Уборка сирот — путь без человека: то, в чём человеку отказано, она не делает. А когда проверить
    /// не удалось, не трогает никого, но ОТЧЁТ отдаёт — сухой прогон обязан отвечать числом, а не
    /// отказом (ревью PR #1188).
    /// </summary>
    [Fact]
    public async Task Уборка_сирот_не_трогает_занятое_а_без_проверки_не_трогает_никого()
    {
        Guid held, free;
        using (var seed = host.Services.CreateScope())
        {
            var m = seed.ServiceProvider.GetRequiredService<IMediator>();
            var code = $"T{Guid.NewGuid():N}"[..12];
            var type = await m.Send(new CreateDocumentTypeCommand(
                $"Акт {code}", code, BHS.CRG.Domain.Documents.DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}""")));
            var (_, sections) = await SiteAsync("Под сирот", "ЭОМ");
            var set = await m.Send(new CreateDocumentSetCommand(sections[0], "ЭОМ-1"));
            held = (await m.Send(new AddDocumentToSetCommand(set.Id, type.Id))).Id;
            free = (await m.Send(new AddDocumentToSetCommand(set.Id, type.Id))).Id;
            // Комплект исчезает мимо каскада — так и получаются сироты.
            await SqlAsync("""DELETE FROM document_sets WHERE "Id" = @p0""", set.Id);
        }

        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, held uuid");
        await SqlAsync($"INSERT INTO {probe.Schema}.things VALUES (gen_random_uuid(), @p0)", held);

        var role = $"probe_role_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE ROLE {role} NOLOGIN");
        try
        {
            // Сначала — без проверки: роль читает ядро, но не схемы вне его.
            await SqlAsync($"GRANT USAGE ON SCHEMA public TO {role}");
            await SqlAsync($"GRANT SELECT, DELETE ON ALL TABLES IN SCHEMA public TO {role}");
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.OpenConnectionAsync();
                try
                {
#pragma warning disable EF1002 // Имя роли — из теста.
                    await db.Database.ExecuteSqlRawAsync($"SET ROLE {role}");
#pragma warning restore EF1002
                    var blind = await scope.ServiceProvider
                        .GetRequiredService<BHS.CRG.Infrastructure.Maintenance.OrphanObjectCleanup>().RunAsync(dryRun: false);
                    Assert.True(blind.HoldersUnverified);
                    Assert.Equal(0, blind.Total);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync("RESET ROLE");
                    await db.Database.CloseConnectionAsync();
                }
            }
            Assert.True(await ExistsAsync(free), "уборка без проверки держателей удалила сироту");

            using (var scope = host.Services.CreateScope())
            {
                var report = await scope.ServiceProvider
                    .GetRequiredService<BHS.CRG.Infrastructure.Maintenance.OrphanObjectCleanup>().RunAsync(dryRun: false);
                Assert.False(report.HoldersUnverified);
            }
            Assert.True(await ExistsAsync(held), "уборка удалила сироту, которую держат данные модуля");
            Assert.False(await ExistsAsync(free));
        }
        finally
        {
            await SqlAsync($"DROP OWNED BY {role}");
            await SqlAsync($"DROP ROLE {role}");
        }
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DomainObjects.AnyAsync(o => o.Id == id);
    }

    /// <summary>
    /// Данные вне ядра прочитать нельзя — ОТКАЗ, а не «никто не держит»: непрочитанное могло держать.
    /// Роль здесь не имеет на схему никаких прав — и колонка всё равно в списке: он берётся из
    /// каталога базы, а не из <c>information_schema</c>, которая такую колонку не показала бы вовсе.
    /// </summary>
    [Fact]
    public async Task Нечитаемые_данные_вне_ядра_останавливают_удаление()
    {
        var record = await OwnPositionAsync();
        await using var probe = await ProbeAsync("id uuid PRIMARY KEY, held uuid");
        var role = $"probe_role_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE ROLE {role} NOLOGIN");
        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.OpenConnectionAsync();
            try
            {
#pragma warning disable EF1002 // Имя роли — из теста.
                await db.Database.ExecuteSqlRawAsync($"SET ROLE {role}");
#pragma warning restore EF1002
                var holders = scope.ServiceProvider.GetRequiredService<IRecordHolders>();
                var found = await holders.FindAsync([record]);

                // Оба вопроса отвечают «не проверено»: человеку — отказом в удалении, а пути без
                // человека — признаком, с которым пустое множество нельзя прочесть как «свободны все».
                var refusal = Assert.Throws<ConflictException>(() => found.EnsureNone("запись"));
                Assert.False((await holders.HeldAsync([record])).Verified);
                Assert.Contains("Удаление отменено", refusal.Message);
                Assert.DoesNotContain("probe_", refusal.Message); // адрес — только администратору
                Assert.DoesNotContain("costs.", refusal.Message);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("RESET ROLE");
                await db.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            await SqlAsync($"DROP ROLE {role}");
        }
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Своя позиция номенклатуры: общие позиции базового класса делят все классы хоста.</summary>
    private async Task<Guid> OwnPositionAsync() =>
        await EntryAsync(
            await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция {Guid.NewGuid().ToString()[..8]}");

    private static ModuleRecordHolders HoldersWith(IServiceScope scope, ModuleRegistry registry) =>
        new(registry,
            scope.ServiceProvider.GetRequiredService<ModuleReferenceScan>(),
            scope.ServiceProvider.GetRequiredService<IUserPermissions>(),
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleRecordHolders>>());

    private async Task SqlAsync(string sql, params object[] parameters)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
#pragma warning disable EF1002 // Имя схемы — из теста, значения — параметрами.
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
#pragma warning restore EF1002
    }

    /// <summary>Подставная схема с одной таблицей <c>things</c>; уходит вместе с тестом.</summary>
    private async Task<Probe> ProbeAsync(string columns)
    {
        var schema = $"probe_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE SCHEMA {schema}");
        await SqlAsync($"CREATE TABLE {schema}.things ({columns})");
        return new Probe(schema, () => SqlAsync($"DROP SCHEMA {schema} CASCADE"));
    }

    private sealed class Probe(string schema, Func<Task> drop) : IAsyncDisposable
    {
        public string Schema => schema;
        public async ValueTask DisposeAsync() => await drop();
    }

    /// <summary>Модуль, которому принадлежит подставная схема, — ради его объявлений.</summary>
    private sealed class ProbeModule(string schema, IReadOnlyList<ModuleReference> references) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public ModuleSchema? Schema => new(schema, typeof(AppDbContext));
        public IReadOnlyList<ModuleReference> References => references;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}

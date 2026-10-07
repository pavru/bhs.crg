using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Принудительное удаление записи, которую держат только данные выключенного или снятого модуля
/// (issue #1187, ТЗ AUTH-19, CORE-34).
///
/// <para>«Снятый модуль» здесь — подставная схема: о ней не знает ни один модуль сборки, и это
/// единственный способ дойти до адреса с настоящим хостом — выключенный модуль своих данных на
/// включённом хосте не имеет. Выключенный проверяется составом реестра, как в
/// <see cref="OccupiedRecordDeleteTests" />: решение принимает ответ порта, а не адрес.</para>
/// </summary>
[Collection("Integration")]
public class RecordPurgeTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Администратор видит в отказе, что запись держат данные снятого модуля и сколько
    /// ссылок; неверное число — отказ со свежим предложением, запись на месте; верное — запись
    /// удалена, а в журнале — кто, что и сколько ссылок потеряно, с адресом колонки.
    /// </summary>
    [Fact]
    public async Task Запись_занятую_данными_снятого_модуля_удаляют_назвав_число_ссылок()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var record = await OwnPositionAsync();
        await using var probe = await HeldByProbeAsync(record, rows: 2);

        var refusal = await client.DeleteAsync($"/api/common-data/{record}");
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var offer = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge");
        Assert.True(offer.GetProperty("allowed").GetBoolean());
        Assert.Equal(2, offer.GetProperty("references").GetInt32());
        // Потерю в схеме без модуля не покажет никто — и предложение говорит это числом.
        Assert.Equal(2, offer.GetProperty("untraceable").GetInt32());
        Assert.Equal(2, Assert.Single(offer.GetProperty("holders").EnumerateArray()).GetProperty("rows").GetInt32());

        var wrong = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 1 });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        var again = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Число ссылок не совпало", again.GetProperty("error").GetString());
        Assert.Equal(2, again.GetProperty("purge").GetProperty("references").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
        Assert.Empty(await PurgedAsync(record));

        var done = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 2 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(2, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("references").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);

        var written = Assert.Single(await PurgedAsync(record));
        Assert.StartsWith("Номенклатура: Позиция", written.TargetLabel);
        Assert.Contains($"{probe.Schema}.things.held", written.Before);
        Assert.Equal("потеряно ссылок: 2", written.After);
        Assert.NotEqual("", written.ActorName);
    }

    /// <summary>
    /// Без права адрес закрыт, а отказ обычного удаления говорит, что выход есть и у кого: молчать о
    /// нём нельзя, а разбивку держателей тому, кто удалить не может, показывать незачем.
    /// </summary>
    [Fact]
    public async Task Без_права_адрес_закрыт_а_отказ_называет_выход_без_разбивки()
    {
        var (client, _) = await SignInAsync(SystemRoles.IdEngineer);
        var record = await OwnPositionAsync();
        await using var probe = await HeldByProbeAsync(record, rows: 1);

        var purge = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, purge.StatusCode);

        var refusal = await client.DeleteAsync($"/api/common-data/{record}");
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var offer = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge");
        Assert.False(offer.GetProperty("allowed").GetBoolean());
        Assert.Empty(offer.GetProperty("holders").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
    }

    /// <summary>
    /// Запись, которую держит ВКЛЮЧЁННЫЙ модуль, не удаляется и с правом, и с верным числом: там
    /// ссылку убирают обычным путём. Держит ещё и снятый — всё равно отказ: одного включённого
    /// держателя достаточно.
    /// </summary>
    [Fact]
    public async Task Запись_занятую_включённым_модулем_право_не_удаляет()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var position = await OwnPositionAsync();
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(position, quantity: 3, price: 10)]);

        var refusal = await client.DeleteAsync($"/api/common-data/{position}");
        Assert.Equal(JsonValueKind.Null,
            (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/common-data/{position}/purge", new { references = 1 })).StatusCode);

        await using var probe = await HeldByProbeAsync(position, rows: 1);

        var both = await client.PostAsJsonAsync($"/api/common-data/{position}/purge", new { references = 2 });
        Assert.Equal(HttpStatusCode.Conflict, both.StatusCode);
        Assert.Equal(JsonValueKind.Null,
            (await both.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{position}")).StatusCode);
        Assert.Empty(await PurgedAsync(position));
    }

    /// <summary>
    /// Запись, которую никто не держит, принудительно не удаляется: иначе право стало бы вторым
    /// обычным удалением — в обход его собственных отказов и без следа в журнале по делу.
    /// </summary>
    [Fact]
    public async Task Свободную_запись_принудительно_не_удаляют()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var record = await OwnPositionAsync();

        var refusal = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 0 });

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains("обычным путём", await refusal.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
    }

    /// <summary>
    /// Отказы ядра принудительное удаление не обходит: запись, на которую ссылается другой объект
    /// ядра, остаётся на месте, хотя модульные держатели у неё — только снятые. И выхода отказ не
    /// предлагает: предложение стоит только за последним отказом удаления.
    /// </summary>
    [Fact]
    public async Task Отказ_ядра_принудительное_удаление_не_обходит()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var record = await OwnPositionAsync();
        await using var probe = await HeldByProbeAsync(record, rows: 1);
        await ReferringAsync(record);

        var purge = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 1 });

        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        var body = await purge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("ссылаются другие объекты", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
    }

    /// <summary>
    /// Выключенный модуль: запись отпускается, и потерю в объявленной колонке модуль потом найдёт —
    /// в отличие от колонки снятого. Не проверено — не отпускается никогда.
    /// </summary>
    [Fact]
    public async Task Выключенный_модуль_отпускает_запись_а_его_объявленную_потерю_найдут()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var position = await OwnPositionAsync();
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(position, quantity: 3, price: 10)]);

        using var scope = host.Services.CreateScope();
        var switchedOff = new ModuleRecordHolders(
            new ModuleRegistry([], [new CostsModule()]),
            scope.ServiceProvider.GetRequiredService<ModuleReferenceScan>(),
            scope.ServiceProvider.GetRequiredService<IUserPermissions>(),
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleRecordHolders>>());

        var found = await switchedOff.FindAsync([position]);

        var holder = Assert.Single(found.Holders);
        Assert.Equal(HolderState.Disabled, holder.State);
        Assert.True(holder.Traceable);
        Assert.Equal(0, found.Release!.Untraceable);
        Assert.Throws<RecordHeldException>(() => found.EnsureOnlyDormant("запись", 5));

        // Слова отказа: «убрать негде» — только там, где есть принудительный выход. Документ, стройка
        // и остальные пути его не имеют, и им обязан остаться названным прежний путь (ревью PR #1246).
        Assert.Contains("выключен или снят",
            Assert.Throws<RecordHeldException>(() => found.EnsureNone("запись", forcedExit: true)).Message);
        Assert.Contains("включите его",
            Assert.Throws<RecordHeldException>(() => found.EnsureNone("документ")).Message);
        Assert.Equal(1, found.EnsureOnlyDormant("запись", 1).References);

        var unverified = RecordHoldings.Unverified("Не удалось проверить.");
        Assert.Null(unverified.Release);
        Assert.Throws<ConflictException>(() => unverified.EnsureOnlyDormant("запись", 0));
    }

    /// <summary>
    /// Запись, которую держит внешний КЛЮЧ посторонней схемы, принудительно не удаляется: скан видит
    /// колонку и предлагает выход, но база такую ссылку оборвать не даёт. Ответ — отказ со словами,
    /// а не внутренняя ошибка, и запись на месте (ревью PR #1246).
    /// </summary>
    [Fact]
    public async Task Внешний_ключ_посторонней_схемы_отвечает_отказом_а_не_поломкой()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var record = await OwnPositionAsync();
        var schema = $"probe_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE SCHEMA {schema}");
        await using var probe = new Probe(schema, () => SqlAsync($"DROP SCHEMA {schema} CASCADE"));
        await SqlAsync($"CREATE TABLE {schema}.things (id uuid PRIMARY KEY, held uuid REFERENCES public.domain_objects(\"Id\"))");
        await SqlAsync($"INSERT INTO {schema}.things VALUES (gen_random_uuid(), @p0)", record);

        var purge = await client.PostAsJsonAsync($"/api/common-data/{record}/purge", new { references = 1 });

        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        var body = await purge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("внешний ключ базы", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
        Assert.Empty(await PurgedAsync(record));
    }

    /// <summary>
    /// «Модуля нет в сборке» получается исключением: схему не назвал ни один модуль. С этой задачи
    /// такой ответ ОТПУСКАЕТ запись — и модуль, чьё объявленное имя схемы разошлось бы с настоящим,
    /// выглядел бы снятым при включённом. Поэтому имя в объявлении обязано быть именем схемы его
    /// контекста, а два модуля одной схемы назвать не могут.
    /// </summary>
    [Fact]
    public void Объявленная_схема_модуля_та_в_которой_лежат_его_таблицы()
    {
        using var scope = host.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var declared = registry.Enabled.Where(m => m.Schema is not null).ToList();
        Assert.NotEmpty(declared);

        foreach (var module in declared)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.Schema!.ContextType);
            Assert.Equal(module.Schema.Name, context.Model.GetDefaultSchema());
        }

        var names = registry.Enabled.Concat(registry.Disabled).Select(m => m.Schema?.Name).OfType<string>().ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private async Task<Guid> OwnPositionAsync() =>
        await EntryAsync(
            await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция {Guid.NewGuid().ToString()[..8]}");

    /// <summary>Другая запись ядра, ссылающаяся на эту значением поля.</summary>
    private async Task ReferringAsync(Guid target)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
            $"Ссылается {Guid.NewGuid().ToString()[..8]}",
            await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            JsonDocument.Parse(
                """{"Наименование":"x","Основа":{"$ref":"catalog","entryId":"§"}}""".Replace("§", target.ToString())),
            CatalogScope.System, null));
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> PurgedAsync(Guid record)
    {
        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, ActivityActions.RecordPurged.Code);
        return [.. all.Where(r => r.TargetId == record.ToString())];
    }

    /// <summary>Подставная схема, о которой не знает ни один модуль: держит запись столькими строками.</summary>
    private async Task<Probe> HeldByProbeAsync(Guid record, int rows)
    {
        var schema = $"probe_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE SCHEMA {schema}");
        await SqlAsync($"CREATE TABLE {schema}.things (id uuid PRIMARY KEY, held uuid)");
        for (var i = 0; i < rows; i++)
            await SqlAsync($"INSERT INTO {schema}.things VALUES (gen_random_uuid(), @p0)", record);
        return new Probe(schema, () => SqlAsync($"DROP SCHEMA {schema} CASCADE"));
    }

    private async Task SqlAsync(string sql, params object[] parameters)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
#pragma warning disable EF1002 // Имя схемы — из теста, значения — параметрами.
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
#pragma warning restore EF1002
    }

    private sealed class Probe(string schema, Func<Task> drop) : IAsyncDisposable
    {
        public string Schema => schema;
        public async ValueTask DisposeAsync() => await drop();
    }
}

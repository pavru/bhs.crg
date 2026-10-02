using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Оснастка проверок строк счёта (C2, issue #1078): хост со своей базой, посев и помощники.
///
/// <para>Своим файлом — не ради порядка, а по храповику размера (#1041): проверок у строк счёта много,
/// и вместе с оснасткой один файл перешагнул порог. Разделено по ЗАНЯТИЯМ: здесь то, чем проверки
/// пользуются, в <see cref="InvoiceLineTests" /> — набор строк и его отказы, в
/// <see cref="InvoiceParsedStateTests" /> — переход «разобран» и отбор «Разобрать».</para>
///
/// <para>⚠️ Посев статический, и это не оптимизация: xUnit создаёт новый экземпляр класса на КАЖДЫЙ
/// тест, а типы «Организация» и «Номенклатура» с их записями обязаны завестись один раз. Оба класса
/// проверок делят один хост и один посев — второй хост означал бы вторую базу и второй прогон
/// миграций.</para>
/// </summary>
[Collection("Integration")]
public abstract class InvoiceLineTestBase(InvoiceLineHost host)
    : IClassFixture<InvoiceLineHost>, IAsyncLifetime
{
    private const string Password = "Test#12345";

    // Статические по той же причине, что у C1: xUnit создаёт новый экземпляр класса на каждый тест, а
    // посев (типы, организации, позиции номенклатуры) обязан случиться один раз.
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    protected static Guid supplier;
    protected static Guid payer;
    protected static Guid cable;
    protected static Guid conduit;

    /// <summary>
    /// Посев: типы «Организация» и «Номенклатура» заводит ЧЕЛОВЕК (первый) и миграция ядра там, где есть
    /// материалы (второй) — на чистой базе нет ни того, ни другого. Заводим оба, повторяем проекцию типов
    /// модуля (она идемпотентна) и кладём две организации и две позиции номенклатуры.
    /// </summary>
    public async Task InitializeAsync()
    {
        await SeedGate.WaitAsync();
        try
        {
            if (supplier != Guid.Empty) return;

            var organizations = await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация");
            var nomenclature = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");

            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.ProjectModuleTypesAsync();

            supplier = await EntryAsync(organizations, "ООО «Кабель-Торг»");
            payer = await EntryAsync(organizations, "ООО «Наша компания»");
            cable = await EntryAsync(nomenclature, "Кабель ВВГнг-LS 3х2,5");
            conduit = await EntryAsync(nomenclature, "Труба гофрированная 20 мм");
        }
        finally
        {
            SeedGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Строка счёта так, как её присылает форма.</summary>
    protected static Dictionary<string, object?> Line(
        Guid? nomenclature, decimal quantity, decimal price, decimal? rate = null, string? text = null,
        decimal? amount = null, decimal? vat = null, Guid? id = null) =>
        new()
        {
            ["id"] = id?.ToString(),
            ["nomenclature"] = nomenclature is { } value ? Reference(value) : null,
            ["supplierText"] = text,
            ["quantity"] = quantity,
            ["price"] = price,
            ["vatRate"] = rate,
            ["vatAmount"] = vat,
            ["amount"] = amount,
        };

    protected static Dictionary<string, object?> Reference(Guid id) =>
        new() { ["$ref"] = "catalog", ["entryId"] = id.ToString() };

    protected static async Task<JsonElement> LinesAsync(
        HttpClient client, Guid invoice, object[] lines)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Завести счёт. <paramref name="complete" /> — со всеми обязательными полями: такой счёт годится
    /// для перехода «разобран», а без них переход отказывает (и это отдельный тест).
    /// </summary>
    protected static async Task<Guid> CreateAsync(HttpClient client, bool complete = false)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(supplier),
            ["Плательщик"] = complete ? Reference(payer) : null,
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Сколько записей «строки счёта изменены» стоит в журнале у этого счёта.</summary>
    protected async Task<int> RecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = await journal.ReadAsync(0, 200, "costs.invoice.lines");
        return records.Count(r => r.TargetId == invoice.ToString());
    }

    /// <summary>
    /// Стройка с разделами — своя на каждый тест: разноска ссылается на неё, а посев общий статический,
    /// и чужая стройка, удалённая соседним тестом, выглядела бы здесь потерей.
    /// </summary>
    protected async Task<(Guid Site, Guid[] Sections)> SiteAsync(string name, params string[] sections)
    {
        using var scope = host.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var site = await mediator.Send(new CreateConstructionCommand($"{name} {Guid.NewGuid().ToString()[..6]}", Guid.NewGuid()));
        var created = new List<Guid>();
        foreach (var section in sections)
            created.Add((await mediator.Send(new CreateSectionCommand(site.Id, section))).Id);

        return (site.Id, [.. created]);
    }

    protected static Dictionary<string, object?> Part(
        Guid site, decimal? quantity = null, decimal? amount = null, Guid? section = null, Guid? id = null) =>
        new()
        {
            ["id"] = id?.ToString(),
            ["construction"] = site.ToString(),
            ["section"] = section?.ToString(),
            ["quantity"] = quantity,
            ["amount"] = amount,
        };

    protected static Task<HttpResponseMessage> AllocateRawAsync(
        HttpClient client, Guid invoice, Guid line, object[] parts) =>
        client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines/{line}/allocation", new { parts });

    protected static async Task<JsonElement> AllocateAsync(HttpClient client, Guid invoice, Guid line, object[] parts)
    {
        var response = await AllocateRawAsync(client, invoice, line, parts);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Строки, каждая разнесённая целиком на одну стройку, — счёт, которому до «разобран» (F1) не
    /// хватает только решения человека. Строки — с количеством.
    /// </summary>
    protected async Task<JsonElement> AllocatedLinesAsync(HttpClient client, Guid invoice, object[] lines)
    {
        var view = await LinesAsync(client, invoice, lines);
        var (site, _) = await SiteAsync("Стройка");

        foreach (var line in view.GetProperty("lines").EnumerateArray().ToList())
            view = await AllocateAsync(client, invoice, line.GetProperty("id").GetGuid(),
                [Part(site, quantity: line.GetProperty("quantity").GetDecimal())]);

        return view;
    }

    /// <summary>Идентификатор строки счёта по её номеру в ответе.</summary>
    protected static Guid LineId(JsonElement view, int ordinal) =>
        view.GetProperty("lines")[ordinal - 1].GetProperty("id").GetGuid();

    protected static async Task<JsonElement> ReadAsync(HttpClient client, Guid invoice) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}");

    /// <summary>Реквизиты счёта, как их отдаёт сервер, с одним изменённым полем.</summary>
    protected static async Task<JsonElement> RequisitesWithAsync(HttpClient client, Guid invoice, string key, object value)
    {
        var requisites = (await ReadAsync(client, invoice)).GetProperty("requisites");
        var patched = JsonSerializer.Deserialize<Dictionary<string, object?>>(requisites.GetRawText())!;
        patched[key] = value;
        return JsonSerializer.SerializeToElement(patched);
    }

    protected static async Task OkAsync(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    protected async Task<Guid> TypeAsync(string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();

        var found = await types.FindAsync(t => t.Code == code);
        if (found.Count > 0) return found[0].Id;

        var created = DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(created);
        await types.SaveChangesAsync();
        return created.Id;
    }

    /// <summary>
    /// Запись справочника — ТАК, КАК ЕЁ ЗАВОДИТ ЭКРАН: общие данные (<c>domain_objects</c>). Сойдя с
    /// дороги экрана, тест снова начал бы подтверждать сам себя — ровно это и случилось в C1, когда
    /// помощник писал в таблицу прежней модели, из которой читал порт.
    /// </summary>
    /// <para>⚠️ Заводится, только если такой записи ещё нет. База между прогонами НЕ сбрасывается (как и
    /// у C1), а статические поля класса — да: посев без этой проверки на втором прогоне давал бы вторую
    /// «Трубу гофрированную», и тест поиска падал бы на дубле, которого в коде нет.</para>
    /// <summary>
    /// Позиция номенклатуры без названия — так, как это бывает в живой базе (записи без имени в ней
    /// есть). Имя снимается запросом к базе, а не командой: команда его требует, и правильно
    /// требует — состояние это старое, а не создаваемое.
    /// </summary>
    protected async Task<Guid> NamelessAsync()
    {
        var type = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        var id = await EntryAsync(type, $"Позиция без имени {Guid.NewGuid().ToString()[..6]}");

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE domain_objects SET "DisplayName" = NULL WHERE "Id" = {0}""", id);
        return id;
    }

    /// <summary>Убрать запись справочника из базы — так выглядит удалённая человеком позиция.</summary>
    protected async Task ForgetAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM domain_objects WHERE "Id" = {0}""", id);
    }

    protected async Task<Guid> EntryAsync(Guid typeId, string name)
    {
        using var scope = host.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var known = await mediator.Send(new ListCommonDataRefsQuery([typeId], name));
        if (known.FirstOrDefault(r => r.DisplayName == name) is { } found) return found.Id;

        var created = await mediator.Send(new CreateCommonDataEntryCommand(name, typeId,
            JsonDocument.Parse($$"""{"Наименование":"{{name}}"}"""), CatalogScope.System, null, null));
        return created.Id;
    }

    protected async Task<(HttpClient Client, Guid Id)> SignInAsync(string role)
    {
        var email = $"line_{Guid.NewGuid():N}@test.local";
        Guid id;

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }
}

/// <summary>
/// Хост со включённым модулем счетов и своей базой (C2, issue #1078) — по той же причине, что у
/// <see cref="InvoiceHost" />: состав системных ролей приводится при старте к объявленному, и хост с
/// другим набором модулей менял бы права ролям у соседних классов.
///
/// <para>Не запечатан ради <see cref="InvoiceClockHost" />: тот же хост с подставным «сегодня».</para>
/// </summary>
public class InvoiceLineHost : IntegrationTestFixture
{
    private static string ConnectionString { get; } = Dedicated();

    private static string Dedicated() => TestDatabases.ConnectionString("lines");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}

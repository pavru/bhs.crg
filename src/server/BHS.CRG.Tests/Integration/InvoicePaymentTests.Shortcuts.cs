using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Готовые отборы таблицы счетов с числом строк под каждым (issue #1186) — то, что экран показывает
/// чипами «Навести порядок» над реестром.
/// </summary>
public partial class InvoicePaymentTests
{
    private static async Task<JsonElement> ShortcutsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tables/costs.invoices/shortcuts");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Shortcut(JsonElement shortcuts, string code) =>
        shortcuts.EnumerateArray().Single(s => s.GetProperty("code").GetString() == code);

    private static int ShortcutCount(JsonElement shortcuts, string code) =>
        Shortcut(shortcuts, code).GetProperty("count").GetInt32();

    /// <summary>
    /// Сторож задачи — <b>равенство трёх чисел</b>: число на чипе, число строк таблицы под условием,
    /// которое чип называет, и число счётчика потерь. До замены значения и после неё.
    ///
    /// <para>Условие берётся ИЗ ОТВЕТА готовых отборов, а не из констант теста: экран ставит именно
    /// его, и сторож обязан идти тем же путём.</para>
    /// </summary>
    [Fact]
    public async Task Число_готового_отбора_равно_числу_строк_под_его_условием_и_счётчику()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, position, _) = await InvoiceWithPositionAsync(admin, "Позиция под чип");
        await ForgetAsync(position);

        var lost = Shortcut(await ShortcutsAsync(admin), "lost");
        Assert.Equal("eq", lost.GetProperty("op").GetString());
        var rows = await TroubleTableAsync(admin, lost.GetProperty("column").GetString()!, lost.GetProperty("value").GetString()!);

        Assert.True(Has(rows, invoice));
        Assert.Equal(Count(rows), lost.GetProperty("count").GetInt32());
        Assert.Equal((await TallyAsync(admin, "editable")).Invoices, lost.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, lost.GetProperty("unchecked").ValueKind);

        var line = (await ReadAsync(admin, invoice)).GetProperty("lines")[0];
        await LinesAsync(admin, invoice, [Line(cable, 100, 400, id: line.GetProperty("id").GetGuid())]);

        Assert.Equal(Count(rows) - 1, ShortcutCount(await ShortcutsAsync(admin), "lost"));
    }

    /// <summary>
    /// Отбор «Записи в архиве» считает только счета в работе: оплаченный счёт с архивной записью верен
    /// навсегда, и число, в которое он входит, до нуля не довести (решение владельца 07.10.2026).
    /// </summary>
    [Fact]
    public async Task Готовый_отбор_архива_считает_только_неоплаченные_счета()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var before = ShortcutCount(await ShortcutsAsync(admin), "archived");

        var (open, first, _) = await InvoiceWithPositionAsync(admin, "Чип архива, счёт в работе");
        var (paid, second, _) = await InvoiceWithPositionAsync(admin, "Чип архива, счёт оплачен");
        await PayAsync(admin, paid, today, await PreviewAsync(admin, paid, today), null);
        using (var scope = host.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<IRecordArchive>();
            await archive.SetAsync(first, archived: true);
            await archive.SetAsync(second, archived: true);
        }

        var archived = Shortcut(await ShortcutsAsync(admin), "archived");
        Assert.Equal(before + 1, archived.GetProperty("count").GetInt32());
        Assert.True(archived.GetProperty("quiet").GetBoolean());
        var rows = await TroubleTableAsync(
            admin, archived.GetProperty("column").GetString()!, archived.GetProperty("value").GetString()!);
        Assert.Equal(Count(rows), archived.GetProperty("count").GetInt32());
        Assert.True(Has(rows, open));
        Assert.False(Has(rows, paid));
    }

    /// <summary>
    /// <b>Тому, кто исправить не может, отборы не предлагаются</b>: бухгалтер читает счета, но не правит
    /// их, и число было бы для него упрёком без выхода. Таблица и сами колонки ему открыты.
    /// </summary>
    [Fact]
    public async Task Готовые_отборы_не_предлагаются_тому_кто_счёт_править_не_может()
    {
        var (accountant, _) = await SignInAsync("Accountant");

        Assert.Empty((await ShortcutsAsync(accountant)).EnumerateArray());
        await OkAsync(await accountant.GetAsync($"/api/tables/costs.invoices?columns=Номер,{LostColumn}&limit=1"));

        // А тому, кому закрыта таблица, — отказ, тот же, что у строк: ворота одни.
        var (user, _) = await SignInAsync("User");
        Assert.Equal(
            (await user.GetAsync("/api/tables/costs.invoices?limit=1")).StatusCode,
            (await user.GetAsync("/api/tables/costs.invoices/shortcuts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.GetAsync("/api/tables/costs.nothing/shortcuts")).StatusCode);
    }

    /// <summary>Порт, у которого опрос проверил не всё: одна колонка не прочиталась, найденного нет.</summary>
    private sealed class HalfBlindTargets : IModuleReferenceTargets
    {
        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReferenceState>>(new Dictionary<Guid, ReferenceState>());

        public Task<ReferenceFindings> NotPresentAsync(
            string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            Task.FromResult(new ReferenceFindings([],
            [
                // Постоянная оговорка: не проверяется по построению — сомнением не считается.
                new("invoices", "data", UncheckedReason.MixedTargets, "счета, где запись выбрана в дополнительном поле"),
                new("invoice_lines", "nomenclature_id", UncheckedReason.Unreadable, "строки счетов с этой позицией"),
            ], DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// <b>«Не проверено» — не ноль.</b> Опрос, отказавший на колонке, не находит в ней ничего, и число под
    /// отбором выходит нулём; без названной причины экран показал бы «наводить нечего». Причина едет и с
    /// числом готового отбора, и подписью колонки таблицы.
    /// </summary>
    [Fact]
    public async Task Непроверенная_колонка_названа_у_числа_и_у_колонки_а_постоянная_оговорка_нет()
    {
        var (admin, _) = await SignInAsync("Admin");
        await using var blind = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleReferenceTargets>();
            services.AddScoped<IModuleReferenceTargets, HalfBlindTargets>();
        }));
        var client = blind.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        var shortcuts = await ShortcutsAsync(client);
        Assert.All(shortcuts.EnumerateArray(), s =>
        {
            Assert.Equal(0, s.GetProperty("count").GetInt32());
            Assert.Equal("проверено не всё: не прочитано колонок со ссылками — 1", s.GetProperty("unchecked").GetString());
        });

        var table = await client.GetFromJsonAsync<JsonElement>($"/api/tables/costs.invoices?columns=Номер,{LostColumn}&limit=1");
        var notes = table.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("note").GetString());
        Assert.StartsWith("проверено не всё", notes[LostColumn]);
        Assert.Null(notes["Номер"]);

        // На здоровом опросе непроверенным остаётся только дополнительное поле счёта — и сомнения нет:
        // оговорка, которая стоит всегда, у числа ничего бы не значила.
        Assert.All((await ShortcutsAsync(admin)).EnumerateArray(),
            s => Assert.Equal(JsonValueKind.Null, s.GetProperty("unchecked").ValueKind));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Фоновый писатель данных объекта не стирает правку формы (issue #1232).
///
/// <para>Перенос ключа поля, починки, перенос картинок, штамп выпуска работали по схеме «прочитал —
/// изменил — сохранил». Форма, сохранённая между их чтением и записью, пропадала под снимком, снятым
/// до неё, и оба получали успех. Теперь такой писатель читает под блокировкой строки
/// (<see cref="IDomainObjectRepository.ReadForUpdateAsync(IReadOnlyCollection{Guid}, CancellationToken)" />).</para>
/// </summary>
[Collection("Integration")]
public class BackgroundWriterRaceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDomainObjectRepository Objects(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private async Task<T> InScopeAsync<T>(Func<IServiceScope, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();
        return await work(scope);
    }

    private Task<DomainObject> EntryAsync(string code) => InScopeAsync(async s =>
    {
        var type = await M(s).Send(new CreateDocumentTypeCommand(
            code, code, DocumentTypeKind.Composite, null,
            J("{'fields':[{'key':'Адрес','type':'string'},{'key':'Метка','type':'string'}]}")));
        return await M(s).Send(new CreateCommonDataEntryCommand(
            "Ромашка", type.Id, J("{'Адрес':'Москва'}"), CatalogScope.System, null));
    });

    private Task<DomainObject> StoredAsync(Guid id) => InScopeAsync(async s =>
        (await M(s).Send(new GetCommonDataEntryQuery(id)))!);

    /// <summary>Правка формы: меняет адрес и название.</summary>
    private Task<DomainObject> FormSavesAsync(Guid id, string seen) => InScopeAsync(s =>
        M(s).Send(new UpdateCommonDataEntryCommand(id, "Ромашка-2", J("{'Адрес':'Тверь'}"), TestAccess.All, seen)));

    /// <summary>Преобразование фонового писателя: добавляет поле, остальное оставляет как есть.</summary>
    private static void Stamp(DomainObject obj)
    {
        var root = JsonNode.Parse(obj.Data.RootElement.GetRawText())!.AsObject();
        root["Метка"] = "фон";
        obj.SetData(JsonDocument.Parse(root.ToJsonString()));
    }

    private static string? Field(DomainObject obj, string key) =>
        obj.Data.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;

    /// <summary>
    /// Главный случай задачи: писатель прочитал объект, форма сохранилась, писатель записал. Раньше
    /// его запись несла адрес «Москва» из снимка — правка формы исчезала.
    /// </summary>
    [Fact]
    public async Task Писатель_прочитавший_до_формы_её_правку_не_стирает()
    {
        var entry = await EntryAsync("RACE_A");

        using var background = fixture.Services.CreateScope();
        var snapshot = (await Objects(background).GetByIdAsync(entry.Id))!;
        Assert.Equal("Москва", Field(snapshot, "Адрес"));

        await FormSavesAsync(entry.Id, entry.Version);

        await using (var rows = await Objects(background).ReadForUpdateAsync([entry.Id]))
        {
            // Тот же объект, что контекст держал со старым адресом, — и он перечитан.
            var fresh = Assert.Single(rows.Objects);
            Assert.Same(snapshot, fresh);
            Assert.Equal("Тверь", Field(fresh, "Адрес"));
            Stamp(fresh);
            await rows.SaveAsync();
        }

        var stored = await StoredAsync(entry.Id);
        Assert.Equal("Тверь", Field(stored, "Адрес"));
        Assert.Equal("фон", Field(stored, "Метка"));
        Assert.Equal("Ромашка-2", stored.DisplayName);
    }

    /// <summary>
    /// Обратный порядок: писатель держит строку, форма приходит со своей версией. Она обязана
    /// дождаться и получить отказ — иначе легла бы поверх записи писателя, собранная до неё.
    /// </summary>
    [Fact]
    public async Task Форма_пришедшая_во_время_записи_ждёт_и_получает_отказ_по_версии()
    {
        var entry = await EntryAsync("RACE_B");

        using var background = fixture.Services.CreateScope();
        Task<DomainObject> form;
        await using (var rows = await Objects(background).ReadForUpdateAsync([entry.Id]))
        {
            form = Task.Run(() => FormSavesAsync(entry.Id, entry.Version));
            // Не «спит ли поток», а «не завершилась ли раньше срока»: без блокировки форма
            // сохранилась бы за миллисекунды.
            Assert.NotSame(form, await Task.WhenAny(form, Task.Delay(TimeSpan.FromMilliseconds(700))));

            Stamp(Assert.Single(rows.Objects));
            await rows.SaveAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(() => form);
        var stored = await StoredAsync(entry.Id);
        Assert.Equal("Москва", Field(stored, "Адрес"));
        Assert.Equal("фон", Field(stored, "Метка"));
        Assert.Equal("Ромашка", stored.DisplayName);
    }

    [Fact]
    public async Task Освобождение_без_сохранения_не_пишет_ничего_и_блокировку_отпускает()
    {
        var entry = await EntryAsync("RACE_C");

        using (var background = fixture.Services.CreateScope())
        await using (var rows = await Objects(background).ReadForUpdateAsync([entry.Id]))
            Stamp(Assert.Single(rows.Objects));

        // Блокировка отпущена: форма сохраняется, и версия у неё прежняя — строку не трогали.
        var saved = await FormSavesAsync(entry.Id, entry.Version);
        Assert.Equal("Тверь", Field(saved, "Адрес"));
        Assert.Null(Field(await StoredAsync(entry.Id), "Метка"));
    }

    [Fact]
    public async Task Удалённой_строки_в_прочитанном_нет()
    {
        var entry = await EntryAsync("RACE_D");
        using var background = fixture.Services.CreateScope();
        await Objects(background).GetByIdAsync(entry.Id);

        await InScopeAsync(async s => { await M(s).Send(new DeleteCommonDataEntryCommand(entry.Id)); return 0; });

        await using var rows = await Objects(background).ReadForUpdateAsync([entry.Id]);
        Assert.Empty(rows.Objects);
    }

    /// <summary>Отбор по условию берёт те же строки, что и по идентификаторам.</summary>
    [Fact]
    public async Task Чтение_по_условию_блокирует_отобранное()
    {
        var entry = await EntryAsync("RACE_E");
        using var background = fixture.Services.CreateScope();

        await using (var rows = await Objects(background).ReadForUpdateAsync(o => o.CompositeTypeId == entry.CompositeTypeId))
        {
            Stamp(Assert.Single(rows.Objects));
            await rows.SaveAsync();
        }

        Assert.Equal("фон", Field(await StoredAsync(entry.Id), "Метка"));
    }
}

using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Несколько источников на одну консолидацию системного набора (issue #717).
///
/// «Документы комплекта» законно нужны дважды: список актов в один реестр и список протоколов в
/// другой — одни и те же живые данные, разные фильтры, разные поля документа. Раньше кандидат
/// исчезал, как только консолидацию заняли, и второй список можно было получить только через
/// «Создать копию» в меню строки — вход, о котором никто не догадывался.
///
/// Проверяется не наличие кнопки, а то, ради чего она нужна: две копии на одном маркере живут
/// своей жизнью (правка одной не трогает другую), а копия наследует настроенное — обработку, тэги и
/// материализацию вместе с правилом выбора варианта (issue #716), иначе копию пришлось бы
/// донастраивать заново, ради чего её и не делают.
/// </summary>
[Collection("Integration")]
public class MultipleSourcesPerConsolidationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private const string Marker = "system:set-documents";

    /// <summary>Комплект с двумя документами разных типов — по ним и будут расходиться фильтры.</summary>
    private static async Task<(Guid SetId, Guid AosrTypeId)> SeedSetAsync(IServiceScope scope)
    {
        var m = M(scope);
        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"A{Guid.NewGuid():N}"[..10],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var protocolType = await m.Send(new CreateDocumentTypeCommand("Протокол", $"P{Guid.NewGuid():N}"[..10],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));

        var aosr = await m.Send(new AddDocumentToSetCommand(set.Id, aosrType.Id));
        await m.Send(new RenameDocumentInstanceCommand(aosr.Id, "АОСР №1"));
        var protocol = await m.Send(new AddDocumentToSetCommand(set.Id, protocolType.Id));
        await m.Send(new RenameDocumentInstanceCommand(protocol.Id, "Протокол №1"));

        return (set.Id, aosrType.Id);
    }

    private static object Filter(string column, string op, string value) =>
        JsonSerializer.Deserialize<object>(
            $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}]}""")!;

    /// <summary>
    /// Занятая консолидация остаётся в кандидатах со счётчиком. Пока она исчезала, «добавить второй
    /// список» не имело входа вовсе — это и есть суть issue #717, поэтому проверяем именно счётчик,
    /// а не просто «кандидат вернулся».
    /// </summary>
    [Fact]
    public async Task OccupiedCandidate_StaysVisibleWithCount()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _) = await SeedSetAsync(scope);
        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);

        Assert.Equal(0, (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single().ExistingCount);

        await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", Marker, null), default);
        var afterFirst = Assert.Single(await svc.DetectSourceCandidatesAsync(file.Id, default));
        Assert.Equal(Marker, afterFirst.SheetOrPath);
        Assert.Equal(1, afterFirst.ExistingCount);

        await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", Marker, null), default);
        Assert.Equal(2, (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single().ExistingCount);
    }

    /// <summary>
    /// Два источника на одном маркере — разные строки и независимые правки. Строки системного
    /// источника собираются провайдером по маркеру, общему у обоих: если бы обработка бралась не с
    /// самого источника, второй список молча повторял бы первый.
    /// </summary>
    [Fact]
    public async Task TwoSourcesOnSameMarker_FilterIndependently()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _) = await SeedSetAsync(scope);
        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);

        var acts = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", Marker, null), default);
        var protocols = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", Marker, null), default);

        await svc.SetSourceProcessingAsync(acts.Id,
            new SetSourceProcessingInput(Filter("Наименование", "starts_with", "АОСР"), null, null), default);
        await svc.SetSourceProcessingAsync(protocols.Id,
            new SetSourceProcessingInput(Filter("Наименование", "starts_with", "Протокол"), null, null), default);

        var actsPreview = (await svc.PreviewSourceAsync(acts.Id, 50, default))!;
        Assert.Equal("АОСР №1", Assert.Single(actsPreview.Rows)[actsPreview.Columns.ToList().IndexOf("Наименование")]);
        var protocolsPreview = (await svc.PreviewSourceAsync(protocols.Id, 50, default))!;
        Assert.Equal("Протокол №1", Assert.Single(protocolsPreview.Rows)[protocolsPreview.Columns.ToList().IndexOf("Наименование")]);

        // Правка одного не трогает другой: снимаем фильтр у актов — протоколы остаются при своём.
        await svc.SetSourceProcessingAsync(acts.Id, new SetSourceProcessingInput(null, null, null), default);
        Assert.Equal(2, (await svc.PreviewSourceAsync(acts.Id, 50, default))!.Rows.Count);
        Assert.Single((await svc.PreviewSourceAsync(protocols.Id, 50, default))!.Rows);
    }

    /// <summary>
    /// Копия наследует обработку, тэги и материализацию вместе с правилом выбора варианта. Без
    /// этого копия «того же источника с другим фильтром» требовала бы настроить тип, маппинг и
    /// правило заново — то есть ровно ту работу, ради экономии которой копию и делают.
    /// </summary>
    [Fact]
    public async Task Duplicate_InheritsProcessingTagsAndMaterialization()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, aosrTypeId) = await SeedSetAsync(scope);
        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", Marker, null), default);

        var unionType = await M(scope).Send(new CreateDocumentTypeCommand("Документ комплекта", $"U{Guid.NewGuid():N}"[..10],
            DocumentTypeKind.Composite, null,
            J($"{{'tags':['type.union'],'fields':[{{'key':'АОСР','type':'doc-ref','typeId':'{aosrTypeId}'}}]}}")));
        var discriminator = new MaterializeDiscriminatorConfig(
            "Ид", MaterializeDiscriminatorConfig.ByDocumentId, new() { ["АОСР"] = [aosrTypeId] });

        await svc.SetSourceProcessingAsync(source.Id,
            new SetSourceProcessingInput(Filter("Наименование", "starts_with", "АОСР"), null, null), default);
        await svc.SetMaterializationAsync(source.Id, unionType.Id, new() { ["АОСР"] = "Ид" }, discriminator, default);
        // Тэги источника ставятся не через IDataSetService (у обычных источников их проставляет
        // распознавание) — для проверки копирования пишем их прямо в хранилище.
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var entity = await db.DataSetSources.FirstAsync(s => s.Id == source.Id);
        entity.SetTags("""["registry"]""");
        await db.SaveChangesAsync();

        var copy = await svc.DuplicateSourceAsync(source.Id, "Протоколы", default);

        Assert.NotNull(copy);
        Assert.Equal("Протоколы", copy!.Name);
        Assert.Equal(Marker, copy.SheetOrPath);
        Assert.Equal(unionType.Id, copy.MaterializeTypeId);
        Assert.Equal("Ид", copy.MaterializeMapping!["АОСР"]);
        Assert.Equal(MaterializeDiscriminatorConfig.ByDocumentId, copy.MaterializeDiscriminator!.Kind);
        Assert.Equal([aosrTypeId], copy.MaterializeDiscriminator.Rules["АОСР"]);
        Assert.Contains("registry", copy.Tags!);
        // Фильтр проверяем действием, а не сравнением JSON: важно, что копия отбирает те же строки.
        Assert.Single((await svc.PreviewSourceAsync(copy.Id, 50, default))!.Rows);

        // Копия — самостоятельный источник: снятие материализации у неё не задевает оригинал.
        await svc.SetMaterializationAsync(copy.Id, null, null, null, default);
        var original = (await svc.ListSourcesAsync(file.Id, default)).Single(s => s.Id == source.Id);
        Assert.Equal(unionType.Id, original.MaterializeTypeId);
    }

    /// <summary>
    /// Имя обязано быть свободным: в селекторе привязки видно только «Название · N строк», и два
    /// «Документы комплекта» подряд означают выбор наугад. Закрыты все входы записи имени, а не
    /// один диалог, — переименование туда же.
    /// </summary>
    [Fact]
    public async Task DuplicateName_IsRejectedOnEveryWritePath()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _) = await SeedSetAsync(scope);
        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var first = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", Marker, null), default);
        var second = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", Marker, null), default);

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            svc.CreateSourceAsync(file.Id, new CreateSourceInput("акты", Marker, null), default));
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            svc.DuplicateSourceAsync(first.Id, "Протоколы", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            svc.RenameSourceAsync(second.Id, "Акты", default));

        // Переименование источника в его же имя — не конфликт: пользователь правит регистр или пробел.
        Assert.NotNull(await svc.RenameSourceAsync(second.Id, "Протоколы", default));

        // Без имени сервер сам берёт свободное — вторая копия не спотыкается о первую.
        var copy = await svc.DuplicateSourceAsync(first.Id, null, default);
        var copy2 = await svc.DuplicateSourceAsync(first.Id, null, default);
        Assert.Equal("Акты — 2", copy!.Name);
        Assert.Equal("Акты — 3", copy2!.Name);
    }

    /// <summary>
    /// Наборы прошлых версий вполне могут содержать два источника с одним именем — уникальности до
    /// этого правила не было. Такой источник обязан остаться редактируемым: правка, не трогающая
    /// имя, не может отказывать ссылкой на название, которого пользователь не менял.
    /// </summary>
    [Fact]
    public async Task LegacyDuplicateNames_DoNotBlockEditsThatKeepTheName()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);

        var csv = "A;B\n1;2\n";
        var file = await svc.UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes(csv), "d.csv", "text/csv", "Тест", "System", null), default);
        var marker = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single().SheetOrPath;
        var first = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Лист", marker, null), default);
        var second = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Лист (второй)", marker, null), default);

        // Совпадение имён мимо сервиса — так они и достались бы из наборов до issue #717.
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        (await db.DataSetSources.FirstAsync(s => s.Id == second.Id)).Rename("Лист");
        await db.SaveChangesAsync();

        // Правка определения, не трогающая имя, проходит — хотя двойник рядом и живой.
        var updated = await svc.UpdateSourceAsync(second.Id, new UpdateSourceInput("Лист", marker, null), default);
        Assert.Equal("Лист", updated!.Name);
        // И переименование в своё же имя — тоже: иначе выхода, кроме переименования, не остаётся.
        Assert.Equal("Лист", (await svc.RenameSourceAsync(second.Id, "Лист", default))!.Name);

        // А занять чужое имя по-прежнему нельзя — послабление касается только неизменного имени.
        var third = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Третий", marker, null), default);
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            svc.RenameSourceAsync(third.Id, "Лист", default));
        Assert.NotEqual(first.Id, third.Id);
    }
}

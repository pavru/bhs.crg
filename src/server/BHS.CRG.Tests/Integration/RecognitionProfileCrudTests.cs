using BHS.CRG.Api.Modules;
using BHS.CRG.Tests.Support;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// CRUD библиотеки профилей (issue #408). Под защитой — правила, нарушение которых ломает систему
/// молча: несущие поля вида нельзя потерять, встроенный профиль нельзя удалить, вид не меняется.
/// </summary>
[Collection("Integration")]
public class RecognitionProfileCrudTests(IntegrationTestFixture fixture)
{
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<IServiceScope> SeededScopeAsync()
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RecognitionProfiles.RemoveRange(db.RecognitionProfiles.Where(p => p.Code == null));
        await db.SaveChangesAsync();
        await RecognitionProfileSeeder.SeedAsync(db, TestRecognition.Catalog);
        db.ChangeTracker.Clear();
        return scope;
    }

    [Fact]
    public async Task List_ReturnsBuiltInsWithKindInfo()
    {
        using var scope = await SeededScopeAsync();
        var list = await M(scope).Send(new ListRecognitionProfilesQuery());

        // Хост — установка по умолчанию, включён только модуль ИД: профиль выключенного модуля
        // счетов объявлен, но в списке его нет (issue #1077).
        var catalog = scope.ServiceProvider.GetRequiredService<RecognitionProfileCatalog>();
        Assert.Equal(catalog.All.Count(d => catalog.IsAvailable(d.Kind)), list.Count(p => p.IsBuiltIn));
        Assert.DoesNotContain(list, p => p.Kind == RecognitionProfileKind.Invoice.ToString());

        // UI не должен знать частных случаев вида — всё нужное приходит в KindInfo.
        var stamp = list.Single(p => p.Code == IdRecognitionProfiles.TitleBlockCode);
        Assert.True(stamp.KindInfo.HasScalarFields);
        Assert.False(stamp.KindInfo.IsTabular);
        Assert.Contains("НаименованиеДокумента", stamp.KindInfo.SystemFieldNames);

        var cable = list.Single(p => p.Code == IdRecognitionProfiles.CableJournalCode);
        Assert.True(cable.KindInfo is { IsTabular: true, SupportsShape: true });
        Assert.NotEmpty(cable.RowColumns);
    }

    [Fact]
    public async Task Update_RejectsRemovalOfSystemField()
    {
        using var scope = await SeededScopeAsync();
        var m = M(scope);
        var stamp = (await m.Send(new ListRecognitionProfilesQuery()))
            .Single(p => p.Code == IdRecognitionProfiles.TitleBlockCode);

        var without = stamp.Fields.Where(f => f.Name != "НаименованиеДокумента").ToList();
        var ex = await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new UpdateRecognitionProfileCommand(stamp.Id, stamp.Name, without, stamp.RowColumns, stamp.Shape)));
        Assert.Contains("НаименованиеДокумента", ex.Message);

        // Переименование = потеря ключа, на который завязан код, — тоже запрещено.
        var renamed = stamp.Fields
            .Select(f => f.Name == "Шифр" ? f with { Name = "Обозначение" } : f).ToList();
        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new UpdateRecognitionProfileCommand(stamp.Id, stamp.Name, renamed, stamp.RowColumns, stamp.Shape)));
    }

    [Fact]
    public async Task Update_AllowsDescriptionEditAndExtraField_MarksModified()
    {
        using var scope = await SeededScopeAsync();
        var m = M(scope);
        var stamp = (await m.Send(new ListRecognitionProfilesQuery()))
            .Single(p => p.Code == IdRecognitionProfiles.TitleBlockCode);

        var edited = stamp.Fields
            .Select(f => f.Name == "Масштаб" ? f with { Description = "Масштаб чертежа, напр. 1:100" } : f)
            .Append(new RecognitionProfileField("Стадия", "Стадия документации"))
            .ToList();

        var saved = await m.Send(new UpdateRecognitionProfileCommand(
            stamp.Id, stamp.Name, edited, stamp.RowColumns, stamp.Shape));

        Assert.True(saved.IsModified);
        Assert.Contains(saved.Fields, f => f.Name == "Стадия");
        Assert.Equal("Масштаб чертежа, напр. 1:100", saved.Fields.Single(f => f.Name == "Масштаб").Description);

        // Возврат к заводскому — и правка исчезает.
        var reset = await m.Send(new ResetRecognitionProfileCommand(stamp.Id));
        Assert.False(reset.IsModified);
        Assert.DoesNotContain(reset.Fields, f => f.Name == "Стадия");
    }

    [Fact]
    public async Task Update_RejectsDuplicateAndEmptyNames()
    {
        using var scope = await SeededScopeAsync();
        var m = M(scope);
        var cable = (await m.Send(new ListRecognitionProfilesQuery()))
            .Single(p => p.Code == IdRecognitionProfiles.CableJournalCode);

        var dup = cable.RowColumns.Append(cable.RowColumns[0]).ToList();
        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new UpdateRecognitionProfileCommand(cable.Id, cable.Name, cable.Fields, dup, cable.Shape)));

        var empty = cable.RowColumns.Append(new RecognitionProfileField("   ")).ToList();
        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new UpdateRecognitionProfileCommand(cable.Id, cable.Name, cable.Fields, empty, cable.Shape)));
    }

    [Fact]
    public async Task Delete_RefusesBuiltIn_AllowsCustom()
    {
        using var scope = await SeededScopeAsync();
        var m = M(scope);

        var builtIn = (await m.Send(new ListRecognitionProfilesQuery())).First(p => p.IsBuiltIn);
        await Assert.ThrowsAsync<ConflictException>(() => m.Send(new DeleteRecognitionProfileCommand(builtIn.Id)));

        var custom = await m.Send(new CreateRecognitionProfileCommand(
            "Список деталей шкафа", nameof(RecognitionProfileKind.Table),
            [], [new RecognitionProfileField("Поз", "Позиция")],
            new RecognitionTableShape(TwoTierHeader: true)));
        Assert.False(custom.IsBuiltIn);
        Assert.Null(custom.Code);
        Assert.True(custom.Shape!.TwoTierHeader);

        await m.Send(new DeleteRecognitionProfileCommand(custom.Id));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await db.RecognitionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == custom.Id));
    }

    [Fact]
    public async Task Create_RejectsUnknownKindAndEmptyParameters()
    {
        using var scope = await SeededScopeAsync();
        var m = M(scope);

        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(new CreateRecognitionProfileCommand(
            "Профиль", "НетТакогоВида", [], [new RecognitionProfileField("A")], null)));

        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(new CreateRecognitionProfileCommand(
            "Профиль", nameof(RecognitionProfileKind.Table), [], [], null)));
    }

    [Fact]
    public async Task Kinds_AreListedForPicker()
    {
        using var scope = await SeededScopeAsync();
        var kinds = await M(scope).Send(new ListRecognitionKindsQuery());
        // Вид выключенного модуля в перечне не предлагается: остаются виды модуля ИД.
        var catalog = scope.ServiceProvider.GetRequiredService<RecognitionProfileCatalog>();
        Assert.Equal(Enum.GetValues<RecognitionProfileKind>().Count(catalog.IsAvailable), kinds.Count);
        Assert.DoesNotContain(kinds, k => k.Kind == RecognitionProfileKind.Invoice.ToString());
        Assert.All(kinds, k => Assert.False(string.IsNullOrWhiteSpace(k.Label)));
    }
}

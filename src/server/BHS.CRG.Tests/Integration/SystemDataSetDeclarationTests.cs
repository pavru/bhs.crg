using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Живая половина правила о параметре доступа (ТЗ CORE-24.1, CORE-24.3, issue #965): объявления
/// НАСТОЯЩИХ поставщиков ядра и отказы на настоящих путях чтения.
///
/// <para>Зачем живой хост, если ворота — чистая логика. Потому что дефект здесь не в логике, а в
/// СОСТАВЕ: поставщик, которому забыли объявить параметр, и ключ доступа с опечаткой компилируются
/// оба. Первый останавливает старт, второй — нет: набор с несуществующим правом не откроется никому,
/// включая администратора, и выглядеть это будет как «мне не дали прав».</para>
/// </summary>
[Collection("Integration")]
public class SystemDataSetDeclarationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    [Fact]
    public void Все_поставщики_ядра_объявлены_полностью()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();

        var providers = scope.ServiceProvider.GetRequiredService<SystemDataProviderRegistry>();

        // Пять — число из ТЗ (CORE-24.1, сверка 20.09.2026). Не педантизм: поставщик, выпавший из
        // регистрации, унёс бы с собой и свои ворота, а прогон объявлений остался бы зелёным.
        Assert.Equal(5, providers.All.Count);
        providers.EnsureDeclared();
    }

    [Fact]
    public void Ключи_объявлений_существуют_в_справочнике_прав_или_среди_модулей()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();

        var providers = scope.ServiceProvider.GetRequiredService<SystemDataProviderRegistry>();
        var permissions = scope.ServiceProvider.GetRequiredService<PermissionCatalog>();
        var modules = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var known = modules.Enabled.Concat(modules.Disabled).Select(m => m.Code)
            .Append(SystemDataSetDeclaration.CoreModule).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in providers.All.Select(p => p.Declaration))
        {
            Assert.True(known.Contains(declaration.Module),
                $"модуля «{declaration.Module}» нет в сборке");
            Assert.True(permissions.Declares(declaration.Requires) || known.Contains(declaration.Requires),
                $"ключа «{declaration.Requires}» нет ни в справочнике прав, ни среди модулей");
        }
    }

    [Fact]
    public async Task Предпросмотр_без_права_отказывает_а_не_отдаёт_пустую_таблицу()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var (fileId, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        // Ключ общих данных снят, остальные оставлены: отказ обязан назвать ИМЕННО его.
        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.PreviewSourceAsync(sourceId, 50, TestAccess.With("id.document.read"), default));
        Assert.Contains("core.catalog.read", refusal.Message);

        // И тем же ключом закрыта выгрузка: путей чтения пять, и правило одно на все.
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.ExportSourceAsync(sourceId, "xlsx", TestAccess.With("id.document.read"), default));

        // С ключом — строки на месте. Без этой половины прогон не отличал бы ворота от поломки.
        var preview = await svc.PreviewSourceAsync(sourceId, 50, TestAccess.All, default);
        Assert.NotNull(preview);
        Assert.Single(preview.Rows);

        // Список наборов НЕ падает на закрытом источнике: живое число строк подменяется
        // запомненным, и страница остаётся читаемой (см. SystemSourceCounter).
        var files = await svc.ListFilesAsync(null, null, false, TestAccess.With("id.document.read"), default);
        Assert.Contains(files, f => f.Id == fileId);
    }

    [Fact]
    public async Task Служебное_задание_опубликованный_набор_не_читает()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => svc.PreviewSourceAsync(
            sourceId, 50, DataAccess.OfSystem("плановая копия"), default));

        Assert.Contains("только по правам человека", refusal.Message);
    }

    [Fact]
    public async Task Недоступный_набор_в_кандидатах_не_предлагается()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        await SeedCommonDataEntryAsync(scope);

        // Кандидат несёт ЧИСЛО своих строк, то есть предложить набор значит уже его прочитать.
        var closed = await svc.ListSystemCandidatesAsync("System", null,
            TestAccess.With("id.document.read"), default);
        Assert.DoesNotContain(closed, c => c.SheetOrPath.StartsWith("system:objects:"));

        var open = await svc.ListSystemCandidatesAsync("System", null, TestAccess.All, default);
        Assert.Contains(open, c => c.SheetOrPath.StartsWith("system:objects:"));
    }

    [Fact]
    public async Task Задание_без_живого_автора_опубликованных_наборов_не_читает()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var resolver = scope.ServiceProvider.GetRequiredService<DataAccessResolver>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        // Учётную запись автора могли удалить, пока задача стояла в очереди. Чьи-то другие права
        // взять неоткуда, поэтому параметр доступа получается БЕЗ ЧЕЛОВЕКА — и отбираемые по правам
        // строки такому не отдаются. Причина обязана быть названа: «нет прав» без слов об удалённом
        // авторе отправило бы администратора выдавать права тому, кого уже нет.
        var ghost = await resolver.ForUserAsync(Guid.NewGuid(), default);
        Assert.True(ghost.IsSystem);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            svc.PreviewSourceAsync(sourceId, 50, ghost, default));
        Assert.Contains("автора задачи больше нет", refusal.Message);
    }

    // ── Данные прогона ────────────────────────────────────────────────────────

    /// <summary>Запись общих данных составного типа — сырьё консолидации «Общие данные: {тип}».</summary>
    private static async Task<Guid> SeedCommonDataEntryAsync(IServiceScope scope)
    {
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = await m.Send(new CreateDocumentTypeCommand("Материал", "Material",
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Наименование','type':'string'}]}")));
        await m.Send(new CreateCommonDataEntryCommand("Кабель", type.Id,
            J("{'Наименование':'ВВГ 3х2.5'}"), Domain.Catalog.CatalogScope.System, null));
        return type.Id;
    }

    private static async Task<(Guid FileId, Guid SourceId)> SeedCommonDataSourceAsync(
        IServiceScope scope, IDataSetService svc)
    {
        var typeId = await SeedCommonDataEntryAsync(scope);

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("System", null, null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Материалы", $"system:objects:{typeId}", null), TestAccess.All, default);
        return (file.Id, source.Id);
    }
}

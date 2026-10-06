using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Backup;
using System.Text;
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
        // Плюс по поставщику на каждую таблицу модуля (G1b, issue #1089) — включая таблицы
        // выключенных модулей: источник на них отвечает «модуль не подключён», а не пропадает.
        var tables = scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.Tables.ModuleTableCatalog>().All.Count;
        Assert.True(tables > 0, "Таблица счетов объявлена модулем costs — поставщиков таблиц не может быть ноль.");
        Assert.Equal(5 + tables, providers.All.Count);
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

    // ── Граница выдачи доходит до человека и до агента (ТЗ CORE-24.3) ─────────

    [Fact]
    public async Task Подпись_стоит_в_предпросмотре_выгрузке_и_ответе_агенту()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IDataSnapshotService>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        const string expected = "Отдаёт все записи выбранного типа";

        var preview = await svc.PreviewSourceAsync(sourceId, 50, TestAccess.All, default);
        Assert.Contains(expected, preview!.Boundary);

        // Выгрузка уходит из системы и живёт своей жизнью: по самому файлу не узнать ни чьими
        // правами он снят, ни того, все ли это строки. Поэтому подпись — ПЕРВОЙ строкой листа XLSX,
        // над заголовком колонок (ТЗ CORE-24.3 называет именно XLSX).
        var xlsx = await svc.ExportSourceAsync(sourceId, "xlsx", TestAccess.All, default);
        Assert.StartsWith(expected, FirstCellOf(xlsx!.Content));

        // ⚠️ А в CSV подписи НЕТ, и это решение: своего места под примечание формат не имеет, строка
        // сдвинула бы заголовок колонок на вторую — и такой файл, загруженный обратно набором
        // данных, разобрался бы с подписью вместо имён колонок (ревью PR #1057).
        var csv = await svc.ExportSourceAsync(sourceId, "csv", TestAccess.All, default);
        var firstLine = Encoding.UTF8.GetString(csv!.Content).Split('\n')[0];
        Assert.DoesNotContain(expected, firstLine);

        // Агенту нужнее, чем человеку: человек видит подпись рядом с таблицей, а агент строит на этих
        // строках сверку и без границы сочтёт их полными.
        var detail = await snapshots.GetSourceAsync(sourceId, TestAccess.All, default);
        Assert.Contains(expected, detail!.Boundary);
        var rows = await snapshots.GetRowsAsync(sourceId, 0, 10, TestAccess.All, ct: default);
        Assert.Contains(expected, rows!.Boundary);

        // Номер контракта поднят вместе с полем: пока агент не увидит 10, он не знает, что строки
        // отбираются по правам, — и сочтёт выборку полной. Сверяем «не ниже», а не «ровно»: контракт
        // растёт и дальше (11 — причина пустой таблицы у документа), и точное число превращало бы
        // этот сторож в препятствие следующему полю, ничего при этом не стерегая.
        Assert.True(rows.ContractVersion >= 10, $"контракт MCP: {rows.ContractVersion}");
    }

    [Fact]
    public async Task У_файлового_источника_подписи_нет()
    {
        // Обратная половина: подпись принадлежит опубликованному набору, а не всякой таблице. Стой
        // она везде, её перестали бы читать — ровно та судьба, что у предупреждения на каждом экране.
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var sourceId = await SeedCsvSourceAsync(scope, svc);

        var preview = await svc.PreviewSourceAsync(sourceId, 50, TestAccess.All, default);
        Assert.Null(preview!.Boundary);
    }

    // ── Чего опубликованный набор не делает (ТЗ CORE-24.2) ────────────────────

    [Fact]
    public async Task К_записи_общих_данных_источник_не_привязывается_а_к_документу_да()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        var entry = (await m.Send(new ListCommonDataEntriesQuery(RecordsFor.Display))).First();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => svc.CreateBindingAsync(
            new CreateBindingInput(entry.Id, sourceId, "Наименование", null), default));
        Assert.Contains("переживут отзыв права", refusal.Message);

        // А к документу комплекта — привязывается: там строки собираются в момент генерации.
        var document = await SeedDocumentAsync(scope);
        var binding = await svc.CreateBindingAsync(
            new CreateBindingInput(document, sourceId, "Таблица", null), default);
        Assert.NotNull(binding);
    }

    [Fact]
    public async Task Строки_опубликованного_набора_не_кэшируются_и_не_едут_в_копию()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        // Читаем всеми путями, которые могли бы «попутно» сохранить разобранное.
        _ = await svc.PreviewSourceAsync(sourceId, 50, TestAccess.All, default);
        _ = await svc.ExportSourceAsync(sourceId, "xlsx", TestAccess.All, default);
        _ = await svc.ListSourcesAsync(
            (await db.DataSetSources.AsNoTracking().FirstAsync(x => x.Id == sourceId)).FileId,
            TestAccess.All, default);

        // Кеша у системного источника нет уже сейчас — и это правило, а не совпадение (ТЗ CORE-24.2):
        // сохранённые строки переживут отзыв права, а снимок, снятый обладателем «читать всё», потом
        // читал бы любой. Прогон закрепляет: кеш не появляется ни одной будущей правкой.
        var source = await db.DataSetSources.AsNoTracking().FirstAsync(s => s.Id == sourceId);
        Assert.Null(source.CachedData);

        // В резервную копию строки не едут тем же следствием: копия выгружает CachedData источников
        // (иначе восстановленный файловый источник приехал бы пустым), и пустой кеш — единственная
        // причина, по которой строк опубликованного набора там не окажется.
        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .ExportAsync(BackupScope.Full);
        await using var _handle = zip;
        using var ms = new MemoryStream();
        await zip.CopyToAsync(ms);
        Assert.DoesNotContain("ВВГ 3х2.5", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Набор_с_изоляцией_к_печатной_форме_не_привязывается()
    {
        // ⚠️ В этапе 1 ни один поставщик изоляции не объявляет, поэтому правило проверяется
        // ПОДСТАВНЫМ объявлением на настоящей базе: служба собирается руками, с реестром из одного
        // поставщика, объявившего отбор по правам. Иначе сторож был бы зелёным всегда — та же
        // ловушка, что в #962, где «завести внешний ключ с каскадом» проверяло ключ, которого в том
        // хранении не существует.
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);
        var document = await SeedDocumentAsync(scope);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = (await db.DataSetSources.AsNoTracking().FirstAsync(x => x.Id == sourceId)).SheetOrPath;
        var isolated = new DataSetBindingService(
            db,
            scope.ServiceProvider.GetRequiredService<IDataSetRowLoader>(),
            new SystemDataProviderRegistry([new IsolatedProvider(marker)]),
            NullLogger<DataSetBindingService>.Instance);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => isolated.CreateBindingAsync(
            new CreateBindingInput(document, sourceId, "Таблица", null), default));

        Assert.Contains("печатная форма", refusal.Message);
        Assert.Contains("после подписи", refusal.Message);
    }

    /// <summary>Поставщик с построчной изоляцией — которого в этапе 1 ещё нет.</summary>
    private sealed class IsolatedProvider(string marker) : ISystemDataProvider
    {
        public SystemDataSetDeclaration Declaration { get; } = new(
            SystemDataSetDeclaration.CoreModule, "core.catalog.read", SystemDataSetIsolation.PerUser,
            ["Отдаёт то, что видно вам", "Отдаёт всё — право «читать всё»"]);

        public bool Handles(string m) => m == marker;

        public Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
            Domain.Catalog.CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

        public Task<DataSetParseResult> ProvideAsync(string m, Domain.Catalog.CatalogScope scope,
            Guid? scopeId, DataAccess access, CancellationToken ct)
            => Task.FromResult(new DataSetParseResult([], []));
    }

    // ── Данные прогона ────────────────────────────────────────────────────────

    /// <summary>Запись общих данных составного типа — сырьё консолидации «Общие данные: {тип}».</summary>
    private static async Task<Guid> SeedCommonDataEntryAsync(IServiceScope scope)
    {
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        // Тип МАТЕРИАЛЬНЫЙ: поле-ссылка на документ качества плюс поле идентичности. Без них ключ
        // идентичности материала пуст, и дозаполнение подписей выходит раньше, чем дойдёт до
        // привязок, — прогон проверял бы не то (ТЗ TYPE-21, MaterialIdentity.KeysOf).
        var type = await m.Send(new CreateDocumentTypeCommand("Материал", "Material",
            DocumentTypeKind.Composite, null, J(
                "{'fields':[" +
                "{'key':'Наименование','type':'string','tags':['identity:1']}," +
                "{'key':'Сертификат','type':'string','tags':['material.qualityDocLink']}]}")));
        await m.Send(new CreateCommonDataEntryCommand("Кабель", type.Id,
            J("{'Наименование':'ВВГ 3х2.5'}"), Domain.Catalog.CatalogScope.System, null));
        return type.Id;
    }

    [Fact]
    public async Task Служебный_проход_не_объявляет_материал_пропавшим_из_за_отказа_ворот()
    {
        // ⚠️ Отказ ворот приходит служебному проходу НЕ исключением: предпросмотр привязок глотает
        // его сам и возвращает элемент со статусом «error». Прежний код читал это как «материалов в
        // этой привязке нет», и связка уезжала в отчёт как «материала больше нет» — при живом
        // материале (нашло ревью PR #1057). Теперь владелец с непрочитанной привязкой попадает в
        // DocumentsFailed, а отчёт говорит вслух, что его материалы в поиске не участвовали.
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);

        // Привязка на опубликованном наборе — к ДОКУМЕНТУ: к записи общих данных её не завести.
        var document = await SeedDocumentAsync(scope);
        await svc.CreateBindingAsync(new CreateBindingInput(document, sourceId, "Таблица", null), default);

        // Связка без подписи — то, ради чего проход и существует. Документ качества настоящий:
        // у связки на него внешний ключ.
        var docType = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new CreateDocumentTypeCommand("Сертификат", "Cert", DocumentTypeKind.Document, null,
                J("{'fields':[]}")));
        var qualityDoc = Domain.Documents.QualityDocument.Create(
            docType.Id, "Сертификат", J("{}"), Domain.Catalog.CatalogScope.System, null,
            Domain.Documents.QualityDocSource.Manual);
        db.QualityDocuments.Add(qualityDoc);
        db.MaterialQualityLinks.Add(Domain.Documents.MaterialQualityLink.Create(
            Domain.Catalog.CatalogScope.System, null, "ВВГ 3х2.5", qualityDoc.Id));
        await db.SaveChangesAsync();

        var report = await new MaterialLabelBackfill(db, svc).RunAsync(dryRun: true);

        Assert.Equal(1, report.DocumentsFailed);
        Assert.Equal(0, report.DocumentsScanned);
    }

    [Fact]
    public async Task Старая_привязка_к_записи_пропускается_с_НАЗВАННОЙ_причиной()
    {
        // Новую такую привязку не создать (см. прогон выше), но заведённая ДО правила в базе
        // заказчика лежать может — поэтому она заводится здесь НАПРЯМУЮ, минуя дверь.
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);
        var entry = (await m.Send(new ListCommonDataEntriesQuery(RecordsFor.Display))).First();

        db.DataSetBindings.Add(Domain.DataSets.DataSetBinding.For(
            entry.Id, sourceId, "Наименование", "{}"));
        await db.SaveChangesAsync();

        // ⚠️ Статус «error», а не «not-found»: резолв не состоялся ВОВСЕ, по правилу, — а not-found
        // означает «значение источника не сматчилось» и отправило бы человека искать пропавшую
        // запись каталога (ревью PR #1057).
        var check = await m.Send(new CheckCommonDataBindingsQuery(entry.Id, TestAccess.All));
        var item = Assert.Single(check.Items, i => i.FieldKey == "Наименование");
        Assert.Equal("error", item.Status);
        Assert.Contains("не сохраняются", item.Detail);

        // И сохранение записи её значения не подмешивает: правка проходит, поле остаётся как было.
        var saved = await m.Send(new UpdateCommonDataEntryCommand(
            entry.Id, "Кабель", J("{'Наименование':'ВВГ 3х2.5'}"), TestAccess.All));
        Assert.Equal("ВВГ 3х2.5", saved.Data.RootElement.GetProperty("Наименование").GetString());
    }

    [Fact]
    public async Task Изоляция_ловится_на_КАЖДОМ_выпуске_а_не_только_при_привязке()
    {
        // Привязка заводится один раз, а поставщик вправе объявить изоляцию позже (этап 3): проверка
        // только на входе оставила бы старые привязки работать, и два инженера получили бы разные
        // акты. Поэтому резолвер генерации собирается руками — с реестром из поставщика, объявившего
        // отбор по правам, — и привязка при этом УЖЕ существует (ревью PR #1057).
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, sourceId) = await SeedCommonDataSourceAsync(scope, svc);
        var documentId = await SeedDocumentAsync(scope);
        await svc.CreateBindingAsync(new CreateBindingInput(documentId, sourceId, "Таблица", null), default);

        var marker = (await db.DataSetSources.AsNoTracking().FirstAsync(x => x.Id == sourceId)).SheetOrPath;
        var resolver = new DataSetResolver(
            db,
            scope.ServiceProvider.GetRequiredService<IDataSetRowLoader>(),
            scope.ServiceProvider.GetRequiredService<Application.Resolution.IObjectResolver>(),
            new SystemDataProviderRegistry([new IsolatedProvider(marker)]),
            NullLogger<DataSetResolver>.Instance);

        // Фасету документа грузим явно: PluginData живёт в ней, а без неё DocumentView.From падает.
        var document = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstAsync(o => o.Id == documentId);
        var diagnostics = new List<Application.Generation.ResolutionDiagnostic>();
        await resolver.InjectAsync(new Application.Generation.GenerationContext(),
            Application.Generation.DocumentView.From(document), TestAccess.All, diagnostics);

        // Отказ приходит диагностикой уровня Error — а он снимает документ с выпуска целиком
        // (GenerateDocumentHandler бросает ResolutionValidationException на любой Error).
        var d = Assert.Single(diagnostics);
        Assert.Equal(Application.Generation.DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("печатная форма", d.Message);
    }

    /// <summary>Первая ячейка первого листа XLSX — там стоит подпись к данным.</summary>
    private static string FirstCellOf(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(ms);
        return wb.GetSheetAt(0).GetRow(0).GetCell(0).StringCellValue;
    }

    /// <summary>Файловый источник (CSV) — для обратной половины правил.</summary>
    private static async Task<Guid> SeedCsvSourceAsync(IServiceScope scope, IDataSetService svc)
    {
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var path = await blob.UploadAsync($"{Guid.NewGuid():N}.csv",
            new MemoryStream(Encoding.UTF8.GetBytes("Имя,Кол\nКабель,10\n")), "text/csv");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = Domain.DataSets.DataSetFile.Create("Файл", Domain.DataSets.DataSetFormat.Csv, path,
            Domain.Catalog.CatalogScope.System, null);
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync();

        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).First();
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Строки", candidate.SheetOrPath, null), TestAccess.All, default);
        return source.Id;
    }

    /// <summary>Документ комплекта — владелец, которому привязка разрешена.</summary>
    private static async Task<Guid> SeedDocumentAsync(IServiceScope scope)
    {
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = await m.Send(new CreateDocumentTypeCommand("Акт", "Act", DocumentTypeKind.Document,
            null, J("{'fields':[{'key':'Таблица','type':'table'}]}")));
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        return (await m.Send(new AddDocumentToSetCommand(set.Id, type.Id))).Id;
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

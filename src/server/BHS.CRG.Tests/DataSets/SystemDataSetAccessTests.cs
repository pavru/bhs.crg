using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Параметр доступа, право и ворота опубликованного набора (ТЗ CORE-24.1, CORE-24.3, issue #965).
///
/// <para>Прогон юнитовый нарочно: и объявление, и ворота — чистая логика, а проверять её через живой
/// хост значило бы поднимать базу ради вычисления, которое ни базы, ни запроса не касается. Живую
/// половину — что объявлены все пять поставщиков ядра и что ключи их объявлений существуют — держит
/// <c>SystemDataSetDeclarationTests</c> на настоящем контейнере.</para>
/// </summary>
public class SystemDataSetAccessTests
{
    /// <summary>Поставщик с любым объявлением: настоящих строк не отдаёт, но запоминает вызов.</summary>
    private sealed class Provider(SystemDataSetDeclaration declaration) : ISystemDataProvider
    {
        public SystemDataSetDeclaration Declaration { get; } = declaration;
        public DataAccess? SeenAccess;

        public bool Handles(string marker) => marker == SystemDataSets.SetDocumentsMarker;

        public Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
            CatalogScope scope, Guid? scopeId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

        public Task<DataSetParseResult> ProvideAsync(
            string marker, CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
        {
            SeenAccess = access;
            return Task.FromResult(new DataSetParseResult(
                [new DataSetColumnInfo("Наименование", ["АОСР 1"])],
                [new Dictionary<string, string?> { ["Наименование"] = "АОСР 1" }]));
        }
    }

    private static SystemDataSetDeclaration Good => new(
        "id", "id.document.read", SystemDataSetIsolation.None, ["Отдаёт документы комплекта"]);

    // ── Объявление: чего не хватает, то и останавливает старт ──────────────────

    [Theory]
    // Без модуля: «модуль не подключён» сказать будет нечем, и набор выключенного модуля
    // перестанет отличаться от набора ядра.
    [InlineData("", "id.document.read", SystemDataSetIsolation.None, true, "не назван модуль")]
    // Без ключа доступа — тот самый случай, ради которого правило и написано: набор «отдаёт всё».
    [InlineData("id", "", SystemDataSetIsolation.None, true, "не назван требуемый ключ")]
    // Вид отбора не выбран. Значение по умолчанию у перечисления есть всегда, поэтому «забыли»
    // обязано отличаться от «решили, что изоляции нет», иначе самое широкое поведение достаётся
    // молчанием.
    [InlineData("id", "id.document.read", SystemDataSetIsolation.Unset, true, "вид отбора")]
    // Без текста границы выдачи: набор молчит о том, что именно он отдал (ТЗ CORE-24.3).
    [InlineData("id", "id.document.read", SystemDataSetIsolation.None, false, "границы выдачи")]
    public void Неполное_объявление_останавливает_регистрацию(
        string module, string requires, SystemDataSetIsolation isolation, bool withBoundary, string expected)
    {
        var declaration = new SystemDataSetDeclaration(
            module, requires, isolation, withBoundary ? ["Отдаёт что-то"] : []);
        var registry = new SystemDataProviderRegistry([new Provider(declaration)]);

        var refusal = Assert.Throws<InvalidOperationException>(registry.EnsureDeclared);

        Assert.Contains(expected, refusal.Message);
        // Отказ обязан называть поставщика: в сборке их несколько, и «набор объявлен не до конца»
        // без имени отправляет читать все пять.
        Assert.Contains(nameof(Provider), refusal.Message);
    }

    [Fact]
    public void Пустая_строка_в_тексте_границы_не_считается_текстом()
    {
        // Пробел проходил бы проверку на количество строк и оставлял бы у данных пустую подпись —
        // хуже отсутствующей: место под объяснение занято, а объяснения нет.
        var registry = new SystemDataProviderRegistry(
            [new Provider(Good with { Boundary = ["   "] })]);

        Assert.Throws<InvalidOperationException>(registry.EnsureDeclared);
    }

    [Fact]
    public void Полное_объявление_проходит()
    {
        var registry = new SystemDataProviderRegistry([new Provider(Good)]);
        registry.EnsureDeclared();
    }

    [Fact]
    public void Отказ_собирается_по_всем_поставщикам_сразу()
    {
        // Чинить объявления по одному на перезапуск — это столько перезапусков, сколько поставщиков.
        var registry = new SystemDataProviderRegistry([
            new Provider(Good with { Requires = "" }),
            new Provider(Good with { Boundary = [] }),
        ]);

        var refusal = Assert.Throws<InvalidOperationException>(registry.EnsureDeclared);

        Assert.Contains("не назван требуемый ключ", refusal.Message);
        Assert.Contains("границы выдачи", refusal.Message);
    }

    // ── Ворота: кого пускаем к строкам ────────────────────────────────────────

    [Fact]
    public void Служебное_задание_опубликованный_набор_не_читает()
    {
        var refusal = Assert.Throws<ConflictException>(() => SystemDataSetGate.Ensure(
            Good, DataAccess.OfSystem("плановая копия"), "Документы комплекта"));

        // Причина названа своими словами: отказ «нет права» отправил бы администратора выдавать
        // права системе, которой их выдать нельзя.
        Assert.Contains("только по правам человека", refusal.Message);
        Assert.Contains("плановая копия", refusal.Message);
    }

    [Fact]
    public void Ключа_нет_отказ_а_не_пустая_таблица()
    {
        var refusal = Assert.Throws<ForbiddenException>(() => SystemDataSetGate.Ensure(
            Good, TestAccess.With("core.catalog.read"), "Документы комплекта"));

        Assert.Contains("id.document.read", refusal.Message);
        Assert.Contains("Документы комплекта", refusal.Message);
    }

    [Fact]
    public void Выключенный_модуль_называет_свою_причину()
    {
        // ⚠️ Отдельная причина, а не «нет права»: включать модуль и выдавать право — разные действия,
        // и одна формулировка на два случая отправила бы администратора не туда (ТЗ CORE-24.3).
        var refusal = Assert.Throws<ConflictException>(() => SystemDataSetGate.Ensure(
            Good, TestAccess.WithoutModules("id.document.read", "id"), "Документы комплекта"));

        Assert.Contains("не подключён", refusal.Message);
    }

    [Fact]
    public void Набор_ядра_не_требует_включённых_модулей()
    {
        // У ядра модуля нет, и требовать его включения значило бы закрыть общие данные на
        // экземпляре без единого модуля.
        SystemDataSetGate.Ensure(
            new(SystemDataSetDeclaration.CoreModule, "core.catalog.read",
                SystemDataSetIsolation.None, ["Отдаёт записи общих данных"]),
            TestAccess.WithoutModules("core.catalog.read"), "Общие данные");
    }

    [Fact]
    public void Ключ_кодом_модуля_годится()
    {
        // Библиотека документов качества своего права не носит: её адреса закрыты воротами модуля
        // (ТЗ AUTH-12.2), и набор обязан спрашивать то же самое.
        SystemDataSetGate.Ensure(
            Good with { Requires = "id" }, TestAccess.With("id"), "Документы качества");
    }

    [Fact]
    public void Открытый_набор_проходит_молча()
    {
        SystemDataSetGate.Ensure(Good, TestAccess.With("id.document.read"), "Документы комплекта");
        Assert.True(SystemDataSetGate.Allows(Good, TestAccess.With("id.document.read")));
        Assert.False(SystemDataSetGate.Allows(Good, TestAccess.With("core.catalog.read")));
    }

    // ── Загрузчик строк: ворота стоят ДО обращения к поставщику ────────────────

    private sealed class NoBlob : IBlobStorage
    {
        public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
            => throw new NotSupportedException("системный набор блоба не читает");
        public Task<string> UploadAsync(string fileName, Stream s, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string blobPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task PutAsync(string blobPath, Stream s, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
    }

    private static (DataSetRowLoader Loader, Provider Provider) Loader(SystemDataSetDeclaration declaration)
    {
        var provider = new Provider(declaration);
        return (new DataSetRowLoader(new NoBlob(), new DataSetParserFactory([]),
            new SystemDataProviderRegistry([provider])), provider);
    }

    private static DataSetSource SystemSource()
    {
        var file = DataSetFile.CreateSystem("Данные системы", CatalogScope.Set, Guid.NewGuid());
        var source = file.AddSource("Реестр документов", SystemDataSets.SetDocumentsMarker, "[]", 0);
        typeof(DataSetSource).GetProperty(nameof(DataSetSource.File))!.SetValue(source, file);
        return source;
    }

    [Fact]
    public async Task Без_ключа_поставщика_даже_не_спрашивают()
    {
        var (loader, provider) = Loader(Good);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            loader.LoadRowsAsync(SystemSource(), TestAccess.With("core.catalog.read"), default));

        // Ворота ПЕРЕД чтением, а не после: собранные и выброшенные строки — это и время, и, что
        // важнее, соблазн однажды вернуть их «частично».
        Assert.Null(provider.SeenAccess);
    }

    [Fact]
    public async Task Параметр_доступа_доходит_до_поставщика()
    {
        // Ради этого и менялся контракт: поставщик с построчной изоляцией (этап 3) отбирает строки
        // сам, и получить пользователя он обязан от вызывающего, а не искать его вокруг себя.
        var (loader, provider) = Loader(Good);
        var access = TestAccess.With("id.document.read");

        var rows = await loader.LoadRowsAsync(SystemSource(), access, default);

        Assert.Single(rows);
        Assert.Same(access, provider.SeenAccess);
    }

    [Fact]
    public async Task Служебное_задание_до_строк_не_доходит()
    {
        var (loader, provider) = Loader(Good);

        await Assert.ThrowsAsync<ConflictException>(() =>
            loader.LoadRowsAsync(SystemSource(), DataAccess.OfSystem("уборка сирот"), default));

        Assert.Null(provider.SeenAccess);
    }
}

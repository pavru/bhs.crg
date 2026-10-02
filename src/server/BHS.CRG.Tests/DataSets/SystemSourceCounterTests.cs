using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Живое состояние системных источников считается ОДНИМ обращением к поставщику на консолидацию, а
/// не по разу на источник (issue #1142).
///
/// <para>Несколько источников на одной консолидации — штатный случай (issue #717): список актов и
/// список протоколов на одних «Документах комплекта». Поставщик собирает строки целиком, и спрошенный
/// по разу на источник он собирал их столько раз, сколько источников в списке. Нашлось это на
/// накопленной тестовой базе: 603 источника на таблице счетов — 603 полных чтения таблицы на один
/// запрос списка наборов, и ответ не укладывался в сто секунд.</para>
///
/// <para>Прогон юнитовый нарочно: считать надо ОБРАЩЕНИЯ к поставщику, а у настоящего их не видно —
/// через живой хост проверялось бы только то, что число строк верное, а оно верное при любом числе
/// обращений.</para>
/// </summary>
public class SystemSourceCounterTests
{
    private const string Documents = "system:проба-документы";
    private const string Materials = "system:проба-материалы";

    /// <summary>
    /// Поставщик, который записывает каждое обращение. Число строк в ответе своё у каждой области —
    /// иначе ответ, по ошибке отданный чужой области, был бы неотличим от правильного.
    /// </summary>
    private sealed class CountingProvider : ISystemDataProvider
    {
        public List<(string Marker, CatalogScope Scope, Guid? ScopeId)> Asked { get; } = [];

        /// <summary>Сколько строк отдавать области; не названная здесь получает одну.</summary>
        public Dictionary<Guid, int> RowsOf { get; } = [];

        /// <summary>Отказывать, как поставщик отказывает на уровне, где консолидация неприменима.</summary>
        public bool Refuses { get; set; }

        /// <summary>Отдавать строки без единой колонки — так выглядит консолидация, которой нечего показать.</summary>
        public bool NoColumns { get; set; }

        public SystemDataSetDeclaration Declaration { get; } = new(
            SystemDataSetDeclaration.CoreModule, "core.catalog.read",
            SystemDataSetIsolation.None, ["Отдаёт записи общих данных"]);

        public bool Handles(string marker) => marker is Documents or Materials;

        public Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
            CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

        public Task<DataSetParseResult> ProvideAsync(
            string marker, CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
        {
            Asked.Add((marker, scope, scopeId));
            if (Refuses) throw new ConflictException("Источник доступен только на уровне комплекта");

            var count = scopeId is { } id && RowsOf.TryGetValue(id, out var known) ? known : 1;
            var rows = Enumerable.Range(1, count)
                .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["Наименование"] = $"Строка {i}" })
                .ToList();
            return Task.FromResult(new DataSetParseResult(
                NoColumns ? [] : [new DataSetColumnInfo("Наименование", [])], rows));
        }
    }

    private static readonly DataAccess Reader = TestAccess.With("core.catalog.read");

    private static (SystemSourceCounter Counter, CountingProvider Provider) Counter()
    {
        var provider = new CountingProvider();
        return (new SystemSourceCounter(new SystemDataProviderRegistry([provider])), provider);
    }

    private static DataSetFile SystemFile(Guid? scopeId = null) =>
        DataSetFile.CreateSystem("Данные системы", CatalogScope.Set, scopeId ?? Guid.NewGuid());

    /// <summary>
    /// Главный сторож: три источника на одной консолидации и один на другой — поставщик спрошен
    /// дважды, а не четырежды, и состояние при этом получил КАЖДЫЙ источник.
    /// </summary>
    [Fact]
    public async Task Источники_одной_консолидации_спрашивают_поставщика_один_раз()
    {
        var (counter, provider) = Counter();
        var file = SystemFile();
        var sources = new[]
        {
            file.AddSource("Акты", Documents, "[]", 0),
            file.AddSource("Протоколы", Documents, "[]", 0),
            file.AddSource("Материалы", Materials, "[]", 0),
            file.AddSource("Исполнительные схемы", Documents, "[]", 0),
        };

        var states = await counter.StateAsync([file], Reader, default);

        Assert.Equal([Documents, Materials], provider.Asked.Select(a => a.Marker));
        // Запомненный ответ достаётся всем, а не только первому спросившему: источник, оставшийся без
        // состояния, показал бы в списке число строк, записанное при его создании.
        Assert.All(sources, s => Assert.Equal(1, states[s.Id].RowCount));
    }

    /// <summary>
    /// Тот же список, собранный не из набора, а из источников, загруженных отдельно (экран
    /// источников одного набора), — второй вход в тот же подсчёт.
    /// </summary>
    [Fact]
    public async Task Источники_загруженные_отдельно_от_набора_считаются_так_же()
    {
        var (counter, provider) = Counter();
        var file = SystemFile();
        var sources = new[]
        {
            file.AddSource("Акты", Documents, "[]", 0),
            file.AddSource("Протоколы", Documents, "[]", 0),
        };

        var states = await counter.StateAsync(file, sources, Reader, default);

        Assert.Single(provider.Asked);
        Assert.Equal(2, states.Count);
    }

    /// <summary>
    /// Ответ поставщика зависит от области набора, поэтому в ключе — и она: у двух комплектов на
    /// одной консолидации строки разные. Запомни мы ответ по одному маркеру, второй комплект
    /// показал бы число строк первого — правдоподобное и неверное.
    /// </summary>
    [Fact]
    public async Task Наборы_разных_областей_ответ_не_делят()
    {
        var (counter, provider) = Counter();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        provider.RowsOf[first] = 3;
        provider.RowsOf[second] = 7;
        var (fileOne, fileTwo) = (SystemFile(first), SystemFile(second));
        var one = fileOne.AddSource("Акты", Documents, "[]", 0);
        var two = fileTwo.AddSource("Акты", Documents, "[]", 0);

        var states = await counter.StateAsync([fileOne, fileTwo], Reader, default);

        Assert.Equal(2, provider.Asked.Count);
        Assert.Equal(3, states[one.Id].RowCount);
        Assert.Equal(7, states[two.Id].RowCount);
    }

    /// <summary>
    /// А два набора ОДНОЙ области ответ делят: до правила «один системный набор на область» их могло
    /// оказаться несколько, и строки у них одни и те же.
    /// </summary>
    [Fact]
    public async Task Наборы_одной_области_делят_один_ответ()
    {
        var (counter, provider) = Counter();
        var scope = Guid.NewGuid();
        var (fileOne, fileTwo) = (SystemFile(scope), SystemFile(scope));
        fileOne.AddSource("Акты", Documents, "[]", 0);
        fileTwo.AddSource("Акты", Documents, "[]", 0);

        var states = await counter.StateAsync([fileOne, fileTwo], Reader, default);

        Assert.Single(provider.Asked);
        Assert.Equal(2, states.Count);
    }

    /// <summary>
    /// Схема источника для клиента собирается вместе с состоянием — одна на консолидацию. Список
    /// наборов отдаёт её каждому источнику, и собранная заново на каждый она стоила бы столько же
    /// сериализаций, сколько источников в списке.
    /// </summary>
    [Fact]
    public async Task Схема_собирается_один_раз_на_консолидацию()
    {
        var (counter, _) = Counter();
        var file = SystemFile();
        var acts = file.AddSource("Акты", Documents, "[]", 0);
        var protocols = file.AddSource("Протоколы", Documents, "[]", 0);

        var states = await counter.StateAsync([file], Reader, default);

        var schema = System.Text.Json.JsonDocument.Parse(states[acts.Id].Schema!).RootElement;
        Assert.Equal("Наименование", schema[0].GetProperty("name").GetString());
        // Одна и та же строка, а не две равные: собрана она один раз.
        Assert.Same(states[acts.Id].Schema, states[protocols.Id].Schema);
    }

    /// <summary>
    /// Колонок нет — схемы нет, и клиенту уезжает запомненная. Пустой список вместо неё выглядел бы
    /// как «у источника нет колонок», и диалог привязки предложил бы выбирать из ничего.
    /// </summary>
    [Fact]
    public async Task Без_колонок_схемы_нет()
    {
        var (counter, provider) = Counter();
        provider.NoColumns = true;
        var file = SystemFile();
        var source = file.AddSource("Акты", Documents, "[]", 0);

        var states = await counter.StateAsync([file], Reader, default);

        Assert.Null(states[source.Id].Schema);
        Assert.Equal(1, states[source.Id].RowCount);
    }

    /// <summary>
    /// Отказ поставщика запоминается так же, как ответ: «на этом уровне консолидация неприменима»
    /// верно для всех источников набора сразу, и переспрашивать о том же незачем.
    /// </summary>
    [Fact]
    public async Task Отказ_поставщика_тоже_спрашивается_один_раз()
    {
        var (counter, provider) = Counter();
        provider.Refuses = true;
        var file = SystemFile();
        file.AddSource("Акты", Documents, "[]", 0);
        file.AddSource("Протоколы", Documents, "[]", 0);

        var states = await counter.StateAsync([file], Reader, default);

        Assert.Single(provider.Asked);
        // Состояния нет ни у одного — список покажет запомненное число, а не упадёт.
        Assert.Empty(states);
    }

    /// <summary>
    /// Память живёт один вызов. Строки системного набора живые: счётчик создаётся на запрос, и ответ,
    /// переживший вызов, показал бы прежнее число после того, как в том же запросе добавили документ.
    /// Сторож стоит против самого правдоподобного «улучшения» — перенести словарь в поле.
    /// </summary>
    [Fact]
    public async Task Следующий_вызов_спрашивает_поставщика_заново()
    {
        var (counter, provider) = Counter();
        var scope = Guid.NewGuid();
        var file = SystemFile(scope);
        var source = file.AddSource("Акты", Documents, "[]", 0);

        provider.RowsOf[scope] = 2;
        var before = await counter.StateAsync([file], Reader, default);
        provider.RowsOf[scope] = 5;
        var after = await counter.StateAsync([file], Reader, default);

        Assert.Equal(2, provider.Asked.Count);
        Assert.Equal(2, before[source.Id].RowCount);
        Assert.Equal(5, after[source.Id].RowCount);
    }
}

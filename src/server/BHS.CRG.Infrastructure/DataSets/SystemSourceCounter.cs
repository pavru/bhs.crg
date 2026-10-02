using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Живое состояние системных источников: число строк (issue #613), оговорка к данным (issue #626) и
/// набор колонок (issue #664). У остальных форматов <c>CachedRowCount</c>/<c>CachedSchema</c>
/// пересчитываются при замене файла или правке извлечения — у системного набора нет ни того, ни
/// другого: файла не существует, а определение источника не редактируется. Записанное при создании
/// устаревает молча — добавили документ в комплект, и «8 строк» в списках и в MCP-срезе уже
/// неправда, хотя сами строки живые; проставили функциональный тэг в схеме типа, и провайдер отдаёт
/// колонку, которой в описании источника нет.
///
/// Поэтому все три берутся у провайдера на чтении, ОДНИМ вызовом: провайдер собирает строки целиком, и
/// спрашивать его дважды значило бы удваивать работу на каждый источник в списке. Считаем ДО
/// обработки (фильтр/вычисляемые колонки/сортировка) — та же семантика, что у <c>CachedRowCount</c>
/// и <c>CachedSchema</c> остальных форматов, иначе одна и та же подпись «N строк» значила бы в
/// списке разное.
///
/// Там, где строки И ТАК загружаются (карточка источника, страница строк), состояние берут не
/// отсюда, а из <see cref="LoadedRows"/> — иначе провайдер отработал бы дважды на один ответ.
///
/// И по той же причине провайдера спрашивают один раз на КОНСОЛИДАЦИЮ, а не на источник (issue
/// #1142): считаем до обработки, то есть ответ у всех источников одной консолидации в одной области
/// одинаков. Несколько источников на консолидации — штатный случай (issue #717), и спрошенный по
/// разу на каждый, провайдер собирал строки столько раз, сколько источников в списке: на 603
/// источниках одной таблицы запрос списка наборов не уложился в сто секунд.
/// </summary>
public class SystemSourceCounter(SystemDataProviderRegistry providers)
{
    /// <summary>
    /// Что определяет ответ провайдера — ровно параметры <c>ProvideAsync</c>, кроме доступа: он один
    /// на весь вызов. Источника в ключе нет нарочно, в нём и состоит экономия.
    /// </summary>
    private readonly record struct Question(string Marker, CatalogScope Scope, Guid? ScopeId);

    /// <summary>
    /// Ответы, уже полученные в ЭТОМ вызове; null — провайдер отказал или его нет, и переспрашивать о
    /// том же незачем.
    ///
    /// <para>⚠️ Словарь заводит каждый вызов заново, и в поле его переносить нельзя. Счётчик живёт
    /// весь запрос, а строки системного набора живые: ответ, переживший вызов, показал бы прежнее
    /// число после того, как в том же запросе добавили документ (сторож —
    /// <c>SystemSourceCounterTests</c>).</para>
    /// </summary>
    private sealed class Answers : Dictionary<Question, SystemSourceState?>;

    /// <summary>Что известно про системный источник на момент чтения.</summary>
    /// <param name="Types">Виды колонок, если поставщик их объявил (таблица модуля): по ним диалог
    /// отбора предлагает колонке её операторы (issue #1133).</param>
    /// <param name="Schema">Колонки в том виде, в каком их ждёт клиент от <c>CachedSchema</c>; null —
    /// колонок нет, и отдавать надо запомненное. Сериализуется ЗДЕСЬ, один раз на ответ провайдера:
    /// состояние у источников одной консолидации общее, и собирать ту же строку заново на каждый
    /// источник списка значило бы вернуть по мелочи то, от чего ушли (issue #1142).</param>
    public readonly record struct SystemSourceState(
        int RowCount, string? Warning, IReadOnlyList<DataSetColumnInfo> Columns, DataSetColumnTypes? Types = null,
        string? Schema = null);

    /// <summary>
    /// Состояние по id источника для системных наборов из выборки (источники берутся из
    /// <see cref="DataSetFile.Sources"/>). Обычные форматы не трогаем — у них кэш поддерживается
    /// штатно. Пустой словарь, если системных наборов в выборке нет.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SystemSourceState>> StateAsync(
        IEnumerable<DataSetFile> files, DataAccess access, CancellationToken ct)
    {
        var states = new Dictionary<Guid, SystemSourceState>();
        // Одни ответы на ВСЮ выборку, а не на набор: наборы одной области спрашивают об одном и том же.
        var answers = new Answers();
        foreach (var file in files.Where(f => f.IsSystem))
            await AddAsync(states, answers, file, file.Sources, access, ct);
        return states;
    }

    /// <summary>То же для источников, загруженных отдельно от файла.</summary>
    public async Task<IReadOnlyDictionary<Guid, SystemSourceState>> StateAsync(
        DataSetFile file, IEnumerable<DataSetSource> sources, DataAccess access, CancellationToken ct)
    {
        var states = new Dictionary<Guid, SystemSourceState>();
        if (file.IsSystem) await AddAsync(states, new Answers(), file, sources, access, ct);
        return states;
    }

    /// <summary>Состояние одного источника; null — набор не системный или маркер неизвестен.</summary>
    public async Task<SystemSourceState?> StateAsync(
        DataSetSource source, DataSetFile file, DataAccess access, CancellationToken ct)
        => file.IsSystem ? await StateAsync(source.SheetOrPath, file, access, ct) : null;

    /// <summary>
    /// Виды колонок источника для ПРОВЕРКИ отбора при сохранении (issue #1137); null — видов у
    /// источника нет вовсе (набор не системный, маркер без поставщика, поставщик видов не объявил).
    ///
    /// <para>Отказ ворот и поставщика здесь НЕ глотается — в отличие от <see cref="StateAsync(DataSetSource, DataSetFile, DataAccess, CancellationToken)" />.
    /// Счётчику «состояния нет» годится: он покажет запомненное число. Проверке — нет: «виды узнать
    /// не удалось» выглядело бы как «видов нет», отбор проверился бы только по форме, и человек,
    /// которому источник закрыт, сохранил бы «Итого содержит 1» — отказом для всех, кто источник
    /// читает. Кому поставщик отказывает в строках, тому он отказывает и здесь, теми же словами.</para>
    /// </summary>
    public async Task<DataSetColumnTypes?> TypesAsync(
        DataSetSource source, DataSetFile file, DataAccess access, CancellationToken ct)
    {
        if (!file.IsSystem || providers.TryGet(source.SheetOrPath) is not { } provider) return null;
        SystemDataSetGate.Ensure(provider.Declaration, access, source.Name);
        return (await provider.ProvideAsync(source.SheetOrPath, file.Scope, file.ScopeId, access, ct)).Types;
    }

    private async Task AddAsync(Dictionary<Guid, SystemSourceState> states, Answers answers, DataSetFile file,
        IEnumerable<DataSetSource> sources, DataAccess access, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            var question = new Question(source.SheetOrPath, file.Scope, file.ScopeId);
            if (!answers.TryGetValue(question, out var state))
                answers[question] = state = await StateAsync(source.SheetOrPath, file, access, ct);
            if (state is not null) states[source.Id] = state.Value;
        }
    }

    // Пересчёт — удобство, а не обязанность: списки наборов не должны падать из-за одного источника.
    // Маркер без провайдера (консолидацию убрали в новой версии) и источник на уровне, где
    // консолидация неприменима (набор остался от версий до гейта #606), отдают запомненное число —
    // оно хотя бы показывает, чем источник был.
    private async Task<SystemSourceState?> StateAsync(
        string marker, DataSetFile file, DataAccess access, CancellationToken ct)
    {
        var provider = providers.TryGet(marker);
        if (provider is null) return null;
        try
        {
            // Ворота (ТЗ CORE-24.1, issue #965) — и здесь: счётчик читает строки целиком, то есть
            // «сколько строк» отвечает по данным, на которые права может не быть. Отказ ворот —
            // DomainException, и его глотает тот же catch ниже: список наборов у человека без права
            // покажет запомненное число вместо живого, а не упадёт целиком. Запомненное число на
            // источнике лежит с момента создания и уезжает в резервную копию — новой утечки здесь нет.
            SystemDataSetGate.Ensure(provider.Declaration, access, "");
            var provided = await provider.ProvideAsync(marker, file.Scope, file.ScopeId, access, ct);
            return new SystemSourceState(provided.Rows.Count, provided.Warning, provided.Columns, provided.Types,
                provided.Columns.Count > 0 ? DataSetDtoMapper.SerializeSchema(provided.Columns, provided.Types) : null);
        }
        // Ловим НАШ отказ провайдера («источник доступен только на уровне комплекта» и подобные) —
        // для счётчика это просто «состояния нет». Чужое исключение сюда попадать не должно: оно
        // означает дефект, и глотать его значит потерять единственный след.
        catch (DomainException)
        {
            return null;
        }
    }
}

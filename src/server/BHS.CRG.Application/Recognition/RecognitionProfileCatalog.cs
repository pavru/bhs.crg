using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Application.Recognition;

/// <summary>
/// Заводской профиль распознавания, как его знает ядро: объявление владельца, переведённое в типы
/// ядра, плюс сам владелец и то, включён ли он на этом экземпляре.
/// </summary>
/// <param name="Owner">Код владельца: код модуля либо <see cref="RecognitionProfileCatalog.CoreOwner" />.</param>
/// <param name="DocumentTag">Тэг документа, по которому профиль выбирается сам; null — не выбирается.</param>
public sealed record RecognitionProfileDeclaration(
    string Code,
    string Name,
    RecognitionProfileKind Kind,
    IReadOnlyList<RecognitionProfileField> Fields,
    IReadOnlyList<RecognitionProfileField> RowColumns,
    RecognitionTableShape? Shape,
    string Owner,
    string? DocumentTag = null)
{
    /// <summary>Хеш заводского содержимого — по нему сидер понимает, что дефолт ушёл вперёд, пока
    /// пользователь держит свою правку (см. <c>RecognitionProfile.BuiltInHash</c>).</summary>
    public string Hash => RecognitionProfileCatalog.HashOf(Fields, RowColumns, Shape, Name);
}

/// <summary>Владелец профилей: модуль сборки или ядро.</summary>
/// <param name="Enabled">Включён ли на этом экземпляре. Ядро включено всегда.</param>
public sealed record RecognitionProfileOwner(string Code, string Title, bool Enabled);

/// <summary>
/// Заводские профили распознавания этого экземпляра: объявления ядра и ВСЕХ модулей сборки
/// (ТЗ CORE-Q6, задача B1a, issue #1075).
///
/// <para>До этой задачи профили лежали одним статическим списком, и ядро знало специфику каждого
/// модуля: графы штампа ГОСТ, колонки кабельного журнала. Теперь содержимое объявляет владелец, а
/// ядро держит только то, без чего не может распознавать: виды, то есть сами запросы к модели.</para>
///
/// <para>Каталог знает и выключенные модули — нарочно. Строка профиля выключенного модуля остаётся в
/// базе, и ядру надо уметь ответить «это профиль модуля такого-то, он выключен», а не «профиль не
/// найден»: второе читается как поломка и зовёт чинить то, что исправно.</para>
///
/// <para>⚠️ Здесь же стоят ВОРОТА: <see cref="Require(RecognitionProfileKind)" /> и
/// <see cref="Require(string, string)" />. Спрашивает их поставщик профилей, а не адреса: адресов
/// распознавания несколько, плюс инструменты MCP и фоновые задачи, и забыть проверку в одном из них —
/// вопрос времени. Потребитель, который берёт профиль у поставщика, обойти ворота не может.</para>
/// </summary>
public sealed class RecognitionProfileCatalog
{
    /// <summary>Код владельца «ядро». Модуля с таким кодом быть не может: <c>core.</c> — начало
    /// прав ядра, и оно занято.</summary>
    public const string CoreOwner = "core";

    private readonly Dictionary<string, RecognitionProfileDeclaration> _byCode;
    private readonly Dictionary<RecognitionProfileKind, RecognitionProfileDeclaration> _byKind;
    private readonly Dictionary<string, RecognitionProfileDeclaration> _byTag;
    private readonly Dictionary<string, RecognitionProfileOwner> _owners;

    /// <param name="faults">Изъяны, найденные при переводе объявлений модулей (неизвестный вид) —
    /// уходят в тот же отказ, что и найденные здесь: один перезапуск на всё.</param>
    public RecognitionProfileCatalog(
        IEnumerable<RecognitionProfileDeclaration> declarations,
        IEnumerable<RecognitionProfileOwner> owners,
        IEnumerable<string>? faults = null)
    {
        All = [.. declarations];
        var problems = (faults ?? []).ToList();
        // Владельцы — без ToDictionary «в лоб»: повтор кода (модуль, назвавшийся «core», или один
        // модуль в двух списках) дал бы «An item with the same key…» без названия, мимо общего
        // отказа (ревью PR #1252).
        _owners = new Dictionary<string, RecognitionProfileOwner>(StringComparer.Ordinal);
        foreach (var owner in owners)
            if (!_owners.TryAdd(owner.Code, owner))
                problems.Add($"код владельца «{owner.Code}» занят дважды: «{_owners[owner.Code].Title}» и «{owner.Title}»");

        foreach (var g in All.GroupBy(d => d.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"код «{g.Key}» объявлен {g.Count()} раза: {Owners(g)}");
        // Один заводской профиль на вид: им читают, когда к файлу не привязан другой. Двое на вид —
        // и умолчание становится выбором наугад.
        foreach (var g in All.GroupBy(d => d.Kind).Where(g => g.Count() > 1))
            problems.Add($"у вида «{g.Key}» {g.Count()} заводских профиля: " +
                         string.Join(", ", g.Select(d => $"«{d.Code}»")));
        foreach (var g in All.Where(d => d.DocumentTag is not null)
                     .GroupBy(d => d.DocumentTag!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"тэг документа «{g.Key}» ведёт к {g.Count()} профилям: " +
                         string.Join(", ", g.Select(d => $"«{d.Code}»")));
        foreach (var d in All)
        {
            if (string.IsNullOrWhiteSpace(d.Code) || string.IsNullOrWhiteSpace(d.Name))
                problems.Add($"профиль владельца «{d.Owner}» без кода или названия");
            if (d.Fields.Count == 0 && d.RowColumns.Count == 0)
                problems.Add($"профиль «{d.Code}» пуст: ни полей, ни колонок");
            if (!_owners.ContainsKey(d.Owner))
                problems.Add($"у профиля «{d.Code}» неизвестный владелец «{d.Owner}»");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Объявления профилей распознавания негодны: " + string.Join("; ", problems) + ".");

        _byCode = All.ToDictionary(d => d.Code, StringComparer.Ordinal);
        _byKind = All.ToDictionary(d => d.Kind);
        _byTag = All.Where(d => d.DocumentTag is not null)
            .ToDictionary(d => d.DocumentTag!, StringComparer.Ordinal);
    }

    /// <summary>Все объявления сборки — и включённых владельцев, и выключенных.</summary>
    public IReadOnlyList<RecognitionProfileDeclaration> All { get; }

    public RecognitionProfileDeclaration? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>Заводской профиль вида — им читают, когда свой не привязан. null — вид не объявлен
    /// никем: читать таким видом на этой сборке нечем.</summary>
    public RecognitionProfileDeclaration? ForKind(RecognitionProfileKind kind) => _byKind.GetValueOrDefault(kind);

    /// <summary>Заводской профиль по тэгу документа; null — тэг не табличный.</summary>
    public RecognitionProfileDeclaration? ForTag(string tag) => _byTag.GetValueOrDefault(tag);

    /// <summary>
    /// Владелец по коду. Неизвестный код — владелец, которого в сборке нет: он «выключен» и назван
    /// кодом. Так отвечает строка, приехавшая из копии экземпляра с другим составом модулей.
    /// </summary>
    public RecognitionProfileOwner Owner(string code) =>
        _owners.GetValueOrDefault(code) ?? new RecognitionProfileOwner(code, code, Enabled: false);

    /// <summary>
    /// Чей вид. Вид принадлежит тому, кто объявил его заводской профиль: своих профилей без
    /// заводского не бывает, потому что читать ими было бы нечем до первой привязки.
    /// null — вид не объявлен никем.
    /// </summary>
    public RecognitionProfileOwner? OwnerOfKind(RecognitionProfileKind kind) =>
        ForKind(kind) is { } d ? Owner(d.Owner) : null;

    /// <summary>
    /// Владелец строки профиля.
    ///
    /// <para>СВОЙ профиль принадлежит владельцу своего вида — всегда, что бы ни лежало в колонке
    /// (ревью PR #1252). Колонка у него — проекция, которую сидер приводит к каталогу при старте.
    /// Иначе она становилась второй правдой: вид переехал к другому модулю — а свои профили этого
    /// вида остались бы у прежнего, видимые и рабочие при выключенном новом владельце. И строка из
    /// копии экземпляра с другим составом модулей оставалась бы невидимой и неудаляемой навсегда.</para>
    ///
    /// <para>ВСТРОЕННЫЙ профиль принадлежит тому, кто записан в колонке: её ставит сидер из
    /// объявления. Строка с кодом, которого больше никто не объявляет, остаётся у прежнего владельца —
    /// скрытой, если его нет. Пустая колонка (копия, снятая до её появления) — владелец вида.</para>
    /// </summary>
    public RecognitionProfileOwner? OwnerOf(RecognitionProfile profile) =>
        profile.IsBuiltIn && profile.Module.Length > 0
            ? Owner(profile.Module)
            : OwnerOfKind(profile.Kind) ?? (profile.Module.Length > 0 ? Owner(profile.Module) : null);

    /// <summary>Доступен ли вид: объявлен и владелец включён.</summary>
    public bool IsAvailable(RecognitionProfileKind kind) => OwnerOfKind(kind) is { Enabled: true };

    /// <summary>Доступна ли строка профиля на этом экземпляре.</summary>
    public bool IsAvailable(RecognitionProfile profile) => OwnerOf(profile) is { Enabled: true };

    /// <summary>
    /// Ворота вида: отказ, если его владелец выключен. Возвращает заводской профиль вида.
    /// </summary>
    public RecognitionProfileDeclaration Require(RecognitionProfileKind kind)
    {
        var declaration = ForKind(kind) ?? throw new InvalidRequestException(
            $"Распознавание вида «{kind}» на этом экземпляре недоступно: его не объявляет ни один модуль.");
        Require(declaration.Owner, $"Профиль распознавания «{declaration.Name}»");
        return declaration;
    }

    /// <summary>Ворота строки профиля: отказ, если её владелец выключен.</summary>
    public void Require(RecognitionProfile profile)
    {
        var owner = OwnerOf(profile) ?? throw new InvalidRequestException(
            $"Профиль распознавания «{profile.Name}» недоступен: его вид «{profile.Kind}» не объявляет ни один модуль.");
        Require(owner.Code, $"Профиль распознавания «{profile.Name}»");
    }

    /// <summary>
    /// Ворота владельца. Отказ называет и то, к чему обратились, и модуль: «профиль не найден» здесь
    /// было бы отказом, переодетым в поломку, — исправная установка без этого модуля выглядела бы
    /// сломанной.
    /// </summary>
    /// <param name="what">Подлежащее отказа: «Профиль распознавания «Штамп ГОСТ»».</param>
    public void Require(string ownerCode, string what)
    {
        var owner = Owner(ownerCode);
        if (!owner.Enabled)
            throw new InvalidRequestException(
                $"{what} принадлежит модулю «{owner.Title}», а он на этом экземпляре выключен.");
    }

    /// <summary>Хеш содержимого профиля. Рецепт один для заводского объявления и для строки в базе —
    /// поэтому их можно сравнивать напрямую.</summary>
    public static string HashOf(
        IReadOnlyList<RecognitionProfileField> fields,
        IReadOnlyList<RecognitionProfileField> rowColumns,
        RecognitionTableShape? shape,
        string name)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            RecognitionProfileJson.Canonical(fields, rowColumns, shape) + '|' + name)));

    /// <summary>Хеш ТЕКУЩЕГО содержимого строки. Нужен, чтобы понять «профиль отличается от
    /// заводского»: сравнивать со СТАРЫМ сохранённым хешем для этого нельзя — он описывает заводскую
    /// версию, а не то, что лежит в строке (после «сбросить к заводским» они как раз расходятся).</summary>
    public static string HashOfCurrent(RecognitionProfile profile) => HashOf(
        RecognitionProfileJson.ReadFields(profile.Fields),
        RecognitionProfileJson.ReadFields(profile.RowColumns),
        RecognitionProfileJson.ReadShape(profile.Shape),
        profile.Name);

    private string Owners(IEnumerable<RecognitionProfileDeclaration> group) =>
        string.Join(", ", group.Select(d => $"«{Owner(d.Owner).Title}»"));
}

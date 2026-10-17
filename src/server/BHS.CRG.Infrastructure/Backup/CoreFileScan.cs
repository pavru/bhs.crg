using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>Строка манифеста, в которой найден ключ пути файла (issue #1422).</summary>
/// <param name="Address">Поле манифеста: имена свойств, массивы — <c>[]</c>; внутри поля-JSON адрес
/// обрывается на самом поле (<c>Documents[].Data</c>) — ключи схемы пользователя суть данные, а не код.</param>
/// <param name="RecordId">Ближайшая запись с идентификатором, которой принадлежит строка, — для журнала.</param>
/// <param name="Text">Сама строка: у точного пути — путь.</param>
/// <param name="Hit">Точный ли это путь и какие ключи в нём.</param>
public sealed record CoreFileHit(string Address, Guid? RecordId, string Text, BlobPathHit Hit);

/// <summary>Что обход манифеста ядра нашёл про файлы.</summary>
/// <param name="Exact">Строки точной формы пути — их можно взять в архив как есть.</param>
/// <param name="InText">Строки, где ключи путей записаны внутри текста; взять отсюда нечего.</param>
public sealed record CoreFilesFound(IReadOnlyList<CoreFileHit> Exact, IReadOnlyList<CoreFileHit> InText);

/// <summary>Поле манифеста так, как его видит обход: адрес и что в нём лежит.</summary>
public sealed record CoreFileField(string Address, CoreFieldKind Kind);

public enum CoreFieldKind
{
    /// <summary>Строка — обходится целиком: путь в ней либо точный, либо внутри текста.</summary>
    Text,

    /// <summary>JSON-значение — обходится на любую глубину.</summary>
    Json,

    /// <summary>Число, дата, идентификатор, признак — файла не держит.</summary>
    Scalar,

    /// <summary>Запись: её поля перечислены отдельно.</summary>
    Record,

    /// <summary>Коллекция: её элемент перечислен отдельно, адрес с <c>[]</c>.</summary>
    List,
}

/// <summary>
/// Файлы ядра — обходом манифеста копии по форме пути (issue #1422).
///
/// <para><b>Зачем.</b> Копия выбирает файлы списком мест, выписанным руками
/// (<c>BackupService.ExtractBlobPaths</c>), и новое место хранения пути выпадало из неё молча, с
/// отчётом «восстановлено N из N» — так было с частями PDF-наборов (#1399). Обход смотрит на каждую
/// строку того, что уезжает, и находит путь там, о чём список не знает. У модулей так устроено с
/// #1261 (<c>ModuleFileScan</c>); ядро получило то же правило (<see cref="BlobPathShape.Inspect" />).</para>
///
/// <para><b>Манифест, а не база.</b> Копия и уборка осиротевших файлов задают разные вопросы: «что
/// несёт копия» против «что держит база». Уборку (<c>LiveBlobPathScan</c>) копия не зовёт: с ней
/// понадобились бы второй список исключённых сущностей, общий снимок и правка оценки веса. Манифест
/// же и есть решение о составе: исключённое в него не попало.</para>
///
/// <para><b>По контракту сериализатора, без второй сериализации.</b> Обход идёт по объектам
/// манифеста, но поля и их имена берёт у того же <see cref="JsonSerializerOptions" />, которым манифест
/// пишется в архив: сверяется ровно то, что уезжает, с теми же именами и без полей, которые
/// сериализатор пропускает. Строки уже лежат в памяти — ни буфера, ни копии манифеста, у которого с
/// картинками в base64 сотни мегабайт. Цена — обход обязан знать каждый вид листа контракта:
/// незнакомый тип — отказ построения плана, и сторож ловит его в день появления.</para>
///
/// <para>Чистая функция: без базы и без хранилища. Решает, ЧТО найдено; что брать и о чём говорить —
/// дело копии.</para>
/// </summary>
public static class CoreFileScan
{
    /// <summary>
    /// Поля манифеста, которые обход НЕ смотрит, и почему. Адреса — через <c>nameof</c>: переименование
    /// поля ломает сборку, а не тихо отменяет исключение.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotWalked = new Dictionary<string, string>
    {
        // Модуль сам называет свои файлы и сам освобождает производные колонки (ModuleFile.Carried):
        // обход ядра по его строкам вернул бы освобождённое.
        [nameof(BackupManifest.ModuleData)] =
            "у модулей свой обход строк (ModuleFileScan) и своё освобождение производных колонок",
        // Журнал — снимок истории: путь в «было/стало» может вести к файлу, удалённому годы назад, и
        // копия вечно писала бы «файлов не оказалось в хранилище». На дев-базе путей в журнале нет
        // (0 из 10 438), так что ничего и не теряется.
        [nameof(BackupManifest.ActivityLog)] =
            "журнал — снимок истории, а не держатель файлов: пути в нём могут вести к удалённым файлам",
    };

    private static readonly ConditionalWeakTable<JsonSerializerOptions, Node> Plans = new();

    /// <summary>Строки манифеста, в которых найден ключ пути, — в порядке адресов.</summary>
    public static CoreFilesFound Scan(BackupManifest manifest, JsonSerializerOptions options)
    {
        var found = new List<CoreFileHit>();
        Walk(manifest, PlanOf(options), null, found);

        // Порядок — по адресу, а внутри адреса — по ходу обхода (сортировка устойчива): текст паспорта
        // обязан совпасть байт в байт у копии и у оценки её веса.
        var ordered = found.OrderBy(h => h.Address, StringComparer.Ordinal).ToList();
        return new CoreFilesFound(
            [.. ordered.Where(h => h.Hit.Exact)], [.. ordered.Where(h => !h.Hit.Exact)]);
    }

    /// <summary>Каждое поле контракта манифеста, кроме <see cref="NotWalked" />, — для сторожей.</summary>
    public static IReadOnlyList<CoreFileField> Fields(JsonSerializerOptions options)
    {
        var fields = new List<CoreFileField>();
        Collect(PlanOf(options));
        return fields;

        void Collect(Node node)
        {
            if (node.Address.Length > 0) fields.Add(new CoreFileField(node.Address, node.Kind));
            foreach (var member in node.Members) Collect(member.Node);
            if (node.Element is { } element) Collect(element);
        }
    }

    private static void Walk(object value, Node node, Guid? owner, List<CoreFileHit> found)
    {
        switch (node.Kind)
        {
            case CoreFieldKind.Text:
                if (BlobPathShape.Inspect((string)value) is { } hit)
                    found.Add(new CoreFileHit(node.Address, owner, (string)value, hit));
                break;

            case CoreFieldKind.Json:
                BlobPathShape.VisitStrings((JsonElement)value, text =>
                {
                    if (BlobPathShape.Inspect(text) is { } inner)
                        found.Add(new CoreFileHit(node.Address, owner, text, inner));
                });
                break;

            case CoreFieldKind.Record:
                if (node.Id?.Get!(value) is Guid id) owner = id;
                foreach (var member in node.Members)
                    if (member.Property.Get!(value) is { } child) Walk(child, member.Node, owner, found);
                break;

            case CoreFieldKind.List:
                foreach (var item in (IEnumerable)value)
                    if (item is not null) Walk(item, node.Element!, owner, found);
                break;
        }
    }

    // ── План обхода: контракт манифеста, разобранный один раз ─────────────────────────────────

    private sealed record Member(JsonPropertyInfo Property, Node Node);

    private sealed record Node(
        string Address, CoreFieldKind Kind, Member[] Members, Node? Element, JsonPropertyInfo? Id);

    private static Node PlanOf(JsonSerializerOptions options)
    {
        // Контракт читается только у неизменяемых настроек. Сериализатор запирает их сам при первой
        // записи; обход может прийти раньше — и запирает так же, с тем же распознавателем по умолчанию.
        if (!options.IsReadOnly) options.MakeReadOnly(populateMissingResolver: true);
        return Plans.GetValue(options, o =>
        {
            // Исключение, не нашедшее поля, молча перестало бы действовать: имя в контракте иное, чем
            // nameof, — например, после смены политики имён. Тогда журнал снова пошёл бы в обход.
            var names = o.GetTypeInfo(typeof(BackupManifest)).Properties.Select(p => p.Name).ToHashSet();
            if (NotWalked.Keys.FirstOrDefault(k => !names.Contains(k)) is { } lost)
                throw new InvalidOperationException(
                    $"Исключение обхода файлов копии «{lost}» не нашло поля в контракте манифеста.");
            return Build(typeof(BackupManifest), "", o);
        });
    }

    private static Node Build(Type type, string address, JsonSerializerOptions options)
    {
        if (type == typeof(string)) return Leaf(address, CoreFieldKind.Text);
        if (type == typeof(JsonElement) || type == typeof(JsonElement?)) return Leaf(address, CoreFieldKind.Json);
        if (IsScalar(Nullable.GetUnderlyingType(type) ?? type)) return Leaf(address, CoreFieldKind.Scalar);

        var info = options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                var members = info.Properties
                    .Where(p => p.Get is not null)
                    .Select(p => (Property: p, Address: address.Length == 0 ? p.Name : $"{address}.{p.Name}"))
                    .Where(p => !NotWalked.ContainsKey(p.Address))
                    .Select(p => new Member(p.Property, Build(p.Property.PropertyType, p.Address, options)))
                    .ToArray();
                var id = info.Properties.FirstOrDefault(p => p.Name == "Id" && p.PropertyType == typeof(Guid));
                return new Node(address, CoreFieldKind.Record, members, null, id);

            case JsonTypeInfoKind.Enumerable:
                return new Node(address, CoreFieldKind.List, [],
                    Build(info.ElementType!, $"{address}[]", options), null);

            default:
                // Словарь, тип со своим конвертером: что в нём лежит, обход не знает — а молча его
                // пропустить значило бы вернуть ту самую дыру, ради которой обход заведён.
                throw new NotSupportedException(
                    $"Поле манифеста копии «{address}» типа {type.Name}: обход файлов копии такой тип не " +
                    "знает. Научите CoreFileScan его обходить или замените тип на запись, строку или JSON.");
        }
    }

    private static Node Leaf(string address, CoreFieldKind kind) => new(address, kind, [], null, null);

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
        type == typeof(TimeOnly) || type == typeof(TimeSpan);
}

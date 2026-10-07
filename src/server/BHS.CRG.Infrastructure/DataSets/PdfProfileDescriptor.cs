using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Категория поведения профиля PDF (issue #44). Данные, НЕ виртуальный dispatch — по прецеденту
/// FunctionalTag/TagRegistry (конфиг↔код мост как статический список, не интерфейс+DI). При двух
/// профилях полноценный IPdfProfile+registry преждевременен (YAGNI); эскалация — только если профили
/// станут pluggable в рантайме (не C#-файл разработчика).
/// </summary>
public enum PdfProfileKind
{
    /// <summary>ГОСТ Р 21.101-2020: постраничная группировка (DataSetFile.Grouping) — источник истины
    /// сырья набора. Кандидаты обложка/титул/документы/таблицы, источники создаёт пользователь.
    /// Долгое распознавание (десятки страниц, минуты) → фоновая задача. Стабильные id групп переживают
    /// перераспознавание (issue #28/#42) — тэги/табличное сырьё переносятся.</summary>
    Gost,

    /// <summary>Счёт на оплату: фиксированная пара срезов документа (шапка + товары) — известное
    /// конечное число (в отличие от динамических групп ГОСТ), распознаётся одним vision-вызовом на
    /// весь PDF. Сырьё пишется в DataSetFile.InvoiceRawData; кандидаты «Шапка»/«Товары» — как у ГОСТ,
    /// источники создаёт пользователь (issue #44 — унифицировано с общей философией набор=сырьё).
    /// Короткое распознавание (секунды) → синхронно.</summary>
    InvoiceFixedSlices,
}

/// <summary>
/// Дескриптор профиля PDF — заменяет разбросанные строковые сравнения (`SheetOrPath is
/// PdfProfiles.GostCoverMarker or PdfProfiles.GostTitlePageMarker or ...`) одним lookup. Статический
/// список данных (issue #44), не registry с DI-регистрацией — расширение под новый профиль обычно
/// требует новой ветки в распознавании/проекции всё равно (see DataSetPdfRecognitionService/
/// DataSetSourceService), дескриптор лишь убирает дублирование "к какому профилю относится маркер".
/// </summary>
/// <param name="ProfileMarker">Значение <see cref="Domain.DataSets.DataSetFile.PreprocessingProfile"/>.</param>
/// <param name="Kind">Категория поведения (см. <see cref="PdfProfileKind"/>).</param>
/// <param name="SourceMarkers">Маркеры <see cref="Domain.DataSets.DataSetSource.SheetOrPath"/>,
/// принадлежащие этому профилю (для legacy source-centric путей и обратной совместимости).</param>
/// <param name="Background">Распознавание — фоновая задача (true) или синхронный вызов (false).</param>
/// <param name="SupportsReprojection">Капабилити-флаг (issue #42): перенос пользовательской разметки
/// (тэги/табличное сырьё) по стабильному id группы при ре-распознавании. false — профиль не имеет
/// понятия "группа", капабилити неприменима (не noop-метод интерфейса — искали бы ISP-нарушение).</param>
/// <param name="RequiredKind">Вид профиля распознавания, без которого этот профиль PDF не читает
/// ничего (issue #1075). По нему стоят ворота: владелец вида выключен — выбрать профиль PDF и
/// запустить по нему распознавание нельзя. Ядро при этом модуль не называет: «ГОСТ» требует вида
/// «штамп», а чей это вид, знает каталог объявлений.</param>
/// <param name="Title">Название для выбора в диалоге «Распознать PDF». Раньше оно было зашито в
/// клиенте вместе с перечнем профилей — и клиент предлагал профиль, который сервер на этом
/// экземпляре отвергал (issue #1075).</param>
/// <param name="NameHint">Пример названия источника — подсказка под полем.</param>
/// <param name="Summary">Что произойдёт после запуска и где искать результат.</param>
public record PdfProfileDescriptor(
    string ProfileMarker, PdfProfileKind Kind, IReadOnlyList<string> SourceMarkers,
    bool Background, bool SupportsReprojection, RecognitionProfileKind RequiredKind,
    string Title, string NameHint, string Summary);

/// <summary>
/// Профиль PDF для диалога выбора (issue #1075). Отдаются только те, чей вид распознавания на этом
/// экземпляре есть кому читать.
/// </summary>
/// <param name="StructureTags">Спрашивать ли у пользователя тэги структуры PDF (есть обложка, есть
/// титульный лист) — они значимы только там, где листы группируются.</param>
public record PdfProfileInfo(string Profile, string Title, string NameHint, string Summary, bool StructureTags);

public static class PdfProfileRegistry
{
    public static readonly IReadOnlyList<PdfProfileDescriptor> All =
    [
        new(PdfProfiles.GostTitleBlock, PdfProfileKind.Gost,
            [PdfProfiles.GostCoverMarker, PdfProfiles.GostTitlePageMarker, PdfProfiles.GostDocumentsMarker],
            Background: true, SupportsReprojection: true, RecognitionProfileKind.TitleBlock,
            Title: "Основная надпись (ГОСТ Р 21.101-2020) — реестр по страницам",
            NameHint: "Реестр листов",
            Summary: "Сразу запустится распознавание — оно постранично извлечёт основную надпись по "
                + "ГОСТ Р 21.101-2020 и сгруппирует листы по шифру документа. Результат появится как "
                + "кандидаты (Документы/Обложка/Титульный лист) под списком источников — создайте из "
                + "них источники в один клик."),
        new(PdfProfiles.Invoice, PdfProfileKind.InvoiceFixedSlices,
            [PdfProfiles.InvoiceHeaderMarker, PdfProfiles.InvoiceLineItemsMarker],
            Background: false, SupportsReprojection: false, RecognitionProfileKind.Invoice,
            Title: "Счёт на оплату — шапка + таблица товаров",
            NameHint: "Счёт на оплату",
            Summary: "Сразу запустится распознавание — оно одним вызовом извлечёт реквизиты счёта и "
                + "таблицу товаров. Результат появится как кандидаты «Шапка» и «Товары» под списком "
                + "источников — создайте из них источники в один клик."),
    ];

    /// <summary>
    /// Что предложить в диалоге «Распознать PDF»: профили, вид которых на этом экземпляре доступен.
    /// Отбор тем же вопросом, что и ворота записи, — иначе список разошёлся бы с отказом.
    /// </summary>
    public static IReadOnlyList<PdfProfileInfo> Offered(Func<RecognitionProfileKind, bool> isAvailable) =>
        [.. All.Where(p => isAvailable(p.RequiredKind)).Select(p => new PdfProfileInfo(
            p.ProfileMarker, p.Title, p.NameHint, p.Summary, StructureTags: p.Kind == PdfProfileKind.Gost))];

    /// <summary>По профилю набора (<see cref="Domain.DataSets.DataSetFile.PreprocessingProfile"/>).</summary>
    public static PdfProfileDescriptor? ByProfileMarker(string? profileMarker) =>
        profileMarker is null ? null : All.FirstOrDefault(p => p.ProfileMarker == profileMarker);

    /// <summary>По маркеру конкретного источника (<see cref="Domain.DataSets.DataSetSource.SheetOrPath"/>)
    /// — legacy source-centric путь и обратная совместимость с наборами, созданными до появления
    /// PreprocessingProfile на файле (у них он null, но источники уже несут профильные маркеры).</summary>
    public static PdfProfileDescriptor? BySourceMarker(string sheetOrPath) =>
        All.FirstOrDefault(p => p.SourceMarkers.Contains(sheetOrPath));
}

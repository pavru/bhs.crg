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
    /// конечное число, распознаётся одним vision-вызовом на весь PDF. Источники создаются сразу при
    /// выборе профиля (кандидатная модель не нужна — это законно другая, не менее корректная форма,
    /// не технический долг). Короткое распознавание (секунды) → синхронно.</summary>
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
public record PdfProfileDescriptor(
    string ProfileMarker, PdfProfileKind Kind, IReadOnlyList<string> SourceMarkers,
    bool Background, bool SupportsReprojection);

public static class PdfProfileRegistry
{
    public static readonly IReadOnlyList<PdfProfileDescriptor> All =
    [
        new(PdfProfiles.GostTitleBlock, PdfProfileKind.Gost,
            [PdfProfiles.GostCoverMarker, PdfProfiles.GostTitlePageMarker, PdfProfiles.GostDocumentsMarker],
            Background: true, SupportsReprojection: true),
        new(PdfProfiles.Invoice, PdfProfileKind.InvoiceFixedSlices,
            [PdfProfiles.InvoiceHeaderMarker, PdfProfiles.InvoiceLineItemsMarker],
            Background: false, SupportsReprojection: false),
    ];

    /// <summary>По профилю набора (<see cref="Domain.DataSets.DataSetFile.PreprocessingProfile"/>).</summary>
    public static PdfProfileDescriptor? ByProfileMarker(string? profileMarker) =>
        profileMarker is null ? null : All.FirstOrDefault(p => p.ProfileMarker == profileMarker);

    /// <summary>По маркеру конкретного источника (<see cref="Domain.DataSets.DataSetSource.SheetOrPath"/>)
    /// — legacy source-centric путь и обратная совместимость с наборами, созданными до появления
    /// PreprocessingProfile на файле (у них он null, но источники уже несут профильные маркеры).</summary>
    public static PdfProfileDescriptor? BySourceMarker(string sheetOrPath) =>
        All.FirstOrDefault(p => p.SourceMarkers.Contains(sheetOrPath));
}

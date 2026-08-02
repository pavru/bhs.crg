namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Размеры страниц доменных выдач MCP (issue #576).
///
/// Потолки разные, потому что различается ВЕС элемента, а не его важность. Документ качества несёт
/// свои реквизиты — на живых данных это около двух килобайт на запись, и сотня таких уже вернула бы
/// ответ, который клиент не принимает. Сводка записи каталога или связь материала — одна строка,
/// и резать их так же мелко значило бы гонять агента за страницами без причины.
///
/// Числа подобраны по замеру, а не выведены: при нужде их правит тот, кто заново померил.
/// </summary>
public static class DomainSnapshotLimits
{
    /// <summary>Документы качества: тяжёлые, вместе с реквизитами.</summary>
    public const int QualityDocumentsDefault = 25;

    /// <inheritdoc cref="QualityDocumentsDefault" />
    public const int QualityDocumentsMax = 100;

    /// <summary>Записи каталога: только сводка, данных записи здесь нет.</summary>
    public const int CatalogEntriesDefault = 100;

    /// <inheritdoc cref="CatalogEntriesDefault" />
    public const int CatalogEntriesMax = 500;

    /// <summary>Связи «материал → документ качества»: строка ключа и имя документа.</summary>
    public const int MaterialLinksDefault = 200;

    /// <inheritdoc cref="MaterialLinksDefault" />
    public const int MaterialLinksMax = 500;

    /// <summary>
    /// Навигационные списки — стройки, наборы данных (#590). Их длину задаёт структура стройки, а не
    /// объём данных, поэтому потолок здесь не про экономию контекста: он про то, чтобы форма ответа
    /// была ОДНА. Оболочка страницы есть и у них, но упереться в её границу на реальных данных
    /// практически нечем — десятки записей против потолка в пятьсот.
    /// </summary>
    public const int NavigationDefault = 200;

    /// <inheritdoc cref="NavigationDefault" />
    public const int NavigationMax = 500;
}

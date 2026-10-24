namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Как реестр счетов называет места разноски в колонках «Объект» и «Раздел» (ТЗ COST-20.1; change
/// <c>costs-filter-place-hints</c>): формат подписи раздела и слова потерянных ссылок.
///
/// <para><b>Эти слова — часть отбора, а не только вида.</b> Отбор реестра идёт по названию, и экран
/// подсказывает значение условия из своей копии этих слов (<c>registryWords.ts</c>) — разойдись они,
/// подсказка предлагала бы название, под которое сервер не найдёт ни одного счёта, а пометка «нет в
/// справочнике» обвиняла бы годное. Поэтому копии сверяет общий набор случаев
/// <c>placeLabelCases.json</c>: его читают тест клиента и <c>PlaceLabelCasesTests</c>. Открыт класс
/// ради теста — <c>InternalsVisibleTo</c> в решении не заведён.</para>
/// </summary>
public static class RegistryPlaceNames
{
    /// <summary>Доля на стройку целиком — раздел не назван.</summary>
    public const string NoSection = "без раздела";

    /// <summary>Как назван раздел, которого больше нет (и доля без раздела на удалённой стройке).</summary>
    public const string LostSection = "раздел удалён";

    /// <summary>Как названа цель, которой больше нет, — стройку удалили в ядре, статью — в справочнике.</summary>
    public const string LostObject = "объект удалён";

    /// <summary>«Комарова 36 / 4 эт.» — так раздел зовут колонка реестра, её отбор и ссылка отчёта.</summary>
    public static string SectionLabel(string site, string section) => $"{site} / {section}";
}

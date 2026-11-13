namespace BHS.CRG.Modules.Tables;

/// <summary>
/// Строка границы выдачи таблицы — одна на исход отбора (ТЗ CORE-24.3; work-07).
///
/// <para><b>Строка на исход, а не одна на таблицу.</b> Монтажнику таблица отчётов отдаёт свои, менеджеру
/// — его стройки, читающему всё — все. Одна постоянная строка подписала бы данные менеджера словами «все
/// отчёты» там, где ему отдали четыре стройки, — и он счёл бы выборку полной.</para>
/// </summary>
/// <param name="Code">Код исхода — им служба строк называет применившийся
/// (<see cref="ModuleTableOutcome.Code" />): <c>own</c>, <c>sites</c>, <c>all</c> — коды охвата
/// <see cref="Access.ModuleReach" />, если таблица отбирает по нему. Уникален в таблице.</param>
/// <param name="Text">Текст словами модуля: «Отдаёт то, что видно вам: отчёты ваших строек ({sites})».
/// Место <see cref="SitesPlace" /> заменяется числом строек исхода.</param>
public sealed record ModuleTableBoundary(string Code, string Text)
{
    /// <summary>Место для числа строек в тексте.</summary>
    public const string SitesPlace = "{sites}";

    /// <summary>Код единственного исхода таблицы, которой выбирать не из чего.</summary>
    public const string OnlyCode = "all";

    /// <summary>Граница таблицы без выбора исхода: одна строка на все случаи.</summary>
    public static IReadOnlyList<ModuleTableBoundary> Only(string text) => [new(OnlyCode, text)];

    /// <summary>Нужно ли этому тексту число строек.</summary>
    public bool CountsSites => Text?.Contains(SitesPlace, StringComparison.Ordinal) == true;

    /// <summary>Текст с подставленным числом строек.</summary>
    public string Render(int? sites) =>
        CountsSites && sites is { } count ? Text.Replace(SitesPlace, count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) : Text;
}

/// <summary>
/// Какой исход отбора применился — его называет служба строк в ответе (<see cref="ModuleTablePage.Outcome" />).
/// </summary>
/// <param name="Code">Код исхода из объявления таблицы (<see cref="ModuleTable.Boundaries" />).</param>
/// <param name="Sites">Сколько строек в охвате — для места <see cref="ModuleTableBoundary.SitesPlace" />;
/// null — исход стройками не считается.</param>
public sealed record ModuleTableOutcome(string Code, int? Sites = null)
{
    /// <summary>Исход по охвату читателя: код охвата и, у охвата по стройкам, их число.</summary>
    public static ModuleTableOutcome Of(Access.ModuleReach reach) =>
        new(reach.Code, reach.Kind == Access.ModuleReachKind.Sites ? reach.Sites.Count : null);
}

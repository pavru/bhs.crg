namespace BHS.CRG.Application.Settings;

/// <summary>
/// Какие настройки экземпляра знает эта сборка (ТЗ CORE-25.4; issue #1070).
///
/// <para>Интерфейсом, а не статическим <see cref="AppSettingKeys" />, с тех пор как ключи объявляют
/// ещё и модули: каталог ядра о них не знает и знать не должен, а восстановление копии обязано
/// принять настройку модуля так же, как пояс компании.</para>
/// </summary>
public interface IAppSettingCatalog
{
    /// <summary>
    /// Объявлен ли ключ и годится ли значение. Незнакомый ключ или негодное значение из копии не
    /// записывают: настройка, которую здесь никто не читает, выглядела бы действующей.
    /// </summary>
    bool Accepts(string key, string value);

    /// <summary>
    /// Значение в хранимом виде. Копия приходит от другой сборки, и «1» в ней — то же, что «1.00»
    /// здесь: записанное как есть, оно читалось бы верно, а выглядело бы чужим.
    /// </summary>
    string Normalize(string key, string value);

    /// <summary>
    /// Смена действующего значения настройки, о которой положено писать в журнал; <c>null</c> —
    /// писать не о чем: значение то же либо ключ не из тех, чья смена журналируется.
    /// </summary>
    SettingChange? Change(string key, string? before, string? after);
}

/// <summary>Смена настройки словами для журнала действий.</summary>
/// <param name="Label">Чья настройка и как называется.</param>
public sealed record SettingChange(string Label, string Before, string After);

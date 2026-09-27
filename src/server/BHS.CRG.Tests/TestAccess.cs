using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Tests;

/// <summary>
/// Параметр доступа для прогонов (ТЗ CORE-24.1, issue #965).
///
/// <para>Заведён ОДНИМ местом нарочно. Тест, которому параметр нужен лишь чтобы вызов
/// скомпилировался, соберёт его как попало — и первый же прогон, проверяющий сами ворота, окажется
/// среди сотни соседей, собравших себе «всё разрешено» кто как сумел. Здесь видно и то, чего у
/// <see cref="All" /> нет: списка выключенных модулей.</para>
/// </summary>
public static class TestAccess
{
    /// <summary>Коды модулей, которые считаются включёнными в прогоне.</summary>
    private static readonly string[] Modules = ["id"];

    /// <summary>
    /// Человек, которому открыто всё, что нужно наборам ядра и модуля исполнительной документации.
    /// Перечислением, а не «звёздочкой»: у ворот наборов нет подстановочного ключа, и появись он —
    /// проверять было бы нечего.
    /// </summary>
    public static DataAccess All { get; } = DataAccess.Of(
        Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "прогон",
        ["core.catalog.read", "core.datasets.read", "core.datasets.edit", "id",
         "id.document.read", "id.document.edit", "id.document.generate"],
        Modules);

    /// <summary>Человек ровно с этими ключами — для прогонов самих ворот.</summary>
    public static DataAccess With(params string[] keys) => DataAccess.Of(
        Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "прогон", keys, Modules);

    /// <summary>Человек со всеми ключами, но на экземпляре БЕЗ включённых модулей.</summary>
    public static DataAccess WithoutModules(params string[] keys) => DataAccess.Of(
        Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "прогон", keys, []);
}

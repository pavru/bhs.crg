using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Ворота инструмента MCP (issue #948, ТЗ AUTH-12.1): у каждого инструмента — своё право, как у
/// адреса API.
///
/// Почему не хватило ворот на самом адресе <c>/mcp</c>. Адрес один, а инструментов двадцать семь, и
/// они открывают разное: от списка строек до запуска распознавания, которое стирает ручную правку.
/// Единственное, что можно потребовать на таком адресе, — «пользователь вошёл»; значит, любой
/// вошедший получал через агента ровно то, чего ему не даёт интерфейс. Права приходится ставить
/// ВНУТРИ, на каждый инструмент.
///
/// Почему через <see cref="AuthorizeAttribute" />, а не своим механизмом. SDK умеет отбирать
/// инструменты по метаданным метода — этим заняты фильтры из
/// <c>AddAuthorizationFilters()</c> в корне композиции. Значит, тот же самый набор политик
/// (<c>perm:</c>, <c>module:</c>), тот же <c>AppPolicyProvider</c> и те же обработчики, что у
/// адресов: право считается из базы одним кодом, и разойтись двум ответам негде.
///
/// ⚠️ Право у инструмента объявляется НА МЕТОДЕ, а не на классе. Атрибут класса SDK учитывает и
/// работал бы, но тогда новый инструмент в существующем классе молча получал бы соседские ворота —
/// и сторож (<c>McpGateInventoryTests</c>) не смог бы отличить «объявили» от «не подумали».
///
/// Список прав — «любое из» (<see cref="AppPolicies.AnyPermission" />, plan-02): та же политика, что
/// у адреса под тем же списком, иначе инструмент и его адрес открывались бы разным людям.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpPermissionAttribute : AuthorizeAttribute
{
    /// <summary>
    /// Право <paramref name="code" /> обязательно отдельным параметром: <c>[McpPermission]</c> без
    /// права не должен собираться — иначе ошибка вышла бы только на первом вызове инструмента.
    /// </summary>
    public McpPermissionAttribute(string code, params string[] alternatives)
        : base(AppPolicies.AnyPermission([code, .. alternatives])) =>
        Codes = AppPolicies.PermissionCodes(Policy)!;

    /// <summary>
    /// Коды прав — их читает сторож. Взяты из имени политики тем же разбором, что у переписей
    /// адресов: свой список атрибута разошёлся бы с воротами на пробелах и повторах.
    /// </summary>
    public IReadOnlyList<string> Codes { get; }
}

/// <summary>
/// Ворота модуля: инструмент доступен тем, у кого есть хоть одно право этого модуля
/// (<see cref="ModuleAccess" />).
///
/// Нужны там, где такие же ворота стоят на адресах: ЧТЕНИЕ библиотеки документов качества закрыто
/// воротами модуля (правка — правом <c>id.quality.edit</c>). Поставить инструменту право, которого
/// нет у его адреса, значило бы завести второе, расходящееся правило доступа к одним и тем же данным.
///
/// ⚠️ Только для читающих инструментов. С раскрытием «читать всё» (issue #1074) ворота модуля
/// проходит и тот, у кого в модуле одно чтение; изменяющий инструмент под ними достался бы
/// «Руководителю» через агента — это сверяет <c>McpGateInventoryTests</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpModuleAttribute : AuthorizeAttribute
{
    public McpModuleAttribute(string code) : base(AppPolicies.Module(code)) => Code = code;

    /// <summary>Код модуля.</summary>
    public string Code { get; }
}

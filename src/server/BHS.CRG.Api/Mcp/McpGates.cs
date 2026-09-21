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
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpPermissionAttribute : AuthorizeAttribute
{
    public McpPermissionAttribute(string code) : base(AppPolicies.Permission(code)) => Code = code;

    /// <summary>Код права — его читает сторож: имя политики он разбирал бы строкой обратно.</summary>
    public string Code { get; }
}

/// <summary>
/// Ворота модуля: инструмент доступен тем, у кого есть хоть одно право этого модуля
/// (<see cref="ModuleAccess" />).
///
/// Нужны там, где такие же ворота стоят на адресах: библиотека документов качества закрыта воротами
/// МОДУЛЯ и своего права пока не носит. Поставить инструменту право, которого нет у его адреса,
/// значило бы завести второе, расходящееся правило доступа к одним и тем же данным.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpModuleAttribute : AuthorizeAttribute
{
    public McpModuleAttribute(string code) : base(AppPolicies.Module(code)) => Code = code;

    /// <summary>Код модуля.</summary>
    public string Code { get; }
}

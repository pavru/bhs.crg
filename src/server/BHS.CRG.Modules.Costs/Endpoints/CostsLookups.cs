using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Кому открыты справочные списки модуля — поиск номенклатуры и стройки с разделами (ревью PR #1206).
///
/// <para>Этими списками заполняют и счёт, и накладную, а права у документов разные и друг друга не
/// открывают (COST-29). Требовать право на счета значило бы, что роль с одними накладными не выберет
/// ни позицию, ни стройку. Открыть воротами модуля — что названия строек получит всякий, у кого есть
/// хоть одно право модуля, в обход прав ядра на стройки и справочники. Поэтому — «читает счета ИЛИ
/// читает накладные»: оба права сами требуют чтения справочников.</para>
///
/// <para>Фильтром адреса, а не политикой: политика ворот называет ОДНО право, и «любое из двух» ей
/// сказать нечем.</para>
/// </summary>
public static class CostsLookups
{
    private static readonly string[] Readers = ["costs.invoice.read", "costs.waybill.read"];

    public static async ValueTask<object?> RequireDocumentReader(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.RequestServices.GetRequiredService<IModuleUser>();
        var granted = await user.PermissionsAsync(context.HttpContext.RequestAborted);

        if (!Readers.Any(right => granted.Contains(right, StringComparer.OrdinalIgnoreCase)))
            throw new ForbiddenException(
                "Список открыт тому, кто читает счета или накладные: им заполняют эти документы.");

        return await next(context);
    }
}

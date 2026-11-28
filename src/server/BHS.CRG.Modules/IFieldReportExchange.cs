using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules;

/// <summary>Объявление полевого обмена отчётами для хоста.</summary>
public interface IFieldReportExchange
{
    /// <summary>Минимальный Android versionCode отправителя для общих сведений о подключении.</summary>
    int MinimumSenderVersionCode { get; }

    /// <summary>Маршруты v1 с собственной политикой аутентификации устройства.</summary>
    void MapReports(IEndpointRouteBuilder fieldV1);
}

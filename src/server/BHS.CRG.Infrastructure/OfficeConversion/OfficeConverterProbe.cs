using BHS.CRG.Infrastructure.Http;

namespace BHS.CRG.Infrastructure.OfficeConversion;

/// <summary>
/// Отвечает ли конвертер офисных файлов (issue #1267) — проба для мониторинга здоровья.
///
/// <para>Спрашивается его собственная проверка: она отвечает отказом и тогда, когда сама обёртка
/// жива, а LibreOffice под ней не поднялся. Открытый порт этого не скажет.</para>
/// </summary>
public sealed class OfficeConverterProbe(IHttpClientFactory clients, OfficeConverterOptions options)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>Не задан адрес — конвертера у экземпляра нет, и проверять нечего.</summary>
    public bool Configured => options.Configured;

    /// <summary>
    /// Почему сервис не готов; <c>null</c> — готов. Не дозвались вовсе — исключение соединения,
    /// как есть: его разбирает тот, кто спрашивал.
    ///
    /// <para>Причина возвращается, а не бросается: отказ сервиса здесь — ожидаемый ответ, а не
    /// сбой. Тем же способом ответит и вызов преобразования (issue #1268).</para>
    /// </summary>
    public async Task<string?> WhyNotReadyAsync(CancellationToken ct)
    {
        using var http = clients.CreateClient(OfficeConverterOptions.ClientName);
        http.Timeout = Budget;
        try
        {
            using var response = await http.GetAsync(options.At("health"), ct);
            return response.IsSuccessStatusCode ? null : $"Конвертер ответил {(int)response.StatusCode}";
        }
        // Соединение принято, а ответа нет: контейнер под давлением памяти или завис. Это отказ
        // сервиса, а не отмена запроса, хотя тип исключения у них один.
        catch (OperationCanceledException ex) when (HttpFailure.IsTimeout(ex, ct))
        {
            return $"Конвертер не ответил за {HttpFailure.Format(Budget)}";
        }
    }
}

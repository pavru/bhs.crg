using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Common;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Конвейер запроса (вынесено из <c>Program.cs</c>, issue #1030).
///
/// <para>⚠️ Порядок здесь — поведение, а не список: <c>UseForwardedHeaders</c> первым (дальше адрес
/// читают и лимитер, и логи), аутентификация перед авторизацией, лимитер после них.</para>
/// </summary>
internal static class RequestPipeline
{
    /// <summary>Ставит конвейер в том же порядке, в каком он стоял в корне композиции.</summary>
    internal static void UseAppPipeline(this WebApplication app)
    {
    // Описание API поднимается только в Development — у заказчика этого адреса нет вовсе. Ворота всё
    // равно ставятся: адрес перечисляет ВСЕ пути экземпляра вместе с составом включённых модулей, и
    // «его нет в поставке» — это свойство конфигурации, а не запрет. Свойство однажды меняют.
    if (app.Environment.IsDevelopment())
        app.MapOpenApi().RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

    // Первым в конвейере: дальше адрес клиента читают и лимитер, и логи.
    app.UseForwardedHeaders();

    app.UseExceptionHandler(exApp => exApp.Run(async ctx =>
    {
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        ctx.Response.ContentType = "application/json";

        // Само правило — в ApiErrorMapping: оно проверяется тестами, а здесь только конвейер (issue #691).
        var (status, message) = ApiErrorMapping.Describe(ex, ctx.TraceIdentifier);
        ctx.Response.StatusCode = status;

        // В лог уходит именно то, чего не увидел клиент: по идентификатору запроса администратор
        // находит запись целиком. Доменные отказы не логируем как ошибки — это штатный ответ.
        if (status == StatusCodes.Status500InternalServerError)
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("BHS.CRG.UnhandledException")
                .LogError(ex, "Необработанное исключение, запрос {TraceId}", ctx.TraceIdentifier);

        // Идентификатор запроса уходит ОТДЕЛЬНЫМ полем, а не только внутри текста (issue #834): его
        // читает форма «Сообщить об ошибке», и вытаскивать его регулярным выражением из фразы значило
        // бы, что первая же правка формулировки молча отключит кнопку. Только у 500: у доменного отказа
        // («укажите название») искать в логе нечего, и предлагать по нему сообщить об ошибке — шум.
        // Адреса нарушений (issue #957) уходят отдельным полем: по ним клиент подсвечивает поле, а не
        // показывает баннер. У отказа без адресов поле пустое — форма ответа одна на все отказы.
        var details = ApiErrorMapping.DetailsOf(ex);
        await ctx.Response.WriteAsJsonAsync(status == StatusCodes.Status500InternalServerError
            ? new { error = message, traceId = (string?)ctx.TraceIdentifier, details }
            : new { error = message, traceId = (string?)null, details });
    }));

    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    }
}

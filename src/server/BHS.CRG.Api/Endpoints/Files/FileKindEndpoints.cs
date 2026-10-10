using BHS.CRG.Modules.Files;

namespace BHS.CRG.Api.Endpoints.Files;

public static class FileKindEndpoints
{
    /// <summary>
    /// Реестр видов файлов — экрану (issue #1266). Отсюда собираются выбор файла (<c>accept</c>),
    /// перечень «распознаются …» и решение, чем приложенный файл показать: своих перечней экран не
    /// держит, иначе новый вид появлялся бы на сервере, а на экране оставался «только скачать».
    ///
    /// <para>Открыт любому вошедшему: в ответе нет ничего, кроме того, что система умеет, а нужен
    /// он раньше любого права — чтобы предложить выбор файла.</para>
    /// </summary>
    public static void MapFileKindEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/kinds", () => Results.Ok(new
        {
            // Чем отвечает сервер на файл, вида которого не знает. Так же браузер называет файл,
            // тип которого не знает сам, и экрану это один и тот же случай: «решит сервер».
            unknown = FileKinds.Unknown,
            maxBytes = FileKindCatalog.MaxBytes,
            kinds = FileKindCatalog.All.Select(kind => new
            {
                mime = kind.Mime,
                label = kind.Label,
                extensions = kind.Extensions,
                // Как ещё вид называет браузер: отсев до отправки сверяет название, а не содержимое.
                aliases = kind.Aliases,
                view = kind.View switch { FileView.Pdf => "pdf", FileView.Image => "image", _ => null },
                recognized = FileKindCatalog.IsRecognized(kind.Mime),
            }),
        })).RequireAuthorization();
    }
}

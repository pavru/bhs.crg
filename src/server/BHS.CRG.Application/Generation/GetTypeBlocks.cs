using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Собранный <c>typeblocks.typ</c> для просмотра на странице шаблонов (issue #770).
///
/// <para>Зовёт то же ядро <see cref="TypstPreambleBuilder.Build"/>, что генерация и debug-бандл, —
/// иначе показанное расходилось бы с уходящим в Typst, а именно за этим сюда и смотрят. Своей сборки
/// «для показа» здесь нет намеренно: файл производный, и второй источник правды означал бы, что по
/// экрану нельзя судить о документе.</para>
///
/// <para>Без экземпляра документа: блоки собираются из схем типов и от данных не зависят. Тем этот
/// путь и отличается от debug-бандла, где тот же файл достаётся только вместе с конкретным
/// документом и в ZIP.</para>
/// </summary>
public record GetTypeBlocksQuery : IRequest<string>;

public class GetTypeBlocksHandler(IRepository<DocumentType> docTypeRepo)
    : IRequestHandler<GetTypeBlocksQuery, string>
{
    public async Task<string> Handle(GetTypeBlocksQuery q, CancellationToken ct)
        => TypstPreambleBuilder.Build(await docTypeRepo.GetAllAsync(ct));
}

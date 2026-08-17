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
/// <param name="BlockCount">Сколько блоков собрано. «Файл пуст» по содержимому не определить: с #768
/// сборка ВСЕГДА дописывает диспетч-часть (таблица, набор union-типов, хелпер), поэтому даже при нуле
/// блоков файл непустой. Без счётчика экран на свежей установке показывал бы каркас без объяснения,
/// откуда берутся блоки.</param>
public record TypeBlocksView(string Content, int BlockCount);

public record GetTypeBlocksQuery : IRequest<TypeBlocksView>;

public class GetTypeBlocksHandler(IRepository<DocumentType> docTypeRepo)
    : IRequestHandler<GetTypeBlocksQuery, TypeBlocksView>
{
    public async Task<TypeBlocksView> Handle(GetTypeBlocksQuery q, CancellationToken ct)
    {
        var types = await docTypeRepo.GetAllAsync(ct);
        return new TypeBlocksView(
            TypstPreambleBuilder.Build(types),
            types.SelectMany(TypstPreambleBuilder.ExtractRenders).Count());
    }
}

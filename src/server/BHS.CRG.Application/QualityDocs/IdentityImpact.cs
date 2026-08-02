using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Что случится с картой привязок, если сохранить черновик схемы (issue #584).
/// </summary>
/// <param name="Changed">Меняется ли набор или порядок компонентов ключа идентичности.</param>
/// <param name="Before">Компоненты ключа сейчас — в порядке склейки.</param>
/// <param name="After">Компоненты ключа после сохранения — в порядке склейки.</param>
/// <param name="AffectedLinks">Сколько существующих связок «материал → документ качества» перестанут
/// находиться. Либо ВСЕ, либо ни одной: ключ общий для всех материалов, и меняется он целиком.</param>
public record IdentityImpact(
    bool Changed, IReadOnlyList<string> Before, IReadOnlyList<string> After, int AffectedLinks);

/// <summary>
/// Предпросчёт последствий правки полей-идентификаторов (issue #584).
///
/// Составной ключ (#582) склеивается из ВСЕХ полей с тэгом «Идентификатор» по всем типам материала,
/// поэтому добавление поля, снятие тэга или перенумерация меняют ключи всех материалов РАЗОМ —
/// заведённые связки перестают находиться, и происходит это молча: документ выпускается без
/// сертификатов, а в UI ошибки нет.
///
/// Риск не гипотетический: поле «Производитель» несёт тэг и сейчас пусто во всех 151 строке реестра —
/// как только распознавание начнёт его заполнять, ключи изменятся сами собой.
///
/// Считает СЕРВЕР, а не клиент: ключ строит сервер, и вопрос «изменится ли он» должен решаться там
/// же, иначе предупреждение разошлось бы с последствием.
/// </summary>
public record IdentityImpactQuery(Guid TypeId, JsonDocument DraftSchema) : IRequest<IdentityImpact>;

public class IdentityImpactHandler(
    IRepository<DocumentType> typeRepo,
    IRepository<MaterialQualityLink> linkRepo
) : IRequestHandler<IdentityImpactQuery, IdentityImpact>
{
    public async Task<IdentityImpact> Handle(IdentityImpactQuery q, CancellationToken ct)
    {
        var types = await typeRepo.GetAllAsync(ct);
        var before = MaterialIdentity.KeysOf(types);
        // Черновик накладываем КОПИЕЙ типа: у загруженной сущности схему менять нельзя — она
        // отслеживаемая, и чужой SaveChanges записал бы несохранённый черновик в базу.
        var after = MaterialIdentity.KeysOf(
            [.. types.Select(t => t.Id == q.TypeId ? t.WithSchema(q.DraftSchema) : t)]);

        if (before.SequenceEqual(after)) return new(false, before, after, 0);

        // Осиротеют ВСЕ связки, а не только относящиеся к правимому типу: ключ у материалов общий.
        var links = await linkRepo.GetAllAsync(ct);
        return new(true, before, after, links.Count);
    }
}

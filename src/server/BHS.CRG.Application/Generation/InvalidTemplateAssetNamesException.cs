using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Ассеты-картинки шаблона с именем не по правилу (<see cref="TemplateAsset.IsValidImageName" />) —
/// документ не генерируется, ассеты называются (change core-template-asset-names, D3).
///
/// <para>Новый такой ассет не создать: правило стоит на загрузке. Сюда приходят прежние данные и
/// резервные копии. Отказ, а не документ без картинки: шаблон, выводящий её под условием, собрался бы,
/// и человек получил бы другой документ молча. Род — <see cref="InvalidRequestException" /> (400), как у
/// <see cref="UnreadableImagesException" />: исправляет сам администратор — переименования нет, путь
/// один: удалить ассет и загрузить заново.</para>
/// </summary>
public sealed class InvalidTemplateAssetNamesException(IReadOnlyList<string> names) : InvalidRequestException(
    "Ассеты шаблона с недопустимым именем: " + string.Join(", ", names.Select(n => $"«{n}»")) +
    $". Удалите их и загрузите заново под именем, где только {TemplateAsset.ImageNameRule}.")
{
    /// <summary>Имена ассетов как они записаны.</summary>
    public IReadOnlyList<string> Names { get; } = names;
}

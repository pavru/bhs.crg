using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Картинки полей не прочитаны из хранилища — документ не генерируется, поля называются (ТЗ CORE-37.2).
///
/// <para>Отказ, а не документ без картинки: шаблон, выводящий картинку под условием, собрался бы, и
/// человек получил бы PDF без подписи или фото, узнав об этом разве что из журнала сервера — отказ,
/// переодетый в результат. Род — <see cref="InvalidRequestException" /> (400), как у
/// <see cref="TemplateCompilationException" />: исправляет сам пользователь, загрузив картинку заново.</para>
///
/// <para>Причину текст не различает: файла нет и файл не документа (файл модуля, вписанный в
/// реквизиты) для генерации одно и то же, а разница между ними сама была бы сведением о чужом файле.</para>
/// </summary>
public sealed class UnreadableImagesException(IReadOnlyList<string> fields) : InvalidRequestException(
    "Картинки полей не прочитаны — файла нет или он недоступен документу: " + string.Join(", ", fields) +
    ". Загрузите картинку в поле заново.")
{
    /// <summary>Адреса полей от корня реквизитов: «Подпись», «Фото[2].Снимок».</summary>
    public IReadOnlyList<string> Fields { get; } = fields;
}

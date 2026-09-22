using System.Text.Json;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Незаполненные обязательные поля (issue #957, ТЗ CORE-20).
///
/// <para><b>Обязательность — инвариант ПЕРЕХОДА, а не сохранения.</b> Черновик хранится неполным:
/// наполовину распознанный счёт и недописанный отчёт сохранить надо, а выпустить нельзя. Поэтому
/// охрана записи (<see cref="RecordWriteGuard"/>) обязательность не проверяет вовсе — и это решение,
/// а не пропуск.</para>
///
/// <para><b>Почему это отдельная функция, а не новый адрес.</b> Переходов («подан», «разобран»,
/// «выдана на подпись») в этапе 1 нет — их объявляет модуль, а модулей нет. Заводить точку входа с
/// нулём вызывающих нельзя: она проверена ровно настолько, насколько угадан её вход. Зато проверка
/// уже существует и уже имеет потребителя — диагностика генерации, — и здесь она просто ВЫНЕСЕНА,
/// чтобы переход этапа 2 позвал ту же функцию, а не написал вторую. Тот же приём, что с
/// <see cref="ValueTypeRules"/> (issue #642), где двух реализаций одного правила мы уже наелись.</para>
///
/// <para>⚠️ Вход — РАЗРЕШЁННЫЙ контекст (реквизиты + привязки наборов + базовый экземпляр +
/// значения по умолчанию), а не сырые данные записи. По сырым «не заполнено» означало бы совсем
/// другое: поле, приходящее из привязки, числилось бы пустым. Это же причина не замораживать
/// контракт перехода сейчас: резолв тянет весь пайплайн генерации, и как его позовёт модуль — мы
/// сегодня угадаем.</para>
/// </summary>
public static class RequiredFields
{
    /// <summary>
    /// Обязательные поля, оставшиеся пустыми. <paramref name="valueOf"/> — значение поля в
    /// разрешённом контексте (null, если ключа нет).
    /// </summary>
    public static IReadOnlyList<SchemaFieldInfo> Missing(
        IReadOnlyList<SchemaFieldInfo> fields, Func<string, object?> valueOf)
        => [.. fields.Where(f => f.Required
                                 // Расчётные производны: «обязательность» к ним неприменима (#368).
                                 && !f.Computed
                                 && IsEmpty(valueOf(f.Key)))];

    /// <summary>
    /// Пусто ли значение. Пустая строка и пустой список — это НЕЗАПОЛНЕНО: форма пишет именно их,
    /// когда поле очищают, и «ключ есть» не значит «значение есть».
    /// </summary>
    public static bool IsEmpty(object? v)
    {
        if (v is null) return true;
        if (v is JsonElement el)
            return el.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => true,
                JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString()),
                JsonValueKind.Array => el.GetArrayLength() == 0,
                JsonValueKind.Object => !el.EnumerateObject().Any(),
                _ => false, // number / true / false — заполнено
            };
        return false; // резолвнутые не-JSON значения считаем заполненными
    }
}

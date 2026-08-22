using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Settings;

/// <summary>
/// След работы фоновой службы: что она уже сделала и когда (issue #813). Ключ + JSON одной строкой.
///
/// Отдельно от <see cref="IntegrationSettingsEntity"/> намеренно, и это не эстетика. Настройки
/// пишутся схемой «прочитать весь документ → поправить → записать целиком», и там уже есть шрам от
/// частичного сохранения (SaveAsync намеренно не трогает секцию SMTP). Пока все писатели —
/// человеческие и редкие, гонок нет; периодическая служба сделала бы конкурентную перезапись
/// рядовым событием: человек сохраняет почту ровно тогда, когда служба пишет свой след, и один из
/// двух проигрывает молча.
///
/// Граница проходит по вопросу «кто это задал»: выключатель проверки задал человек — он настройка;
/// «о какой версии уже уведомляли» записала служба — это состояние.
/// </summary>
public class ServiceStateEntity : Entity
{
    /// <summary>Кто владеет записью, напр. <c>update-check</c>. Уникален.</summary>
    public string Key { get; private set; } = null!;

    public JsonDocument Data { get; private set; } = null!;

    private ServiceStateEntity() { }

    public static ServiceStateEntity Create(string key, JsonDocument data)
        => new() { Key = key, Data = data };

    public void Update(JsonDocument data)
    {
        Data = data;
        TouchUpdatedAt();
    }
}

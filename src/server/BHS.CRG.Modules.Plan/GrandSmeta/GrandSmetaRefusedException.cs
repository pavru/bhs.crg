using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Почему файл ГРАНД-Сметы не разобран вовсе (ТЗ PLAN-7, STG-12). Чинятся причины по-разному:
/// неизвестную версию — строкой списка у разработчика, файл без итогов — сметчик в программе.</summary>
public enum GrandSmetaRefusal
{
    /// <summary>Не ZIP с <c>Data.xml</c> и не XML ГРАНД-Сметы — не тот файл.</summary>
    NotGrandSmeta,

    /// <summary>XML ГРАНД-Сметы, но вид документа (GUID <c>DocumentType</c>) не из читаемых.</summary>
    UnknownKind,

    /// <summary>Версии программы нет в списке <see cref="GrandSmetaSchemas" />.</summary>
    UnsupportedVersion,

    /// <summary>Читателю дали документ другого вида (ВОР — читателю ЛСР).</summary>
    WrongKind,

    /// <summary>Документ не прошёл схему вне позиций или в нём нет разделов (PLAN-7).</summary>
    DocumentInvalid,

    /// <summary>ЛСР сохранена без итогов, и досчитать их нельзя: метод или настройка расчёта вне закрытого
    /// перечня досчёта (PLAN-8) — текст называет их.</summary>
    NoTotals,
}

/// <summary>
/// Файл ГРАНД-Сметы не разобран — и говорит об этом отказом с названием причины, а не пустой моделью:
/// «0 позиций» неотличимо от «в смете ничего нет». Текст наш и адресован человеку — его покажет запись
/// фоновой операции импорта (<c>plan-07</c>).
/// </summary>
public sealed class GrandSmetaRefusedException(GrandSmetaRefusal reason, string message)
    : InvalidRequestException(message)
{
    public GrandSmetaRefusal Reason { get; } = reason;

    /// <summary>Вид документа, если до отказа его узнали (неизвестная версия): отчёт импорта назовёт его.</summary>
    public GrandSmetaKind? Kind { get; init; }

    /// <summary>Версия программы, если до отказа её прочли (неизвестные вид или версия).</summary>
    public string? ProgramVersion { get; init; }
}

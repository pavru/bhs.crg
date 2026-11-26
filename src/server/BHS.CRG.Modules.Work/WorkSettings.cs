using BHS.CRG.Modules.Settings;

namespace BHS.CRG.Modules.Work;

/// <summary>
/// Настройки модуля «Учёт работ» (ТЗ WORK-33, CORE-25.3): лимиты фото строки (<c>work-17</c>). Читает их путь загрузки
/// фото на каждом запросе — смена действует со следующей загрузки.
///
/// <para>⚠️ Здесь только то, что уже кто-то читает. Длинная сторона снимка и превью — не настройка: от них зависит, как
/// фото читаются в галерее и в копии, а не сколько места они занимают у экземпляра.</para>
/// </summary>
public static class WorkSettings
{
    /// <summary>Предел файла фото — до чтения целиком и до записи в хранилище (ТЗ WORK-33).</summary>
    public static readonly NumberSetting PhotoMaxMegabytes = new(
        "work.photos.max-size",
        "Предел файла фото",
        "Фото больше предела не принимается: файл отвергается до записи в хранилище, отказ называет предел. " +
        "Принятое фото сервер всегда пересжимает, так что предел ограничивает файл с телефона или из браузера, " +
        "а не размер хранимого снимка.",
        DefaultValue: 15m, Min: 1m, Max: 50m, Unit: "МБ");

    /// <summary>Сколько фото принимает одна строка работы (ТЗ WORK-33).</summary>
    public static readonly NumberSetting PhotosPerLine = new(
        "work.photos.per-line",
        "Фото на строку работы",
        "Сколько фото можно прикрепить к одной строке отчёта — объявленных и пришедших вместе. Фото сверх " +
        "числа отвергается с названным пределом. Уже прикреплённые фото уменьшение не снимает.",
        DefaultValue: 10m, Min: 1m, Max: 50m);

    public static readonly NumberSetting ProductivityReports = new(
        "work.productivity.min-reports", "Минимум отчётов для выработки",
        "Если в выборке меньше принятых пригодных отчётов, за этот период показывается «данных мало».",
        DefaultValue: 10m, Min: 1m, Max: int.MaxValue);

    public static readonly NumberSetting ProductivityConstructions = new(
        "work.productivity.min-constructions", "Минимум строек для выработки",
        "Если в выборке меньше строек, за этот период показывается «данных мало».",
        DefaultValue: 2m, Min: 1m, Max: int.MaxValue);

    public static IReadOnlyList<ModuleSetting> All =>
        [PhotoMaxMegabytes, PhotosPerLine, ProductivityReports, ProductivityConstructions];
}

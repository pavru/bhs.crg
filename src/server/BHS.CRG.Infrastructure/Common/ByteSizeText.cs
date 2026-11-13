using System.Globalization;

namespace BHS.CRG.Infrastructure.Common;

/// <summary>
/// Размер в байтах так, как его прочтёт человек: «15 МБ», «1,5 МБ», «500 КБ», «2,3 ГБ». Одна запись на
/// сервер — отказ фото по лимиту и уведомление о готовой копии говорят о размере одинаково. Запятая —
/// русская всегда: текст русский, а язык потока сервера от поставки не зависит.
/// </summary>
public static class ByteSizeText
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("0.#", Ru) + " ГБ",
        >= 1024L * 1024 => (bytes / 1024d / 1024).ToString("0.#", Ru) + " МБ",
        >= 1024 => (bytes / 1024d).ToString("0.#", Ru) + " КБ",
        _ => bytes + " Б",
    };
}

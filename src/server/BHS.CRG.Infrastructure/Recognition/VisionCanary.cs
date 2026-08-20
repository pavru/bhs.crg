using System.Text.RegularExpressions;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// «Канарейка зрения» (issue #801, выделено из #481): картинка, которую движку показывают ПЕРЕД
/// работой, и правило, по которому его ответ читается как «вижу» или «не вижу».
///
/// Зачем вообще: слепота модели не даёт ни исключения, ни пустого результата. Модель получает
/// список запрошенных полей и добросовестно его заполняет — «ООО ЭнергоСтрой», ГИП «Иванов А.Б.» —
/// а задача завершается успехом. Замер 2026-07-27: альбом из 16 листов, ошибок ноль, совпадение с
/// эталоном 0 из 106 полей. Ловится это только прямым вопросом «что ты видишь».
/// </summary>
public static class VisionCanary
{
    /// <summary>
    /// Картинка — константа в коде, не файл-ресурс и не генерация на лету: канарейка обязана быть
    /// побайтово одной и той же, иначе «модель слепа» и «мы сгенерировали кривой PNG» становятся
    /// неразличимы — то есть проверка приобретает ровно тот порок, который ищет.
    ///
    /// 174 байта: PNG 96×64 из трёх вертикальных полос — зелёная, пурпурная, жёлтая.
    /// </summary>
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAGAAAABACAIAAABqVuVZAAAAdUlEQVR42u3QQQ0AIAwAsfOvAnXYGCqW8GhSBa3Tqml23VYlSJAg" +
        "QYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIECRIkSJAgQYIE" +
        "/Rf0AHJYGh33ELryAAAAAElFTkSuQmCC";

    public static byte[] Png { get; } = Convert.FromBase64String(PngBase64);

    public const string MimeType = "image/png";

    /// <summary>
    /// Поле-заглушка. Движку список полей нужен для промпта, но ответ канарейки разбирается СЫРЫМ
    /// текстом, а не <see cref="RecognitionShared.ParseValues" />: модель вправе ответить прозой, и
    /// «не отдала валидный JSON» — не «не увидела картинку».
    /// </summary>
    private static readonly IReadOnlyList<RecognitionField> CanaryFields =
        [new RecognitionField("colors", "Цвета полос слева направо", "string")];

    public static IReadOnlyList<RecognitionField> Fields => CanaryFields;

    /// <summary>
    /// Промпт НЕ называет цвета — и это главное в нём. Назови мы их («красный ли слева?»), слепая
    /// модель повторила бы их из вопроса, и канарейка выдала бы ей справку о зрении.
    ///
    /// По той же причине полос три, а не две, и цвета взяты неочевидные: пару из ходовой палитры
    /// («красный, синий») слепая модель угадывает наугад с заметной вероятностью, набор из трёх
    /// редких — практически нет. Порядок в вопросе спрашивается, но вердиктом не проверяется
    /// (см. <see cref="SeesImage" />): он нужен, чтобы модель описывала картинку, а не угадывала.
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<RecognitionField> _)
        => "На картинке несколько вертикальных цветных полос. Перечисли их цвета СЛЕВА НАПРАВО. " +
           "Ответ — один JSON-объект {\"colors\": [\"цвет\", \"цвет\", ...]} без markdown и пояснений.";

    /// <summary>
    /// Цвета полос по порядку и слова, которыми их называют. Списки щедрые и двуязычные: модель
    /// отвечает как хочет, а спрашиваем мы не про терминологию, а про то, дошла ли картинка. Ложное
    /// «слепа» дороже пропуска — оно отключит рабочее распознавание.
    /// </summary>
    private static readonly string[][] Stripes =
    [
        ["зелён", "зелен", "лайм", "салат", "изумруд", "green", "lime"],
        ["пурпур", "малин", "фуксия", "маджент", "розов", "сирен", "magenta", "fuchsia", "purple", "pink", "violet"],
        ["жёлт", "желт", "лимон", "золот", "yellow", "gold", "amber", "lemon"],
    ];

    /// <summary>
    /// Увидела ли модель картинку: названы ли все три цвета. Порядок НЕ требуется, хотя промпт про
    /// него спрашивает, — набор из трёх редких цветов наугад не составить, а зрячая модель, которая
    /// перепутала две правые полосы или прочла справа налево, получила бы за это запрет на работу.
    /// Проверка спрашивает «дошла ли картинка», а не «аккуратна ли модель».
    ///
    /// Всё остальное — молчание, мусор, отказ — не «слепа», а «не проверили»; решение об этом
    /// принимает вызывающий (см. IRecognitionModelCatalog).
    /// </summary>
    public static bool SeesImage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = Normalize(raw);
        return Stripes.All(stripe => stripe.Any(w => text.Contains(w, StringComparison.Ordinal)));
    }

    /// <summary>Ответ модели в нижнем регистре и без разметки — для поиска слов, а не разбора.</summary>
    private static string Normalize(string raw)
        => Regex.Replace(RecognitionShared.StripFences(raw).ToLowerInvariant(), @"\s+", " ");

    /// <summary>Чем ответила модель — в сообщение пользователю и в лог, усечённо.</summary>
    public static string Excerpt(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "" : RecognitionShared.Truncate(Normalize(raw), 200);
}

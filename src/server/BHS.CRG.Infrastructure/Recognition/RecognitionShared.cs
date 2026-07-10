using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Общие для всех движков распознавания части: промпт и разбор ответа.</summary>
public static class RecognitionShared
{
    public static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };

    public static string NormalizeImageMime(string mime)
        => string.Equals(mime, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mime.ToLowerInvariant();

    public static string BuildPrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь реквизиты из скан-копии документа (сертификат/декларация соответствия и т.п.).");
        AppendCommonInstructions(sb, fields);
        return sb.ToString();
    }

    /// <summary>Промпт для распознавания основной надписи (штампа) чертежа/документа по ГОСТ Р 21.101-2020 —
    /// вместо общей формулировки про сертификаты (для точности на плотном мелком штампе).</summary>
    public static string BuildTitleBlockPrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь данные из основной надписи (штампа) листа проектной/рабочей документации");
        sb.AppendLine("по ГОСТ Р 21.101-2020 (обычно правый нижний угол листа, реже — верх текстового документа).");
        sb.AppendLine("ВАЖНО: все значения бери ТОЛЬКО из граф самого штампа. НЕ используй текст из тела листа —");
        sb.AppendLine("заголовки таблиц, ведомостей, спецификаций, штампов согласования и т.п. (напр. заголовок");
        sb.AppendLine("«Ведомость рабочих чертежей комплекта ЭМ» — это НЕ наименование документа).");
        AppendCommonInstructions(sb, fields);
        if (fields.Any(f => f.Path == "ТипСтраницы"))
        {
            sb.AppendLine();
            sb.AppendLine("Поле ТипСтраницы — классифицируй страницу:");
            sb.AppendLine("- Обложка — заглавный лист комплекта, на котором НЕТ подписей и фамилий исполнителей (графы подписей/фамилий пустые). Обычно самый первый лист.");
            sb.AppendLine("- ТитульныйЛист — заглавный лист комплекта, на котором ЕСТЬ подписи и фамилии исполнителей/согласующих.");
            sb.AppendLine("- Документ — обычный рабочий лист с заполненной основной надписью (штампом с шифром и номером листа) — подавляющее большинство страниц.");
            sb.AppendLine("ГЛАВНЫЙ признак различия обложки и титульного листа — подписи: подписей нет → Обложка, подписи есть → ТитульныйЛист. Если на листе полноценный штамп с шифром и номером листа — это Документ.");
        }
        if (fields.Any(f => f.Path == GostTitleBlockFields.StampFormPath))
        {
            sb.AppendLine();
            sb.AppendLine("Поле Форма — классифицируй ФОРМУ основной надписи по ГОСТ Р 21.101-2020:");
            sb.AppendLine("- Форма3 — чертёж/схема, ПЕРВЫЙ лист основного комплекта рабочих чертежей; крупная табличка, обычно заполнены Организация/Масштаб/ВидДокументации.");
            sb.AppendLine("- Форма4 — первый лист чертежа СТРОИТЕЛЬНОГО ИЗДЕЛИЯ (похожа на форму 3 по размеру, но для отдельного изделия, не основного комплекта).");
            sb.AppendLine("- Форма5 — первый/заглавный лист ТЕКСТОВОГО документа (пояснительная записка, спецификация, ведомость и т.п.).");
            sb.AppendLine("- Форма6 — ЛЮБОЙ ПОСЛЕДУЮЩИЙ (не первый) лист — чертежа, изделия, эскиза или текстового документа. Табличка ЗАМЕТНО МЕНЬШЕ, чем на первом листе (обычно только Шифр и номер листа, остальные графы физически отсутствуют, не просто пусты).");
            sb.AppendLine("Если табличка маленькая (лист явно не первый) — Форма6. Если сомневаешься между 3/4/5 для первого листа — выбирай Форма3.");
            sb.AppendLine("Для листов Форма6 НЕ заполняй НаименованиеДокумента — по ГОСТ на последующих листах наименование документа не повторяется (оставь пустым, НЕ бери текст из содержимого/таблицы листа).");
        }
        return sb.ToString();
    }

    /// <summary>Промпт для ЗАГЛАВНОГО листа комплекта (обложка/титульный лист): в отличие от
    /// <see cref="BuildTitleBlockPrompt"/> здесь НЕТ штампа в углу — реквизиты по всему листу, поэтому
    /// читаем ТЕЛО листа, а не штамп (набор полей — <see cref="GostCoverTitleFields"/>).</summary>
    public static string BuildCoverTitlePrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь реквизиты с ЗАГЛАВНОГО листа (обложка или титульный лист) комплекта");
        sb.AppendLine("проектной/рабочей документации. На таком листе НЕТ основной надписи (штампа) в углу —");
        sb.AppendLine("реквизиты размещены по ВСЕМУ листу крупным титульным блоком. Читай значения из всего");
        sb.AppendLine("листа: наименование объекта, наименование комплекта/раздела, шифр, организацию-");
        sb.AppendLine("разработчика, стадию (П/Р), город, год, ФИО ГИП. Если значения на листе нет — верни");
        sb.AppendLine("пустую строку, НЕ выдумывай.");
        AppendCommonInstructions(sb, fields);
        return sb.ToString();
    }

    /// <summary>Промпт распознавания штампа + «опора» (grounding): точный текст, извлечённый из
    /// текстового слоя/аннотаций PDF в области штампа (см. GostStampTextExtractor). Модель должна
    /// предпочитать этот текст тому, что «видит» на картинке, для совпадающих граф — это устраняет
    /// ошибки OCR в шифре/наименовании там, где точный текст в PDF есть.</summary>
    public static string BuildTitleBlockPromptWithGrounding(
        IReadOnlyList<RecognitionField> fields, IReadOnlyList<string> stampText)
    {
        var sb = new StringBuilder();
        sb.Append(BuildTitleBlockPrompt(fields));
        sb.AppendLine();
        sb.AppendLine("В PDF найден ТОЧНЫЙ текст в области штампа (извлечён из текстового слоя/аннотаций файла).");
        sb.AppendLine("Это ИСТИНА: для граф, где он подходит, бери значение из него, а не из изображения");
        sb.AppendLine("(особенно Шифр и Наименование — не «исправляй» их по картинке). Строки текста штампа:");
        foreach (var t in stampText) sb.Append("- ").AppendLine(t);
        return sb.ToString();
    }

    /// <summary>Промпт для распознавания счёта на оплату целиком (весь многостраничный документ
    /// одним вызовом, не постранично) — шапка + вложенная таблица товаров/услуг одним полем.</summary>
    public static string BuildInvoicePrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь реквизиты из счёта на оплату (может быть на нескольких страницах —");
        sb.AppendLine("это ОДИН документ, не путай разные страницы с разными счетами).");
        AppendCommonInstructions(sb, fields);
        sb.AppendLine();
        sb.Append("Поле ").Append(InvoiceFields.LineItemsPath)
          .AppendLine(" — верни ЗНАЧЕНИЕМ этого ключа настоящий JSON-массив объектов");
        sb.AppendLine("(не строку), по одному объекту на строку таблицы товаров/услуг. Если товаров нет — пустой массив [].");
        return sb.ToString();
    }

    /// <summary>Промпт для распознавания ТАБЛИЦЫ документа ГОСТ (спецификация/ведомость или кабельный
    /// журнал) — весь под-документ одним вызовом, строки таблицы одним полем-массивом (см. GostTableFields).</summary>
    public static string BuildTablePrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь ТАБЛИЦУ из документа проектной/рабочей документации по ГОСТ");
        sb.AppendLine("(спецификация/ведомость материалов и оборудования либо кабельный журнал).");
        AppendCommonInstructions(sb, fields);
        sb.AppendLine();
        sb.Append("Поле ").Append(GostTableFields.RowsPath)
          .AppendLine(" — верни ЗНАЧЕНИЕМ этого ключа настоящий JSON-массив объектов (не строку),");
        sb.AppendLine("по одному объекту на СТРОКУ таблицы. Заголовки/итоги/пустые строки НЕ включай.");
        sb.AppendLine("Ключи объектов — из перечисленных выше колонок; пустая ячейка — пустая строка.");
        return sb.ToString();
    }

    /// <summary>Промпт для ленивого извлечения ВСЕГО текста документа (issue #51) — субстрат для
    /// пользовательских вычисляемых колонок (regex и т.п.), не структурированные поля.</summary>
    public static string BuildFullTextPrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь ВЕСЬ текст документа (скан-копия, одна или несколько страниц) для");
        sb.AppendLine("последующего текстового поиска. Включай весь читаемый текст: штампы, таблицы, подписи,");
        sb.AppendLine("примечания — ничего не пропускай и не суммаризируй.");
        AppendCommonInstructions(sb, fields);
        sb.AppendLine();
        sb.Append("Поле ").Append(DocumentTextFields.Path)
          .AppendLine(" — верни ОДНОЙ строкой: страницы по порядку следования, на каждой странице —");
        sb.AppendLine("сверху вниз, слева направо; фрагменты раздели ОДНИМ пробелом. Без переносов строк и markdown.");
        return sb.ToString();
    }

    private static void AppendCommonInstructions(StringBuilder sb, IReadOnlyList<RecognitionField> fields)
    {
        sb.AppendLine("Извлеки значения СТРОГО для перечисленных полей. Ответ — один JSON-объект {\"путь\": \"значение\"} без markdown и пояснений.");
        sb.AppendLine("Даты возвращай в ISO (ГГГГ-ММ-ДД). Если значения нет — пустая строка. Не выдумывай.");
        sb.AppendLine();
        sb.AppendLine("Поля (путь — название — тип):");
        foreach (var f in fields)
        {
            sb.Append("- ").Append(f.Path).Append(" — ").Append(f.Title).Append(" — ").Append(f.Type);
            if (f.Options is { Count: > 0 }) sb.Append(" (варианты: ").Append(string.Join(", ", f.Options)).Append(')');
            sb.AppendLine();
        }
    }

    public static IReadOnlyDictionary<string, string?> ParseValues(string text, IReadOnlyList<RecognitionField> fields)
    {
        var result = new Dictionary<string, string?>();
        var jsonText = StripFences(text).Trim();
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => prop.Value.GetRawText(),
                    };
                    if (!string.IsNullOrWhiteSpace(v)) result[prop.Name] = v;
                }
        }
        catch (JsonException) { /* не-JSON ответ — вернём пусто */ }

        var allowed = fields.Select(f => f.Path).ToHashSet();
        return result.Where(kv => allowed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var inner = s[(firstNl + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? inner[..lastFence] : inner;
    }

    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Один движок распознавания (Anthropic/Gemini/Ollama). Возвращает СЫРОЙ текст модели.</summary>
public interface IRecognizerEngine
{
    string Name { get; }
    Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default);
}

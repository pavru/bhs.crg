using System.Globalization;

namespace BHS.CRG.Modules.Settings;

/// <summary>
/// Настройка, которую объявляет модуль (задача M1, issue #1070; ТЗ CORE-25.3).
///
/// <para>Объявление — тот же договор, что право или таблица: модуль называет ключ, говорит человеку,
/// что настройка меняет, и сам проверяет значение. Ядро хранит строку, показывает её администратору и
/// отдаёт модулю обратно — о смысле настройки оно не знает ничего.</para>
///
/// <para>⚠️ <b>Объявляют то, у чего есть потребитель.</b> Настройка, которую никто не читает,
/// сохраняется успешно и не действует — отказ, переодетый в удавшуюся запись. Ключ приезжает вместе с
/// работой, которая его читает, а не «на будущее».</para>
/// </summary>
/// <param name="Key">Ключ вида «модуль.объект.настройка»; первая часть — код модуля-владельца.</param>
/// <param name="Title">Подпись поля на экране администратора.</param>
/// <param name="Effect">Что изменится и для чего — следствие, а не пересказ названия.</param>
public abstract record ModuleSetting(string Key, string Title, string Effect)
{
    /// <summary>Предел длины ключа: он попадает в адрес и в журнал.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>
    /// О чём предупредить ПЕРЕД сохранением нового значения; <c>null</c> — предупреждать не о чем.
    ///
    /// <para>Нужно настройке, смена которой действует на уже записанные данные: администратор обязан
    /// узнать об этом до записи, а не по изменившимся суммам.</para>
    /// </summary>
    public string? ChangeWarning { get; init; }

    /// <summary>Вид значения — по нему экран выбирает поле.</summary>
    public abstract string Kind { get; }

    /// <summary>Умолчание в том же виде, в каком значение хранится.</summary>
    public abstract string DefaultText { get; }

    /// <summary>Почему значение не годится; <c>null</c> — годится.</summary>
    public abstract string? Refuse(string value);

    /// <summary>Значение в виде, в котором его хранят: «1» и «1.00» — одна и та же настройка.</summary>
    public abstract string Normalize(string value);

    /// <summary>
    /// Годное значение словами для человека — в журнал действий и в тексты отказов: «0,50 ₽», а не
    /// «0.50». Хранимый вид — для машины, и читать его в журнале пришлось бы с оглядкой на ключ.
    /// </summary>
    public abstract string Display(string value);

    /// <summary>Код модуля-владельца — первая часть ключа.</summary>
    public string Module => Key.Split('.')[0];

    /// <summary>Что не так с объявлением; <c>null</c> — объявление годное.</summary>
    public virtual string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Key)) return "ключ пуст";

        var parts = Key.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            return $"«{Key}» — ключ обязан быть вида «модуль.объект.настройка»";

        if (Key.Length > MaxKeyLength)
            return $"«{Key}» — ключ длиннее {MaxKeyLength} знаков";

        if (string.IsNullOrWhiteSpace(Title))
            return $"«{Key}» — у настройки нет подписи";

        if (string.IsNullOrWhiteSpace(Effect))
            return $"«{Key}» — не сказано, что настройка меняет";

        return Refuse(DefaultText) is { } why
            ? $"«{Key}» — умолчание «{DefaultText}» не проходит собственную проверку: {why}"
            : null;
    }
}

/// <summary>
/// Настройка со значением типа <typeparamref name="T" />. Модуль держит её статическим полем и этим
/// же объектом и объявляет, и читает: порт принимает объявление, а не строку, поэтому прочитать ключ
/// с опечаткой нельзя — он не скомпилируется.
/// </summary>
public abstract record ModuleSetting<T>(string Key, string Title, string Effect)
    : ModuleSetting(Key, Title, Effect)
{
    public abstract T Default { get; }

    /// <summary>
    /// Значение по сохранённой строке. Нет строки или она не проходит проверку — действует умолчание:
    /// негодное значение может лежать в базе после смены границ в новой версии, и ронять из-за него
    /// каждый расчёт нельзя.
    /// </summary>
    public abstract T Read(string? stored);
}

/// <summary>Число с границами и единицей: «допуск, ₽».</summary>
/// <param name="Scale">Сколько знаков после запятой допускается.</param>
/// <param name="Unit">Единица для подписи; <c>null</c> — число без единицы.</param>
public sealed record NumberSetting(
    string Key, string Title, string Effect,
    decimal DefaultValue, decimal Min, decimal Max, int Scale = 0, string? Unit = null)
    : ModuleSetting<decimal>(Key, Title, Effect)
{
    public override string Kind => "number";

    public override decimal Default => DefaultValue;

    public override string DefaultText => Text(DefaultValue);

    public override string? Refuse(string value)
    {
        if (!TryParse(value, out var number))
            return "нужно число";

        if (number < Min || number > Max)
            return $"допустимо от {Human(Min)} до {Display(Text(Max))}";

        // Лишние знаки — отказ, а не округление: «0,005» при шаге в копейку человек набрал не затем,
        // чтобы получить «0,01» или «0,00» по выбору сервера.
        if (decimal.Round(number, Scale) != number)
            return Scale == 0 ? "нужно целое число" : $"не больше {Scale} знаков после запятой";

        return null;
    }

    public override string Normalize(string value) => Text(Parse(value));

    public override string Display(string value) =>
        Unit is null ? Human(Parse(value)) : $"{Human(Parse(value))} {Unit}";

    public override decimal Read(string? stored) =>
        stored is not null && Refuse(stored) is null ? Parse(stored) : DefaultValue;

    public override string? Validate()
    {
        if (Scale is < 0 or > 6) return $"«{Key}» — знаков после запятой может быть от 0 до 6";
        if (Min > Max) return $"«{Key}» — нижняя граница больше верхней";
        return base.Validate();
    }

    private string Text(decimal number) => number.ToString("F" + Scale, CultureInfo.InvariantCulture);

    private string Human(decimal number) => Text(number).Replace('.', ',');

    // Ноль — без знака: «-0» проходит границу «не меньше нуля», а decimal знак нуля помнит и
    // печатает — в базе и в журнале оказалось бы «-0,00 ₽» (ревью PR #1249).
    private static decimal Parse(string value) => TryParse(value, out var number) && number != 0 ? number : 0m;

    // Разделитель — точка: значение хранится и едет по сети в одном виде, а запятую человека
    // переводит экран. Пробелы и разделители тысяч не принимаются — «1 000» и «1,000» читались бы
    // по-разному в разных местах.
    private static bool TryParse(string value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number);
}

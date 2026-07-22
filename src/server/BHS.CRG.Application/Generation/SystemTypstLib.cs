namespace BHS.CRG.Application.Generation;

/// <summary>
/// Системная Typst-библиотека (issue #344) — ХАРДКОД, только чтение. Третий уровень рядом с
/// <c>userlib.typ</c> (админ-редактируемый) и <c>typeblocks.typ</c> (автоген). Содержит системные
/// хелперы (сейчас <c>instance-of</c>) и авто-подключается к каждому шаблону при компиляции —
/// авторам шаблонов её импортировать не нужно. Просмотр — <c>GET /api/templates/systemlib</c> и
/// read-only режим на странице шаблонов.
/// </summary>
public static class SystemTypstLib
{
    public const string FileName = "systemlib.typ";

    /// <summary>Содержимое библиотеки. Держим здесь единым источником — эмитится в генерацию,
    /// debug-bundle и read-only просмотр.</summary>
    public const string Content =
        "// systemlib.typ — системная библиотека BHS.CRG (только чтение, авто-подключается к шаблону).\n" +
        "//\n" +
        "// instance-of(it, code): полиморфная проверка типа объекта по метаполю _type (issue #342/#344).\n" +
        "// true, если тип объекта РАВЕН code ЛИБО является его потомком — chain включает сам тип и всех\n" +
        "// предков (self→root). Устойчива к объектам без _type и к не-словарям.\n" +
        "//   #if instance-of(it, \"Организация\") [ … ]  — сработает и для потомков «Подрядчик»/«Проектировщик».\n" +
        "#let instance-of(it, code) = type(it) == dictionary and code in it.at(\"_type\", default: (:)).at(\"chain\", default: ())\n";

    /// <summary>Стандартные импорты, которые вставляются в НАЧАЛО шаблона ТОЛЬКО ПРИ СОЗДАНИИ шаблона
    /// (issue #353): systemlib + typeblocks. Компиляция/превью/debug-bundle шаблон НЕ трогают — он
    /// компилируется дословно, поэтому импорты обязаны жить в самом содержимом шаблона.</summary>
    public static readonly string Prelude =
        $"#import \"{FileName}\": *\n#import \"typeblocks.typ\": *\n";

    /// <summary>Добавляет стандартные импорты в начало содержимого нового шаблона — идемпотентно
    /// (если systemlib уже импортирован, не дублируем).</summary>
    public static string EnsureImports(string content)
        => content.Contains($"\"{FileName}\"") ? content : Prelude + (content ?? "");
}

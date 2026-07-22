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

    /// <summary>Импорты, авто-подставляемые в НАЧАЛО шаблона при компиляции: systemlib + typeblocks.
    /// Повторный импорт идемпотентен → старые шаблоны с ручным <c>#import "typeblocks.typ"</c> не ломаются,
    /// новым импорты писать не нужно.</summary>
    public static readonly string Prelude =
        $"#import \"{FileName}\": *\n#import \"typeblocks.typ\": *\n";

    /// <summary>Число строк префикса — константный офсет для сдвига номеров строк ошибок Typst обратно
    /// на строки РЕДАКТОРА (template.typ пишется с префиксом, а автор видит шаблон без него).</summary>
    public static readonly int PreludeLineCount = Prelude.Count(c => c == '\n');

    /// <summary>Компилируемое содержимое: префикс импортов + шаблон ДОСЛОВНО (единый источник для
    /// генерации и debug-bundle — иначе отладка расходится с генерацией).</summary>
    public static string ComposeTemplate(string templateContent) => Prelude + templateContent;
}

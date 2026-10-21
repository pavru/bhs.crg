namespace BHS.CRG.Modules.Ports;

/// <summary>Строка карточки записи — поле, как его прочтёт человек.</summary>
/// <param name="Key">Ключ поля в схеме: по нему окно сверяет набранное с лежащим (issue #1517) — заголовки у
/// полей бывают одинаковые.</param>
/// <param name="Text">Значение для показа; <c>null</c> — не заполнено либо не прочитано (<paramref name="Unreadable" />).</param>
/// <param name="Kind"><c>text</c>, <c>number</c>, <c>date</c>, <c>boolean</c>, <c>choice</c> (перечисление —
/// подписью), <c>reference</c> (ссылка — названием записи), <c>hidden</c> (поле ключа с картинкой или
/// таблицей: заполнено, содержимое не показывается).</param>
/// <param name="Identity">Поле ключа идентичности: стоит первым и всегда, даже пустое.</param>
/// <param name="InheritedFrom">Название основы, от которой значение пришло; <c>null</c> — своё.</param>
/// <param name="Unreadable">Прочитать не удалось — это не «пусто».</param>
/// <param name="Archived">Ссылка ведёт на архивную запись.</param>
public sealed record ModuleCardLine(
    string Key, string Title, string? Text, string Kind, bool Identity, string? InheritedFrom, bool Unreadable, bool Archived);

/// <summary>
/// Карточка записи справочника — ПРЕДСТАВЛЕНИЕ ДЛЯ ЧЕЛОВЕКА (issue #1516): чтобы убедиться, что позиция та,
/// до выбора.
///
/// <para>⚠️ Сравнивать и сопоставлять по этим значениям нельзя: перечисление здесь подписью, ссылка —
/// названием. Значение поля для кода — <see cref="IModuleCatalog.FieldValuesAsync" />.</para>
/// </summary>
/// <param name="TypeName">Название вида записи.</param>
/// <param name="Lines">Поля по схеме вида: ключ первым и всегда, остальные простые — только заполненные.</param>
/// <param name="NotShown">Заполненные картинки, файлы, составные и табличные поля — только заголовками.</param>
/// <param name="EmptyCount">Сколько простых полей вне ключа не заполнено.</param>
/// <param name="Tagged">Строки полей, размеченных спрошенными тэгами (артикул, единица): тэг → строка. Тэга
/// нет на схеме — нет и ключа: сравнения по нему не будет.</param>
/// <param name="Scope">Уровень области, если не вся система: <c>construction</c>, <c>section</c>, <c>set</c>.</param>
public sealed record ModuleCatalogCard(
    ModuleCatalogRef Ref, string TypeName, IReadOnlyList<ModuleCardLine> Lines, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> NotShown, int EmptyCount, IReadOnlyDictionary<string, ModuleCardLine> Tagged, string? Scope);

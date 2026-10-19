namespace BHS.CRG.Modules.Ports;

/// <summary>Строка бумаги (счёта, накладной) глазами подбора: слова поставщика и его артикул.</summary>
public sealed record ModuleLineWords(string? Name, string? Code);

/// <summary>Слово строки так, как оно в ней написано.</summary>
/// <param name="Start">Где слово начинается в исходной строке (<see cref="ModuleLineWords.Name" />).</param>
/// <param name="Found">Слово есть в названии позиции — или в том альтернативном имени, которым она нашлась.</param>
public sealed record ModuleLineWord(string Text, int Start, bool Found);

/// <summary>Что совпало — для сравнения «строка ↔ позиция» в карточке.</summary>
/// <param name="Words">Слова строки по порядку. Разбивает на слова только ядро: модуль или клиент,
/// повторивший разбиение, подсветил бы не то, по чему подбирали.</param>
/// <param name="Field">Заголовок поля ключа, с которым совпал артикул строки; <c>null</c> — не совпал.</param>
public sealed record ModuleLineMatched(IReadOnlyList<ModuleLineWord> Words, string? Field);

/// <summary>Кандидат для строки.</summary>
/// <param name="Why">Причины словами ЯДРА, старшая первой, — готовые к показу: модуль их не сочиняет.</param>
/// <param name="Strong">«Лучшее совпадение»: артикул или то же наименование — и такая действующая позиция
/// одна. ⚠️ «Одна» — среди кандидатов ядра: модуль, добавляя своих (соответствия поставщика), обязан
/// снять признак, если добавленная позиция другая, — иначе у строки два «лучших».</param>
public sealed record ModuleLineCandidate(
    ModuleCatalogRef Ref, IReadOnlyList<string> Why, bool Strong, ModuleLineMatched Matched);

/// <summary>Ответ подбора на одну строку.</summary>
/// <param name="Items">Действующие кандидаты, лучший первым; архивных здесь нет.</param>
/// <param name="More">Действующих кандидатов больше, чем показано.</param>
/// <param name="InArchive">Сколько подошло АРХИВНЫХ позиций. Это выбор: не найдя позицию, человек
/// заведёт её заново, а она есть, в архиве, — кто показывает кандидатов, обязан это сказать.</param>
/// <param name="Unreadable">Сколько позиций сверить НЕ УДАЛОСЬ (основа удалена и подобное). Это не
/// «не подошли»: о них ответа нет.</param>
public sealed record ModuleLineCandidates(
    IReadOnlyList<ModuleLineCandidate> Items, bool More, int InArchive, int Unreadable);

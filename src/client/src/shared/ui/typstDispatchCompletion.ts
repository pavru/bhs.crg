import type * as Monaco from 'monaco-editor';

/**
 * Автокомплит диспетчеризации по типу — `render-by-type` и `type-renders` (issue #768).
 *
 * <p>Список СТАТИЧЕСКИЙ, в отличие от соседних провайдеров (userlib, ассеты), которые читают то,
 * что завёл пользователь. Эти два имени эмитит сервер в конец <code>typeblocks.typ</code> при каждой
 * сборке, они есть всегда и не зависят ни от схемы, ни от библиотеки — тянуть их из данных значило
 * бы придумать источник ради единообразия.</p>
 *
 * <p>Оборотная сторона: список обязан меняться вместе с эмиссией
 * (<code>TypstPreambleBuilder.AppendDispatch</code>). Ровно поэтому имена продублированы там
 * константами <code>DispatchTableName</code>/<code>DispatchFnName</code> — расхождение видно по
 * упоминанию issue с обеих сторон.</p>
 *
 * <p><b>Где именно доступно — зависит от файла.</b> Провайдер Monaco регистрируется на ЯЗЫК, а язык
 * `typst` общий у четырёх редакторов: шаблон, Typst-блоки типа, библиотека, системная библиотека.
 * Доступность же у двух имён разная, и подсказка обязана это повторять — иначе она заводит в
 * поломку, причём тихо: проверка блоков (#309, фаза 2) только ИМПОРТИРУЕТ файлы и тела не зовёт,
 * то есть осталась бы зелёной, а сломалась бы генерация.</p>
 *
 * <ul>
 *   <li><b>Шаблон</b> — оба имени: он импортирует агрегатор целиком.</li>
 *   <li><b>Блок типа</b> — только <code>render-by-type</code>. С расколом по файлам (#772) в шапку
 *   каждого модуля эмитится переходник с отложенным импортом, и блок наконец может позвать диспетч
 *   (во flat-файле это было невозможно: хелпер шёл ниже блоков). А вот таблицы
 *   <code>type-renders</code> в модуле нет — она в агрегаторе, и статический импорт оттуда дал бы
 *   <code>cyclic import</code>.</li>
 *   <li><b>Библиотека</b> — ничего: отдельный модуль, ни того, ни другого не видит.</li>
 * </ul>
 *
 * <p>Модели помечаются явно, а не различаются по URI: путь у моделей не задан, Monaco выдаёт им
 * безымянные <code>inmemory://</code>, и опереться на них нельзя.</p>
 */
const templateModels = new WeakSet<Monaco.editor.ITextModel>();
const typeBlockModels = new WeakSet<Monaco.editor.ITextModel>();

/** Пометить модель как редактор ШАБЛОНА — в нём доступно всё, что эмитит агрегатор. */
export function markTemplateModel(model: Monaco.editor.ITextModel | null): void {
  if (model) templateModels.add(model);
}

/** Пометить модель как редактор ТИПОВОГО БЛОКА — в нём доступен только сам диспетч (#772). */
export function markTypeBlockModel(model: Monaco.editor.ITextModel | null): void {
  if (model) typeBlockModels.add(model);
}

interface DispatchDef {
  insert: string; label: string; documentation: string; snippet: boolean;
  /** Доступно ли имя из тела блока, а не только из шаблона. */
  inBlocks: boolean;
}

const DEFS: DispatchDef[] = [
  {
    insert: 'render-by-type(${1:it})',
    label: 'render-by-type(obj, variant: auto)',
    snippet: true,
    inBlocks: true,
    documentation:
      'Отобразить объект его собственным Typst-блоком, не зная тип заранее.\n\n'
      + 'Идёт по `_type.chain` от фактического типа вверх и берёт первый тип, у которого блок есть, '
      + '— то есть подтип отображается блоком предка. Без `variant` берётся первый вариант типа, '
      + 'иначе — с указанным именем.\n\n'
      + 'Строка union-массива вида `{Вариант: значение}` разворачивается автоматически.\n\n'
      + 'Нет блока или варианта — в документе появится видимая пометка, а не пустое место.\n\n'
      + 'Примеры:\n'
      + '```\n#render-by-type(it)\n#render-by-type(строка, variant: "Краткое")\n```',
  },
  {
    insert: 'type-renders',
    label: 'type-renders',
    snippet: false,
    inBlocks: false,
    documentation:
      'Диспетч-таблица «код типа → варианты отображения», которую собирает сервер в конце '
      + '`typeblocks.typ`. Ключ совпадает с кодом типа в `_type.chain`, значение — массив пар '
      + '`(name, fn)` в порядке объявления вариантов.\n\n'
      + 'Обычно её не трогают напрямую — есть `render-by-type`; прямой доступ нужен, чтобы '
      + 'проверить наличие блока: `if "Организация" in type-renders { … }`.',
  },
];

let registered = false;

export function registerDispatchCompletion(monaco: typeof Monaco): void {
  if (registered) return;
  registered = true;
  monaco.languages.registerCompletionItemProvider('typst', {
    triggerCharacters: ['#'],
    provideCompletionItems(model, position) {
      const inTemplate = templateModels.has(model);
      const inBlock = typeBlockModels.has(model);
      if (!inTemplate && !inBlock) return { suggestions: [] };
      const available = inTemplate ? DEFS : DEFS.filter(d => d.inBlocks);
      const line = model.getValueInRange({
        startLineNumber: position.lineNumber, startColumn: 1,
        endLineNumber: position.lineNumber, endColumn: position.column,
      });
      // Тот же вход, что у userlib-провайдера: хвостовой идентификатор с дефисами либо свежий «#».
      const idMatch = line.match(/([\p{L}_][\p{L}\p{N}_-]*)$/u);
      const prefix = idMatch ? idMatch[1] : '';
      if (prefix.length === 0 && !/#$/.test(line)) return { suggestions: [] };

      const matches = prefix
        ? available.filter(d => d.insert.toLowerCase().startsWith(prefix.toLowerCase()))
        : available;
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - prefix.length, endColumn: position.column,
      };

      return {
        suggestions: matches.map(d => ({
          label: d.label,
          kind: d.snippet
            ? monaco.languages.CompletionItemKind.Function
            : monaco.languages.CompletionItemKind.Variable,
          insertText: d.insert,
          insertTextRules: d.snippet
            ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
            : undefined,
          range,
          detail: 'typeblocks.typ · диспетчеризация по типу',
          documentation: { value: d.documentation },
        })),
      };
    },
  });
}

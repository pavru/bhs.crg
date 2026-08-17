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
 * <p><b>Только редактор шаблона.</b> Провайдер Monaco регистрируется на ЯЗЫК, а язык `typst` общий у
 * четырёх редакторов — шаблон, Typst-блоки типа, библиотека, системная библиотека. Но хелпер эмитится
 * ПОСЛЕ всех блоков, а замыкание Typst захватывает область на месте определения: блок, позвавший
 * <code>render-by-type</code>, падает с <code>unknown variable</code> (проверено), и библиотека —
 * отдельный модуль — не видит его тоже. Подсказка там завела бы прямиком в поломку, причём тихо:
 * проверка блоков (#309, фаза 2) только ИМПОРТИРУЕТ typeblocks и тела не зовёт, то есть осталась бы
 * зелёной, а сломалась бы генерация.</p>
 *
 * <p>Поэтому модели помечаются явно, а не различаются по URI: путь у моделей не задан, Monaco выдаёт
 * им безымянные <code>inmemory://</code>, и опереться на них нельзя.</p>
 */
const templateModels = new WeakSet<Monaco.editor.ITextModel>();

/** Пометить модель как редактор ШАБЛОНА — только в нём доступны имена из typeblocks.typ. */
export function markTemplateModel(model: Monaco.editor.ITextModel | null): void {
  if (model) templateModels.add(model);
}
interface DispatchDef { insert: string; label: string; documentation: string; snippet: boolean; }

const DEFS: DispatchDef[] = [
  {
    insert: 'render-by-type(${1:it})',
    label: 'render-by-type(obj, variant: auto)',
    snippet: true,
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
      if (!templateModels.has(model)) return { suggestions: [] };
      const line = model.getValueInRange({
        startLineNumber: position.lineNumber, startColumn: 1,
        endLineNumber: position.lineNumber, endColumn: position.column,
      });
      // Тот же вход, что у userlib-провайдера: хвостовой идентификатор с дефисами либо свежий «#».
      const idMatch = line.match(/([\p{L}_][\p{L}\p{N}_-]*)$/u);
      const prefix = idMatch ? idMatch[1] : '';
      if (prefix.length === 0 && !/#$/.test(line)) return { suggestions: [] };

      const matches = prefix
        ? DEFS.filter(d => d.insert.toLowerCase().startsWith(prefix.toLowerCase()))
        : DEFS;
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

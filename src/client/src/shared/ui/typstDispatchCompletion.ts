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
 */
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

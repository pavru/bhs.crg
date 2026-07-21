import type * as Monaco from 'monaco-editor';
import { useEffect } from 'react';
import { useTypstUserLib } from '@/shared/api/typstUserLib';

/**
 * Автокомплит функций/значений из общей библиотеки Typst (`userlib.typ`) в Monaco-редакторах
 * (шаблоны, Typst-блоки типов, сам userlib). Зеркалит паттерн `it.`-провайдера: модульный список
 * обновляется хуком из загруженного userlib, провайдер регистрируется один раз и читает его лениво.
 */
interface UserLibDef { name: string; params: string; isFunction: boolean; }

let _defs: UserLibDef[] = [];
let _registered = false;

/** Парсит `#let name(params) = …` (функция) и `#let name = …` (значение) из содержимого userlib.typ. */
export function setUserLibDefs(content: string): void {
  const defs: UserLibDef[] = [];
  const seen = new Set<string>();
  // Имя — Typst-идентификатор (буквы/цифры/_/-), опциональные (params) → функция.
  const re = /#let\s+([\p{L}_][\p{L}\p{N}_-]*)\s*(\(([^)]*)\))?/gu;
  let m: RegExpExecArray | null;
  while ((m = re.exec(content)) !== null) {
    if (seen.has(m[1])) continue;
    seen.add(m[1]);
    defs.push({ name: m[1], params: m[3] ?? '', isFunction: m[2] != null });
  }
  _defs = defs;
}

/** Регистрирует провайдер автокомплита userlib для языка typst (один раз глобально). */
export function registerUserLibCompletion(monaco: typeof Monaco): void {
  if (_registered) return;
  _registered = true;
  monaco.languages.registerCompletionItemProvider('typst', {
    triggerCharacters: ['#'],
    provideCompletionItems(model, position) {
      if (_defs.length === 0) return { suggestions: [] };
      const line = model.getValueInRange({
        startLineNumber: position.lineNumber, startColumn: 1,
        endLineNumber: position.lineNumber, endColumn: position.column,
      });
      // Хвостовой идентификатор (с дефисами) — префикс для фильтра; либо только что напечатан «#».
      const idMatch = line.match(/([\p{L}_][\p{L}\p{N}_-]*)$/u);
      const prefix = idMatch ? idMatch[1] : '';
      const afterHash = /#$/.test(line);
      if (prefix.length === 0 && !afterHash) return { suggestions: [] };

      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - prefix.length, endColumn: position.column,
      };
      const matches = prefix
        ? _defs.filter(d => d.name.toLowerCase().startsWith(prefix.toLowerCase()))
        : _defs;

      return {
        suggestions: matches.map(d => ({
          label: d.isFunction ? `${d.name}(${d.params})` : d.name,
          kind: d.isFunction
            ? monaco.languages.CompletionItemKind.Function
            : monaco.languages.CompletionItemKind.Variable,
          insertText: d.isFunction ? `${d.name}($0)` : d.name,
          insertTextRules: d.isFunction
            ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
            : undefined,
          range,
          detail: 'userlib.typ',
          documentation: d.isFunction ? `#let ${d.name}(${d.params}) = …` : `#let ${d.name} = …`,
        })),
      };
    },
  });
}

/** Хук: подтягивает userlib.typ и обновляет список автокомплита. Вызывать в компонентах-хостах Typst-редакторов. */
export function useUserLibCompletion(): void {
  const { data } = useTypstUserLib();
  useEffect(() => { setUserLibDefs(data ?? ''); }, [data]);
}

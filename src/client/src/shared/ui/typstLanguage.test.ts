import { describe, it, expect } from 'vitest';
import type * as Monaco from 'monaco-editor';
import { registerTypstLanguage } from './typstLanguage';

/**
 * Определение языка Monarch компилируется в рантайме, поэтому ошибка в нём не видна ни
 * компилятору, ни сборке — она обрушивает токенизатор целиком уже в браузере, и подсветка
 * пропадает во всех редакторах Typst сразу (issue #690).
 *
 * Ловим тот класс ошибок, на который наступили: ключ ветки `cases` — это охранное выражение,
 * и Monarch разбирает его, только когда оно начинается с `$` (ссылка на группу) или `@`
 * (проверка по множеству). Ключ с любым другим началом он принимает за строку и раскрывает
 * в ней `@имя`, а если за именем стоит массив — падает.
 */
describe('определение языка typst', () => {
  const lexer = captureMonarchDefinition();

  it('ключи ветвей cases — охранные выражения, а не строки', () => {
    const bad: string[] = [];
    for (const [state, rules] of Object.entries(lexer.tokenizer)) {
      for (const rule of rules as unknown[]) {
        const action = Array.isArray(rule) ? rule[1] : (rule as { action?: unknown }).action;
        const cases = (action as { cases?: Record<string, unknown> } | undefined)?.cases;
        for (const key of Object.keys(cases ?? {})) {
          if (!/^[$@]/.test(key)) bad.push(`${state}: «${key}»`);
        }
      }
    }
    expect(bad).toEqual([]);
  });

  it('ключевые слова отделены от имён функций', () => {
    const rule = lexer.tokenizer.root.find(
      (r): r is [RegExp, { cases: Record<string, string> }] =>
        Array.isArray(r) && r[0] instanceof RegExp && r[0].source.startsWith('#'),
    );
    expect(rule).toBeDefined();
    // Ветка ключевых слов проверяет захваченное имя (без решётки) по множеству keywords,
    // всё прочее после решётки — вызов функции.
    expect(rule![1].cases['$1@keywords']).toBe('keyword.control');
    expect(rule![1].cases['@default']).toBe('entity.name.function');
    expect(lexer.keywords).toContain('let');
  });
});

/** Подсовывает `registerTypstLanguage` заглушку Monaco и возвращает переданное ей определение. */
function captureMonarchDefinition() {
  let captured: Monaco.languages.IMonarchLanguage | undefined;
  const fake = {
    languages: {
      register: () => {},
      setMonarchTokensProvider: (_id: string, def: Monaco.languages.IMonarchLanguage) => { captured = def; },
      setLanguageConfiguration: () => {},
      registerCompletionItemProvider: () => {},
      CompletionItemKind: { Function: 1, File: 2, Text: 3 },
      CompletionItemInsertTextRule: { InsertAsSnippet: 4 },
    },
  } as unknown as typeof Monaco;

  registerTypstLanguage(fake);
  if (!captured) throw new Error('определение Monarch не зарегистрировано');
  return captured as {
    keywords: string[];
    tokenizer: Record<string, unknown[]> & { root: unknown[] };
  };
}

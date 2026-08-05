import { describe, it, expect } from 'vitest';
import type * as Monaco from 'monaco-editor';
// Внутренности Monaco, а не публичный API: публичный `editor.tokenize` тянет за собой браузер,
// а нам нужен ровно компилятор Monarch. Путь может съехать при обновлении Monaco — тогда тест
// не пройдёт сборку, и это правильнее, чем тихо перестать проверять.
import { compile } from 'monaco-editor/editor/standalone/common/monarch/monarchCompile.js';
import { registerTypstLanguage } from './typstLanguage';

/**
 * Определение Monarch компилируется в рантайме, поэтому ошибка в нём не видна ни компилятору,
 * ни сборке — она обрушивает токенизатор целиком уже в браузере, и подсветка пропадает сразу
 * во всех редакторах Typst (issue #690).
 *
 * Проверяем настоящим компилятором Monarch, а не глазами: часть ошибок он ловит при компиляции,
 * а часть — ту самую, на которой обожглись, — откладывает до момента, когда ветка `cases`
 * действительно вычисляется. Поэтому мало скомпилировать определение, надо ещё прогнать по нему
 * образцы кода.
 */
describe('определение языка typst', () => {
  const definition = captureMonarchDefinition();

  it('компилируется', () => {
    expect(() => compileTypst()).not.toThrow();
  });

  it('ключевые слова отличаются от вызовов функций', () => {
    // Именно здесь падал прежний ключ ветки: проверку «$1 входит в keywords» Monarch принимал
    // за строку с подстановкой @keywords, а keywords — массив.
    expect(decide('#let')).toBe('keyword.control');
    expect(decide('#show')).toBe('keyword.control');
    expect(decide('#mycall')).toBe('entity.name.function');
  });

  it('ни одна ветка не падает на образцах разметки', () => {
    const samples = [
      '#let x = 1', '#mycall(2)', '// комментарий', '/* блок */', '$ x^2 $',
      '"строка"', '12pt', '3.5', '*жирный*', '_курсив_', '{}', '[]', '()',
      'идентификатор', 'identifier-with-dash', '+', '-', '==', ',', ':', ';',
    ];
    const lexer = compileTypst();
    for (const [state, rules] of Object.entries(lexer.tokenizer)) {
      for (const rule of rules) {
        const test = rule.action?.test;
        if (typeof test !== 'function') continue;   // у правила нет ветвления — проверять нечего
        for (const sample of samples) {
          const m = sample.match(rule.regex);
          if (m) expect(() => test(m[0], m, state, false)).not.toThrow();
        }
      }
    }
  });

  /** Компилирует определение и возвращает лексер в том виде, в каком его читает токенизатор. */
  function compileTypst() {
    return compile('typst', definition as never) as unknown as {
      tokenizer: Record<string, { regex: RegExp; action?: { test?: (id: string, matches: string[], state: string, eos: boolean) => string } }[]>;
    };
  }

  /** Прогоняет образец через правило `#имя` и возвращает выбранный им токен. */
  function decide(sample: string) {
    const lexer = compileTypst();
    const rule = lexer.tokenizer.root.find(r => r.regex.test(sample) && r.action?.test);
    if (!rule) throw new Error(`ни одно правило с ветвлением не разобрало «${sample}»`);
    const m = sample.match(rule.regex)!;
    return rule.action!.test!(m[0], m, 'root', false);
  }
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
  return captured;
}

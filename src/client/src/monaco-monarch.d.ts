/**
 * Компилятор Monarch у Monaco не в публичном API и без типов, но он единственный способ
 * проверить определение языка без браузера (см. shared/ui/typstLanguage.test.ts).
 * Объявляем ровно то, чем пользуемся.
 */
declare module 'monaco-editor/editor/standalone/common/monarch/monarchCompile.js' {
  /** Компилирует определение Monarch; на ошибке в определении бросает. */
  export function compile(languageId: string, json: unknown): unknown;
}

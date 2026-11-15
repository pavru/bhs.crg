import { describe, expect, it } from 'vitest';

/**
 * Каждое окно — слой истории (issue #1418): на телефоне системная «Назад» закрывает его, а не уводит
 * со страницы под ним. Окна, собранные на `Modal`, получают это от каркаса; окно на голом
 * `Dialog.Root` обязано назвать свой выход само — `useHistoryLayer`. Иначе «Назад» на нём уводит со
 * страницы, а окно уходит вместе с ней, и с ним — набранное.
 *
 * Сторож читает исходники: своего `popstate` окну заводить нельзя (docs/UI.md), а забытый слой
 * поведением на рабочем столе не виден вовсе — там слоёв нет.
 */

const sources = import.meta.glob<string>(['/src/**/*.tsx', '!/src/**/*.test.*'], { query: '?raw', import: 'default', eager: true });

/**
 * Вопрос о несохранённом лежит над окном `Modal` и своего слоя не заводит: «Назад» при вопросе решает окно
 * под ним — «Продолжить редактирование», как Esc (sharedui-md3-13, [UI-75]).
 */
const OVER_WINDOW = ['/src/shared/ui/LeaveQuestion.tsx'];

/**
 * Поповер — слой истории ([UI-96]): `Root` обёртки `shared/ui/Popover` сам кладёт запись, и пакет
 * `@radix-ui/react-popover` берёт только она. Иначе забытый поповер на телефоне отдавал бы «Назад»
 * окну или странице под ним — дефект #1522 в каждом новом месте.
 */
const POPOVER_OWNER = '/src/shared/ui/Popover.tsx';

/**
 * Части поля, открываемые набором, — не слои: первая «Назад» на Android убирает клавиатуру, и список
 * уходит вместе с фокусом, а запись на каждое открытие гонялась бы с вводом. Это список подсказки
 * `SuggestField` (docs/UI.md, «Назад на телефоне»).
 */
const FIELD_PARTS = ['/src/shared/ui/SuggestBox.tsx'];

const POPOVER_PACKAGE = String.raw`['"]@radix-ui/react-popover['"]`;
/** Импорт, реэкспорт (`export … from`) и `import()` пакета поповера. */
const TAKES_POPOVER = new RegExp(String.raw`\bfrom\s*${POPOVER_PACKAGE}|\bimport\s*${POPOVER_PACKAGE}|\bimport\(\s*${POPOVER_PACKAGE}\s*\)`);

/** Файлы, которые берут пакет поповера мимо обёртки и названных частей поля. */
export function popoverBypasses(sources: Record<string, string>): string[] {
  return Object.entries(sources)
    .filter(([file, text]) => TAKES_POPOVER.test(text) && file !== POPOVER_OWNER && !FIELD_PARTS.includes(file))
    .map(([file]) => file);
}

describe('окна и «Назад» на телефоне', () => {
  it('окно на голом Dialog.Root называет свой выход для «Назад»', () => {
    const missing = Object.entries(sources)
      .filter(([file, text]) => /<Dialog\.Root\b/.test(text) && !text.includes('useHistoryLayer(') && !OVER_WINDOW.includes(file))
      .map(([file]) => file);
    expect(missing).toEqual([]);
  });

  it('своего popstate никто, кроме охраны и слоёв, не заводит', () => {
    const all = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.*'], { query: '?raw', import: 'default', eager: true });
    const own = Object.entries(all)
      .filter(([, text]) => /addEventListener\(\s*'popstate'/.test(text))
      .map(([file]) => file);
    expect(own).toEqual(['/src/shared/ui/NavigationGuard.ts']);
  });

  it('пакет поповера берут только обёртка shared/ui/Popover и список подсказки', () => {
    const all = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.*'], { query: '?raw', import: 'default', eager: true });
    expect(popoverBypasses(all)).toEqual([]);
  });

  it('самопроверка: ловит прямой импорт, реэкспорт в .ts и import()', () => {
    expect(popoverBypasses({
      '/src/a.tsx': "import * as Popover from '@radix-ui/react-popover';",
      '/src/b.ts': 'export * from "@radix-ui/react-popover";',
      '/src/c.ts': "export { Root } from '@radix-ui/react-popover';",
      '/src/d.tsx': "const P = await import('@radix-ui/react-popover');",
      '/src/e.ts': "import '@radix-ui/react-popover';",
      // Обёртка и её потребители — не нарушение.
      '/src/f.tsx': "import * as Popover from '@/shared/ui/Popover';",
      [POPOVER_OWNER]: "import * as P from '@radix-ui/react-popover';",
      [FIELD_PARTS[0]]: "import * as Popover from '@radix-ui/react-popover';",
    })).toEqual(['/src/a.tsx', '/src/b.ts', '/src/c.ts', '/src/d.tsx', '/src/e.ts']);
  });
});

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
});

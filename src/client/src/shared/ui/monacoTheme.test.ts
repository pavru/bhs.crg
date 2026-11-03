// @vitest-environment jsdom
// Типов Node у приложения нет нарочно (tsconfig.app) — как в pdfEngine.test.ts, без типов, здесь.
// @ts-expect-error — модуль Node без типов
import { readFileSync } from 'node:fs';
// @ts-expect-error — модуль Node без типов
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installBhsTheme, MONACO_THEME, readRole, ROLE } from './monacoTheme';

// Стили палитры — файлом: CSS-импорт vitest отдаёт пустым, а адрес модуля в jsdom — не путь файла.
// Корень прогона vitest — каталог клиента.
const tokens = readFileSync(path.resolve('src/theme/md3-tokens.css'), 'utf8');

/**
 * Тема редактора кода следует теме приложения без перезагрузки ([UI-80]): атрибут `data-theme` сменился —
 * тема `bhs` переопределена ролями новой темы. Значения ролей против генератора палитры сверяет
 * `scripts/monaco-theme.test.mjs`; здесь — что берутся вычисленные стили документа и в нужный момент.
 */

let style: HTMLStyleElement;
beforeEach(() => {
  style = document.createElement('style');
  style.textContent = tokens;
  document.head.append(style);
  document.documentElement.dataset.theme = 'light';
});
afterEach(() => {
  style.remove();
  delete document.documentElement.dataset.theme;
});

/** MutationObserver отвечает микрозадачей. */
const settle = () => new Promise(r => setTimeout(r, 0));

describe('тема редактора кода', () => {
  it('определяется сразу, ролями текущей темы, и переопределяется при смене темы', async () => {
    const defineTheme = vi.fn();
    const stop = installBhsTheme({ defineTheme });
    expect(defineTheme).toHaveBeenCalledTimes(1);
    const [name, light] = defineTheme.mock.calls[0];
    expect(name).toBe(MONACO_THEME);
    expect(light.base).toBe('vs');
    expect(light.colors['editor.background']).toBe(readRole(ROLE.surface));

    document.documentElement.dataset.theme = 'dark';
    await settle();
    expect(defineTheme).toHaveBeenCalledTimes(2);
    const dark = defineTheme.mock.calls[1][1];
    expect(dark.base).toBe('vs-dark');
    expect(dark.colors['editor.background']).toBe(readRole(ROLE.surface));
    expect(dark.colors['editor.background']).not.toBe(light.colors['editor.background']);

    stop();
    document.documentElement.dataset.theme = 'light';
    await settle();
    expect(defineTheme).toHaveBeenCalledTimes(2);
  });

  it('без стилей палитры — громкий отказ, а не молча красный редактор', () => {
    style.remove();
    expect(() => installBhsTheme({ defineTheme: vi.fn() })).toThrow(/--md-sys-color-/);
  });
});

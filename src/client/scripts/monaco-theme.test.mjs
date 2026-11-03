import { describe, expect, it } from 'vitest';
import { readTheme, resolve } from './md3-theme.mjs';
import { contrast } from './md3-contrast.mjs';
import { bhsTheme } from '../src/shared/ui/monacoTheme.ts';

// Тема редактора кода против генератора палитры ([UI-80], макет А md3-16): фон — роль surface в обеих
// темах, подсветка — системными ролями и читается на фоне. Значения — из генерации, а не из тех же
// вычисленных стилей, которыми тема пользуется: иначе проверка сравнивала бы значение само с собой.
const palette = resolve(readTheme());

describe.each([['light', false], ['dark', true]])('тема редактора, %s', (mode, dark) => {
  const roles = palette.roles[mode];
  const role = variable => {
    const name = variable.replace(/^--md-sys-color-/, '');
    if (!roles[name]) throw new Error(`роли ${name} в палитре нет`);
    return roles[name];
  };
  const theme = bhsTheme(role, dark);

  it('фон и полоса номеров — роль surface, текущая строка — surface-container', () => {
    expect(theme.colors['editor.background']).toBe(roles.surface);
    expect(theme.colors['editorGutter.background']).toBe(roles.surface);
    expect(theme.colors['editor.lineHighlightBackground']).toBe(roles['surface-container']);
    expect(theme.base).toBe(dark ? 'vs-dark' : 'vs');
  });

  it('цвета токенов читаются на фоне редактора (≥ 4,5:1)', () => {
    for (const rule of theme.rules.filter(r => r.foreground)) {
      const ratio = contrast(`#${rule.foreground}`, roles.surface);
      expect(ratio, `${rule.token || 'текст'}: ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5);
    }
  });
});

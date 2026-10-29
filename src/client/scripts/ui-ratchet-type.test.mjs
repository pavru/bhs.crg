import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Храповик интерфейса, роли текста (sharedui-md3-07, [UI-67]): правило legacy-type и подсказка
// text-size. Отдельно от ui-ratchet.test.mjs: там фикстуры прочих правил пишут ступени Tailwind, и счёт
// тех правил смотрит без этого.
const screen = { dir: 'features', file: 'features/x.tsx' };
const own = text => countViolations(text, screen)['legacy-type'] ?? 0;

describe('храповик интерфейса: роли текста (sharedui-md3-07)', () => {
  it('ступень, своя ступень, начертание вне шкалы, капс и разрядка — нарушение', () => {
    // Граница к прогону: правило обязано срабатывать на каждом виде, который оно называет.
    expect(own('<p className="text-sm font-semibold uppercase tracking-wide" />')).toBe(4);
    expect(own('<p className="md:text-xs text-caption text-note text-base text-lg text-2xl text-sm/6 font-bold tracking-[0.08em]" />'))
      .toBe(9);
  });

  it('роль MD3, font-medium, цвет текста и tracking-normal — не нарушение', () => {
    expect(own('<p className="text-body-medium font-medium text-fg1 text-on-surface-variant tracking-normal text-left text-label-small" />'))
      .toBe(0);
  });

  it('класс в комментарии не рисуется и не считается; смотрит весь src/, shared/ui тоже', () => {
    expect(own('// раньше здесь был text-xs uppercase\n<p className="text-body-small" />')).toBe(0);
    expect(countViolations('<b className="font-semibold" />', { dir: 'shared', file: 'shared/ui/Modal.tsx' }))
      .toEqual({ 'legacy-type': 1 });
  });

  it('подсказки называют роли, а не прежние ступени', () => {
    for (const id of ['legacy-type', 'text-size']) {
      const { instead } = RULES.find(r => r.id === id);
      expect(instead).toContain('text-body-medium');
      expect(instead).not.toMatch(/\btext-(?:xs|sm|caption|note)\b/);
    }
  });
});

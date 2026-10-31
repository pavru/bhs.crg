import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Правило segmented и подсказки chip / icon-button ([UI-68], sharedui-md3-10 PR 2). Отдельно от
// ui-ratchet.test.mjs — тот у порога FileSizeRatchet. Счёт — только своих правил.
const only = (rule, text, opts) => Object.fromEntries(Object.entries(countViolations(text, opts)).filter(([r]) => r === rule));

describe('храповик интерфейса: segmented', () => {
  // Образец — ThemeToggle до перевода: группа в рамке со скруглением, кнопки с aria-pressed внутри.
  const THEME_TOGGLE = `
    <div role="group" aria-label={label} className="flex h-10 rounded-full border border-outline overflow-hidden">
      {OPTIONS.map(o => (
        <button type="button" key={o.value} aria-pressed={o.value === theme} className="flex-1 border-l">{o.label}</button>
      ))}
    </div>`;

  it('свой ряд сегментов — нарушение, и в shared/ui тоже', () => {
    expect(only('segmented', THEME_TOGGLE, { dir: 'shared', file: 'shared/ui/ThemeToggle.tsx' })).toEqual({ segmented: 1 });
    expect(only('segmented', THEME_TOGGLE.replace('role="group"', 'role="radiogroup"'), { dir: 'features', file: 'features/x/X.tsx' }))
      .toEqual({ segmented: 1 });
  });

  it('группа без рамки, рамка без группы и группа без кнопок — не ряд сегментов', () => {
    const opts = { dir: 'features', file: 'features/x/X.tsx' };
    expect(only('segmented', '<div role="group" className="flex gap-2"><button type="button">a</button></div>', opts)).toEqual({});
    expect(only('segmented', '<div className="rounded-md border"><button type="button">a</button></div>', opts)).toEqual({});
    expect(only('segmented', '<div role="group" className="rounded-md border"><span>a</span></div>', opts)).toEqual({});
  });

  it('подсказки называют виды чипа, SegmentedButton и Icon', () => {
    const chip = RULES.find(r => r.id === 'chip').instead;
    for (const word of ['filter', 'input', 'assist', 'SegmentedButton', 'iconTone']) expect(chip).toContain(word);
    expect(chip).not.toMatch(/\btone\b,/);
    expect(RULES.find(r => r.id === 'segmented').instead).toContain('SegmentedButton');
    expect(RULES.find(r => r.id === 'icon-button').instead).toContain('знак Icon');
  });
});

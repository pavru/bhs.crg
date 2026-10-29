import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { loadDesign } from './color-classes-lib.mjs';
import { themeDir } from './md3-theme.mjs';

// Сторож утилит состояния MD3 (sharedui-md3-05, [UI-64] [UI-65]): тем же движком Tailwind и тем же
// index.css, что собирает vite. Проверяется CSS, который получит браузер, а не текст index.css: опечатка
// в селекторе или в имени токена дала бы утилиту, которая молча ничего не рисует.
const srcRoot = join(themeDir, '..');
const read = file => readFileSync(file, 'utf8').replaceAll('\r\n', '\n');
const indexCss = read(join(srcRoot, 'index.css'));
const tokens = read(join(themeDir, 'md3-tokens.css'));

let css;
beforeAll(async () => {
  const design = await loadDesign(indexCss, srcRoot);
  css = cls => design.candidatesToCss([cls])[0];
});

/** Тело вложенного правила утилиты с данным списком селекторов: `&:hover, &[data-highlighted]`. */
const rule = (text, selectors) => {
  const at = text.indexOf(`${selectors} {`);
  return at < 0 ? null : text.slice(at + selectors.length + 2, text.indexOf('}', at)).replace(/\s+/g, ' ').trim();
};

describe('state-layer', () => {
  it('слой — цвет содержимого поверх заливки, картинкой фона', () => {
    const text = css('state-layer');
    expect(text).toContain('background-image: linear-gradient(color-mix(in srgb, var(--_state-color, currentColor) calc(var(--_s) * 100%), transparent) 0 0);');
    // Без псевдоэлемента и без position: они ломали бы абсолютно позиционированное (D6).
    expect(text).not.toMatch(/::?before|position:/);
  });

  it.each([
    ['&:hover, &[data-highlighted]', '--_s: var(--md-sys-state-hover);'],
    // Нажатие — только своё: :active зажигается у предков нажатой кнопки, и строка темнела бы от крестика.
    ['&:focus-visible, &:active:not(:has(:is(button, a, input, select, textarea, label, [role=button], [tabindex]):active)), &[data-state=open]', '--_s: var(--md-sys-state-pressed);'],
    ['&[data-dragging]', '--_s: var(--md-sys-state-dragged);'],
    ['&:disabled, &[aria-disabled=true], &[data-disabled]', '--_s: 0;'],
  ])('%s → %s', (selectors, body) => {
    expect(rule(css('state-layer'), selectors)).toBe(body);
  });

  it('отключённое — последним: под курсором у него слоя нет', () => {
    const text = css('state-layer');
    expect(text.indexOf('&:disabled')).toBeGreaterThan(text.indexOf('&:hover'));
    expect(text.indexOf('&:disabled')).toBeGreaterThan(text.indexOf('&:focus-visible'));
  });

  it('токены прозрачности, на которые ссылается слой, есть в теме', () => {
    for (const name of new Set(css('state-layer').match(/--md-sys-[\w-]+/g)))
      expect(tokens, `${name} нет в src/theme/md3-tokens.css`).toContain(`${name}:`);
  });

  it('прозрачность не наследуется: слой родителя не зажигает слой вложенного', () => {
    expect(indexCss).toMatch(/@property --_s \{[^}]*syntax: '<number>';[^}]*inherits: false;[^}]*initial-value: 0;/);
  });

  it('появляется переходом с кривой standard', () => {
    const text = css('state-layer');
    expect(text).toContain('transition-property: --_s;');
    expect(text).toContain('transition-timing-function: var(--md-sys-motion-easing-standard);');
  });

  it('state-layer-group — добавка к слою: только состояния ближайшей .group, своего слоя нет', () => {
    const text = css('state-layer-group');
    expect(rule(text, '&:is(:where(.group):hover *)')).toBe('--_s: var(--md-sys-state-hover);');
    expect(text).not.toContain('background-image');
  });
});

describe('кольцо фокуса', () => {
  it('значения — один раз: 3 px secondary, отступ 2 px, внутрь — на всю толщину', () => {
    const root = indexCss.match(/:root \{([^}]*--md-focus-ring:[^}]*)\}/)?.[1].replace(/\s+/g, ' ');
    expect(root).toContain('--md-focus-ring: 3px solid var(--md-sys-color-secondary);');
    expect(root).toContain('--md-focus-ring-offset: 2px;');
    expect(root).toContain('--md-focus-ring-inset: -3px;');
  });

  it('focus-ring — только с клавиатуры', () => {
    expect(rule(css('focus-ring'), '&:focus-visible')).toBe('outline: var(--md-focus-ring); outline-offset: var(--md-focus-ring-offset);');
    expect(css('focus-ring')).not.toMatch(/&:focus[ ,{]/);
  });

  it('focus-ring-inset — то же кольцо внутрь границы', () => {
    expect(rule(css('focus-ring-inset'), '&:focus-visible')).toBe('outline: var(--md-focus-ring); outline-offset: var(--md-focus-ring-inset);');
  });

  it('focus-ring-mark — то же кольцо для варианта группы', () => {
    expect(css('group-focus-visible:focus-ring-mark')).toContain(':where(.group):focus-visible');
    expect(css('focus-ring-mark')).toContain('outline: var(--md-focus-ring);');
  });
});

describe('отключённое', () => {
  // !important — отключённое сильнее наведения элемента: hover:text-… стоит в CSS позже и весит столько же.
  const content = 'color: color-mix(in srgb, var(--md-sys-color-on-surface) 38%, transparent) !important;';
  const container = 'background-color: color-mix(in srgb, var(--md-sys-color-on-surface) 12%, transparent) !important;';

  const off = cls => rule(css(cls), '&:disabled, &[aria-disabled=true], &[data-disabled]');

  it.each(['disabled-content', 'disabled-outlined', 'disabled-container'])('%s — одним видом при disabled, aria-disabled и data-disabled', cls => {
    expect(off(cls)).toContain(content);
  });

  it('рамку красит только disabled-outlined: у прозрачной рамки серой не появится', () => {
    expect(off('disabled-outlined')).toContain('border-color: color-mix(in srgb, var(--md-sys-color-on-surface) 12%, transparent) !important;');
    expect(off('disabled-content')).not.toContain('border-color');
  });

  it('у контейнера — on-surface 12 % и без тени, у содержимого — без заливки', () => {
    const filled = rule(css('disabled-container'), '&:disabled, &[aria-disabled=true], &[data-disabled]');
    expect(filled).toContain(container);
    expect(filled).toContain('box-shadow: none !important;');
    expect(rule(css('disabled-content'), '&:disabled, &[aria-disabled=true], &[data-disabled]')).not.toContain('background-color');
  });
});

describe('движение', () => {
  it('глобального перехода на всех узлах нет', () => {
    expect(indexCss).not.toMatch(/^\*,\s*\*::before,\s*\*::after\s*\{\s*transition:/m);
    expect(indexCss).not.toContain('.no-transition');
  });

  it('уменьшенное движение гасит переходы у всех, индикатор хода остаётся', () => {
    const reduced = [...indexCss.matchAll(/@media \(prefers-reduced-motion: reduce\) \{([\s\S]*?)\n\}/g)].map(m => m[1]).join('\n');
    expect(reduced).toMatch(/\*, \*::before, \*::after \{[^}]*transition-duration: 1ms !important;/);
    expect(reduced).toMatch(/\.md-progress \{[^}]*animation-iteration-count: infinite !important;/);
  });
});

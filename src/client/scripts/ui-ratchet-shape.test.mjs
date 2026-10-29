import { describe, expect, it } from 'vitest';
import { countViolations as countAll, RULES } from './ui-ratchet-lib.mjs';

// Храповик интерфейса, форма и возвышение (sharedui-md3-06, [UI-66]): правила legacy-radius и legacy-shadow
// и форма чипа MD3 у правил chip и badge. Отдельно от ui-ratchet.test.mjs: там прочие правила, и их
// фикстуры пишут старые радиусы — счёт тех правил смотрит без этих двух.
const SHAPE = new Set(['legacy-radius', 'legacy-shadow']);
const others = (text, opts) =>
  Object.fromEntries(Object.entries(countAll(text, opts)).filter(([rule]) => !SHAPE.has(rule)));

describe('храповик интерфейса: форма чипа MD3 у chip и badge', () => {
  it('свой чип в форме чипа MD3 — rounded-sm с рамкой (sharedui-md3-06, [UI-66])', () => {
    // Граница к прогону: при PILL только с rounded-full этот тест краснеет — правило обязано срабатывать
    // на форме, которую дал переход.
    expect(others('<button className="rounded-sm px-3 border">Без раздела</button>')).toEqual({ chip: 1 });
    // 8 px — и у самодельной кнопки, и у пункта списка: это не чипы, совет «Chip» увёл бы не туда.
    expect(others([
      '<button className="px-4 py-2 rounded-sm text-sm font-medium text-on-primary bg-primary">Сохранить</button>',
      '<button className="w-full flex px-3 py-2 text-left rounded-sm border border-outline-variant">Пункт</button>',
    ].join(' '))).toEqual({});
  });

  it('своя пометка в форме чипа — rounded-sm с мелким текстом и полями по бокам; плашка и рамка — нет', () => {
    expect(others('<span className="text-xs px-2 py-0.5 rounded-sm bg-surface-container">черновик</span>'))
      .toEqual({ badge: 1 });
    expect(others([
      '<div className="rounded-sm bg-warning-container px-3 py-2 text-xs">Сообщение</div>',
      '<div className="rounded-sm border border-outline-variant overflow-hidden text-xs">строки</div>',
    ].join(' '))).toEqual({});
  });
});

describe('храповик интерфейса: форма и возвышение (sharedui-md3-06)', () => {
  const screen = { dir: 'features', file: 'features/x.tsx' };

  it('радиус и тень вне шкалы — классом и в стиле, по всему src/', () => {
    expect(countAll([
      '<div className="rounded-2xl p-4 shadow-lg" />',
      '<div className="rounded rounded-t-[20px]" style={{ boxShadow: "0 1px 2px #000", borderRadius: 6 }} />',
      '<i className="md:rounded-3xl shadow-[0_0_0_1px_red] shadow" />',
    ].join('\n'), screen)).toEqual({ 'legacy-radius': 5, 'legacy-shadow': 4 });
    expect(countAll('<div className="rounded-3xl" />', { dir: 'shared', file: 'shared/ui/Modal.tsx' }))
      .toEqual({ 'legacy-radius': 1 });
  });

  it('тень фильтром и свои значения в файле стилей — тоже нарушение; уровень, none и внутренняя тень — нет', () => {
    expect(countAll('<div className="drop-shadow-lg hover:drop-shadow inset-shadow-sm" />', screen))
      .toEqual({ 'legacy-shadow': 2 });
    const css = { dir: 'shared', file: 'shared/ui/x.css' };
    expect(countAll('.a { box-shadow: 0 8px 24px rgba(0,0,0,.3); border-radius: 6px; }\n.b { border-top-left-radius: 10px }', css))
      .toEqual({ 'legacy-radius': 2, 'legacy-shadow': 1 });
    expect(countAll([
      '.a { box-shadow: var(--md-sys-elevation-level2); border-radius: var(--md-sys-shape-corner-medium); }',
      '.b { box-shadow: none; border-radius: 0; }',
      '@utility drop-mark-before { box-shadow: inset 0 2px 0 0 var(--color-primary); }',
    ].join('\n'), css)).toEqual({});
  });

  it('ступени шкалы и уровни — не нарушение', () => {
    expect(countAll([
      '<div className="rounded-xs rounded-sm rounded-md rounded-lg rounded-xl rounded-full rounded-none rounded-t-xl md:rounded-b-sm" />',
      '<div className="shadow-elevation-1 hover:shadow-elevation-2 shadow-none drop-mark-before" />',
      '// раньше: rounded-2xl и boxShadow',
    ].join('\n'), screen)).toEqual({});
  });

  it('совет называет ступени, уровни и раздел docs/UI.md', () => {
    const radius = RULES.find(r => r.id === 'legacy-radius').instead;
    const shadow = RULES.find(r => r.id === 'legacy-shadow').instead;
    for (const step of ['rounded-xs 4', 'rounded-sm 8', 'rounded-md 12', 'rounded-lg 16', 'rounded-xl 28']) expect(radius).toContain(step);
    expect(shadow).toContain('shadow-elevation-2 (меню');
    for (const text of [radius, shadow]) expect(text).toContain('«Форма и возвышение»');
  });
});

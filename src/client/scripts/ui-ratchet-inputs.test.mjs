import { describe, expect, it } from 'vitest';
import { countViolations as countAll } from './ui-ratchet-lib.mjs';

// Правила native-radio и raw-field ([UI-69], sharedui-md3-11). Отдельно от ui-ratchet.test.mjs — тот у
// порога FileSizeRatchet. Счёт — только этих правил: фикстуры не обязаны быть чистыми для прочих.
const only = (...rules) => text => Object.fromEntries(Object.entries(countAll(text)).filter(([rule]) => rules.includes(rule)));
const radios = only('native-radio');
const fields = only('raw-field');

describe('храповик интерфейса: native-radio', () => {
  it('нативная радиокнопка — красная, и в многострочном теге со стрелкой перед type', () => {
    expect(radios('<input type="radio" name="scope" checked={a} />')).toEqual({ 'native-radio': 1 });
    expect(radios([
      '<input name="mode" checked={!byRowType}',
      '  onChange={() => switchMode(false)}',
      "  type={'radio'} />",
    ].join('\n'))).toEqual({ 'native-radio': 1 });
  });

  it('свой role="radio" — тоже красный', () => {
    expect(radios('<button type="button" role="radio" aria-checked={on} onClick={pick}>Русский</button>')).toEqual({ 'native-radio': 1 });
    expect(radios('<div role={"radio"} aria-checked="false">а</div>')).toEqual({ 'native-radio': 1 });
  });

  it('RadioGroup и SegmentedButton — зелёные; флажок и radiogroup — не радиокнопка', () => {
    expect(radios([
      '<RadioGroup label="Что включать" items={ITEMS} value={scope} onChange={setScope} />',
      '<SegmentedButton aria-label="Вид" items={items} value={v} onChange={setV} />',
      '<input type="checkbox" checked={a} onChange={b} />',
      '<div role="radiogroup" aria-label="x">…</div>',
      '<DropdownMenu.RadioItem value="a">А</DropdownMenu.RadioItem>',
    ].join('\n'))).toEqual({});
  });

  it('в shared/ui правило не смотрит — там живёт RadioGroup', () => {
    const text = '<button role="radio" aria-checked={on}>а</button>';
    expect(countAll(text, { dir: 'shared', file: 'shared/ui/RadioGroup.tsx' })['native-radio']).toBeUndefined();
    expect(countAll(text, { dir: 'shared', file: 'shared/siteFilter/SitesFilter.tsx' })['native-radio']).toBe(1);
  });
});

describe('храповик интерфейса: raw-field', () => {
  it('сырое поле со своей рамкой — красное: input, textarea, рамка с вариантом', () => {
    expect(fields([
      '<input value={v} onChange={e => set(e.target.value)} className="w-full border border-outline rounded-xs px-2" />',
      '<textarea rows={3} className={`w-full ${wide} border-b border-outline-variant`} />',
      '<input className="bg-transparent outline-none focus:border-primary" />',
    ].join('\n'))).toEqual({ 'raw-field': 3 });
  });

  it('класс из константы файла — тоже красный', () => {
    expect(fields([
      "const inputCls = 'w-full px-3 py-2 rounded-md border border-outline bg-surface';",
      '<input value={v} className={inputCls} />',
      '<input value={v} className={`${inputCls} font-mono`} />',
    ].join('\n'))).toEqual({ 'raw-field': 2 });
  });

  it('константа-условие, склейка, цепочка и одно имя в двух функциях — тоже красные', () => {
    expect(fields([
      "const cls = invalid ? 'border border-error' : 'border border-outline';",
      '<input className={cls} />',
      "const base = 'w-full border border-outline';",
      "const wide = base + ' px-3';",
      '<input className={wide} />',
      "const chained = (`${wide} font-mono`) as const;",
      '<textarea className={chained} />',
      "function A() { const c = 'px-2'; return <input className={c} />; }",
      "function B() { const c = 'border px-2'; return null; }",
    ].join('\n'))).toEqual({ 'raw-field': 4 });
  });

  it('поле без рамки, снятая рамка, флажок и файл — зелёные; компоненты поля — тоже', () => {
    expect(fields([
      '<input className="w-full bg-transparent outline-none text-body-medium" />',
      '<input className="border-0 bg-transparent" />',
      '<input className="border-none" />',
      '<input type="checkbox" className="border" />',
      '<input type="file" className="hidden border" />',
      '<TextField label="Имя" className="border" />',
      "const quiet = 'px-2 text-body-medium';",
      '<input className={quiet} />',
      '<input className={FIELD_CLS} />',
    ].join('\n'))).toEqual({});
  });
});

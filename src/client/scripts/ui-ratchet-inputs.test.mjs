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
      // Имя, которого нет ни в файле, ни в импортах, — не рамка: раскрывать нечего.
      '<input className={unknownCls} />',
    ].join('\n'))).toEqual({});
  });
});

// Класс из функции файла и из другого модуля (D3 sharedui-md3-19): модули — фикстуры по пути от src/, их отдаёт
// функция чтения, как ui-ratchet.mjs с диска.
const reader = files => path => files[path] ?? null;
const fieldsIn = (text, files, file = 'features/x/Screen.tsx') =>
  countAll(text, { dir: 'features', file, read: reader(files) })['raw-field'] ?? 0;

describe('храповик интерфейса: raw-field видит класс из функции и другого модуля', () => {
  it('функция файла — вызов в className и константа-вызов; функция без вызова — не класс', () => {
    expect(fields([
      "function boxCls(err) { if (err) return 'border border-error'; return 'border border-outline'; }",
      "const arrow = (w: string) => `${w} border-b`;",
      '<input className={boxCls(invalid)} />',
      "const cls = arrow('w-full');",
      '<textarea className={cls} />',
      // Компонент по имени — не класс: раскрывается только вызов.
      'function Frame() { return <div className="border" />; }',
      '<input className={Frame ? "px-2" : ""} />',
    ].join('\n'))).toEqual({ 'raw-field': 2 });
  });

  it('className={cls}, где cls = f() из соседнего модуля, — красный; по алиасу, относительно и через index', () => {
    const files = {
      'features/x/boxes.ts': "export function f() { return 'h-10 border border-outline'; }\nexport const quiet = 'px-2';",
      'shared/kit/index.ts': "export { inner as box } from './inner';",
      'shared/kit/inner.tsx': "const base = 'border px-3';\nexport const inner = (dense) => dense ? base : base + ' h-14';",
    };
    expect(fieldsIn([
      "import { f, quiet } from './boxes';",
      "import { box as kitBox } from '@/shared/kit';",
      'const cls = f();',
      '<input className={cls} />',
      '<input className={kitBox(true)} />',
      '<input className={quiet} />',
    ].join('\n'), files)).toBe(2);
  });

  it('пакет вне src/ не раскрывается; одноимённые константы двух модулей не путаются', () => {
    const files = {
      'features/x/a.ts': "export const cls = 'px-2';",
      'features/x/b.ts': "const cls = 'border';\nexport const other = cls;",
      'pkg': "export const PKG = 'border';",
      'pkg.ts': "export const PKG = 'border';",
    };
    expect(fieldsIn([
      "import { PKG } from 'pkg';",
      "import { cls } from './a';",
      '<input className={PKG} />',
      '<input className={cls} />',
    ].join('\n'), files)).toBe(0);
    expect(fieldsIn("import { other } from './b';\n<input className={other} />", files)).toBe(1);
  });

  it('цикл импортов не зависает', () => {
    const files = {
      'features/x/a.ts': "import { b } from './b';\nexport const a = `${b} px-2`;",
      'features/x/b.ts': "import { a } from './a';\nexport const b = `${a} py-1`;",
    };
    expect(fieldsIn("import { a } from './a';\n<input className={a} />", files)).toBe(0);
  });
});

describe('храповик интерфейса: FIELD_CLS — объявленное исключение (D4)', () => {
  const files = {
    'shared/filter/fieldClass.ts': "export const FIELD_CLS = 'h-10 px-3 rounded-xs border border-outline';\nexport const OTHER = 'border';",
  };
  const at = text => fieldsIn(text, files, 'shared/filter/Screen.tsx');

  it('импортированная FIELD_CLS — не считается, и под другим именем тоже', () => {
    expect(at("import { FIELD_CLS } from './fieldClass';\n<input className={`${FIELD_CLS} w-30`} />")).toBe(0);
    expect(at("import { FIELD_CLS as X } from '@/shared/filter/fieldClass';\n<input className={X} />")).toBe(0);
  });

  it('своя локальная FIELD_CLS, добавленная рамка и другая константа того модуля — считаются', () => {
    expect(at("const FIELD_CLS = 'border px-2';\n<input className={FIELD_CLS} />")).toBe(1);
    expect(at("import { FIELD_CLS } from './fieldClass';\n<input className={`${FIELD_CLS} border-b`} />")).toBe(1);
    expect(at("import { OTHER } from './fieldClass';\n<input className={OTHER} />")).toBe(1);
  });
});

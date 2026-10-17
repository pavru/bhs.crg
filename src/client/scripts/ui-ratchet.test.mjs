import { describe, expect, it } from 'vitest';
import { compare, countViolations } from './ui-ratchet-lib.mjs';

// Храповик интерфейса (issue #1349): что считается нарушением и как сравнивается с уровнем.
describe('храповик интерфейса: счёт', () => {
  it('таблица и нативный список', () => {
    expect(countViolations('<table className="w-full"><tr/></table> <select value={v}>'))
      .toEqual({ table: 1, select: 1 });
  });

  it('компоненты-обёртки нарушением не считаются', () => {
    expect(countViolations('<DataGrid rows={r} /> <Select value={v} /> <selection> <PageHeader title="x" />')).toEqual({});
  });

  it('свой заголовок страницы', () => {
    expect(countViolations('<h1 className="text-xl">Стройки</h1> <h2>Раздел</h2> <h1>')).toEqual({ h1: 2 });
  });

  it('произвольный размер текста — px и rem, но не цвет', () => {
    expect(countViolations('text-[11px] md:text-[12.5px] text-[0.7rem] text-xs text-[#333]'))
      .toEqual({ 'text-size': 3, palette: 1 });
  });

  it('сырая палитра с модификаторами и прозрачностью', () => {
    expect(countViolations('bg-indigo-50 hover:text-purple-700 border-red-200/50 placeholder:text-purple-400/70'))
      .toEqual({ palette: 4 });
  });

  /** Тело окна обычно живёт в своём файле, без `<Modal` в тексте (ревью PR #1386). */
  it('своя ширина — классом и в style, в любом файле экрана', () => {
    expect(countViolations('<div className="space-y-4 min-w-[520px]"> <p className="max-w-[18rem] w-[600px] md:min-w-[30rem]">'))
      .toEqual({ 'fixed-width': 4 });
    expect(countViolations('<span style={{ width: \'120px\' }} /> <div style={{ minWidth: 520 }} />'))
      .toEqual({ 'fixed-width': 2 });
  });

  it('шкала, доля и высота своей шириной не считаются', () => {
    expect(countViolations('min-w-0 max-w-lg w-36 max-w-[45%] w-[calc(100%-1rem)] h-[70vh] max-h-[55vh] style={{ width: pct }}'))
      .toEqual({});
  });

  /** Подсказка браузера вместо нашей (issue #1394): атрибут в любой строке многострочного тега. */
  it('нативный title у элемента разметки', () => {
    expect(countViolations('<button\n  onClick={go}\n  title="Удалить">x</button> <span title={why}>!</span>'))
      .toEqual({ 'native-title': 2 });
  });

  it('title компонента, iframe и полный текст обрезанной строки нарушением не считаются', () => {
    expect(countViolations([
      '<Modal title="Окно" /> <Button title="Почему" /> <iframe title="Скан" />',
      '<span className="truncate" title={f.name}>{f.name}</span>',
      '<p className={`line-clamp-2 ${x}`} title="Длинный текст">Длинный текст</p>',
    ].join(' '))).toEqual({});
  });

  it('у обрезанной строки подсказка не о ней — нарушение', () => {
    expect(countViolations('<button className="truncate" title="Просмотреть документ">{doc.name}</button>'))
      .toEqual({ 'native-title': 1 });
  });

  /** Флажок в обход Checkbox (issue #1414): `type` в любой строке тега, после обработчика со стрелкой. */
  it('нативный флажок', () => {
    expect(countViolations([
      '<input type="checkbox" checked={a} onChange={() => go()} />',
      '<input\n  onChange={e => set(e.target.checked)}\n  type={\'checkbox\'}\n  checked={b}\n/>',
    ].join(' '))).toEqual({ 'native-checkbox': 2 });
  });

  it('Checkbox, другие поля и проверка типа в коде флажком не считаются', () => {
    expect(countViolations([
      '<Checkbox checked={a} onChange={setA} label="Общее" /> <input type="text" />',
      '<input type={kind} /> <div role="checkbox" /> if (el.type !== \'checkbox\') el.select();',
    ].join(' '))).toEqual({});
  });

  /** Свои чипы, пометки, кнопки-иконки и вкладки при готовых Chip, StatusBadge, IconButton, Tabs. */
  it('свой чип: нажимаемая пилюля или aria-pressed', () => {
    expect(countViolations([
      '<button className={`px-2 py-1 rounded-full ${on ? "bg-brand-subtle" : ""}`}>Только новые</button>',
      '<button aria-pressed={on} onClick={go}>Таблица</button>',
    ].join(' '))).toEqual({ chip: 2 });
  });

  it('своя пометка: пилюля с фоном или рамкой', () => {
    expect(countViolations([
      '<span className="text-xs px-2 py-0.5 rounded-full bg-muted text-fg3">строка</span>',
      '<span className={`rounded-full border px-1.5 ${cls}`}>внеш. документ</span>',
    ].join(' '))).toEqual({ badge: 2 });
  });

  it('круг заданного размера, подсветка наведением и компоненты пилюлями не считаются', () => {
    expect(countViolations([
      '<span className="w-2 h-2 rounded-full bg-brand" /> <div className="h-1.5 rounded-full bg-muted" />',
      '<div className="absolute inset-0 rounded-full bg-muted" />',
      '<span className="px-2 rounded-full hover:bg-muted">наведи</span>',
      '<Chip selected>Все</Chip> <StatusBadge tone="success">Оплачен</StatusBadge> <Button>Сохранить</Button>',
    ].join(' '))).toEqual({});
  });

  it('своя кнопка из одной иконки — но не кнопка с подписью', () => {
    expect(countViolations([
      '<button type="button" onClick={close}>\n  <X size={16} />\n</button>',
      '<button type="button"><Plus size={14} /> Добавить</button>',
      '<IconButton label="Закрыть" onClick={close}><X size={16} /></IconButton>',
    ].join(' '))).toEqual({ 'icon-button': 1 });
  });

  it('свои вкладки по ARIA', () => {
    expect(countViolations('<div role="tablist"><button role="tab" aria-selected={on}>A</button></div> <div role="option" />'))
      .toEqual({ tabs: 2 });
  });

  it('токены и чёрно-белые слои палитрой не считаются', () => {
    expect(countViolations('bg-brand-subtle text-tertiary border-danger/30 bg-black/40 hover:bg-white/10 text-fg2'))
      .toEqual({});
  });
});

describe('храповик интерфейса: сравнение по файлам', () => {
  const baseline = { table: { 'features/a.tsx': 2 } };

  it('перенос нарушения в новый файл — рост, хотя общее число то же', () => {
    const { worse, better } = compare(baseline, { table: { 'features/a.tsx': 1, 'features/b.tsx': 1 } });
    expect(worse).toEqual([{ rule: 'table', file: 'features/b.tsx', was: 0, now: 1 }]);
    expect(better).toEqual([{ rule: 'table', file: 'features/a.tsx', was: 2, now: 1 }]);
  });

  it('удалённый файл опускает уровень', () => {
    expect(compare(baseline, {}).better).toEqual([{ rule: 'table', file: 'features/a.tsx', was: 2, now: 0 }]);
  });

  it('совпадение с уровнем — ни роста, ни улучшения', () => {
    expect(compare(baseline, { table: { 'features/a.tsx': 2 } })).toEqual({ worse: [], better: [] });
  });
});

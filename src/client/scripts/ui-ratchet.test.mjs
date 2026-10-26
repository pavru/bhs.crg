import { describe, expect, it } from 'vitest';
import { compare, countViolations, dirsToScan, scopeNote, RULES, ROOT } from './ui-ratchet-lib.mjs';

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

  it('title компонента и полный текст обрезанной строки нарушением не считаются', () => {
    expect(countViolations([
      '<Modal title="Окно" /> <Button title="Почему" />',
      '<span className="truncate" title={f.name}>{f.name}</span>',
      '<p className={`line-clamp-2 ${x}`} title="Длинный текст">Длинный текст</p>',
    ].join(' '))).toEqual({});
  });

  it('у фрейма исключения больше нет: фреймов в разметке нет вовсе ([UI-26])', () => {
    expect(countViolations('<iframe title="Скан" />')).toEqual({ 'native-title': 1, 'native-pdf-frame': 1 });
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

  /** Белое на светлой заливке тёмной темы (issue #1462): пара литералов — где бы ни стояла. */
  it('белое на сплошной заливке — в className целиком и в строке вне его', () => {
    expect(countViolations([
      '<button className="px-4 bg-brand text-white">Сохранить</button>',
      '<button className={`px-2 ${on ? \'bg-brand text-white\' : \'bg-surface text-fg3\'}`}>A</button>',
      '<span className={`bg-danger ${big ? "text-white" : ""}`}>3</span>',
      '<span className={`w-1.5 h-1.5 rounded-full ${on ? \'bg-white\' : \'bg-brand\'}`} />',
      "const DANGER = 'bg-danger text-white shadow';",
      "const sel = 'ring-2 ring-white/70 hover:bg-brand-hover';",
    ].join('\n'))).toEqual({ 'white-on-fill': 6 });
  });

  it('парный токен, контейнер роли и слой состояния белым на заливке не считаются', () => {
    expect(countViolations([
      '<button className="bg-brand text-on-brand hover:bg-brand-hover">A</button>',
      '<span className="bg-danger text-on-danger ring-on-danger">!</span>',
      '<div className="bg-brand-subtle text-on-brand-subtle dark:hover:bg-white/10">B</div>',
      '<div className="bg-black/40 text-white">затемнение</div>',
      '<div className="bg-surface hover:bg-white/10" /> <Logo className="bg-brand" />',
    ].join('\n'))).toEqual({});
  });

  /** Ревью #1477: прозрачность, заливка модификатором, свои белые утилиты, произвольное значение, не-className. */
  it('белое на заливке — во всех записях заливки и белого', () => {
    expect(countViolations([
      '<span className="bg-brand/90 text-white" />',
      '<button className="hover:bg-brand-hover fill-white" />',
      '<div className="bg-success stroke-white divide-white" />',
      '<div className="bg-[var(--f-brand)] text-white" /> <i className="bg-[var(--color-danger)] shadow-white" />',
      '<Badge iconClassName="bg-warning text-white" />',
    ].join('\n'))).toEqual({ 'white-on-fill': 6 });
  });

  /** Календарь, портированный внутрь окна (issue #1521): его резал край окна. */
  it('портал слоя в свой контейнер — в features/ и в shared/', () => {
    const text = [
      '<Popover.Portal container={portalContainer ?? undefined}><i /></Popover.Portal>',
      '<DropdownMenu.Portal\n  forceMount={open && n > 0}\n  onX={() => go()}\n  container={el}><i /></DropdownMenu.Portal>',
      '<Portal container={host} />',
      '{createPortal(node, dialogEl)} {ReactDOM.createPortal(node, el)}',
    ].join('\n');
    expect(countViolations(text)).toEqual({ 'portal-container': 5 });
    expect(countViolations(text, { dir: 'shared' })).toEqual({ 'portal-container': 5 });
  });

  it('портал в body, data-container и чужой container= порталом не считаются', () => {
    expect(countViolations([
      '<Popover.Portal><i /></Popover.Portal> <Select.Portal><i /></Select.Portal>',
      '<Tooltip.Portal data-container="x"><i /></Tooltip.Portal>',
      '<Panel container={el} /> <div data-portal="1" />',
    ].join('\n'), { dir: 'shared' })).toEqual({});
  });

  it('нативное поле даты: литерал, выражение и TextField — по одному на поле', () => {
    const text = [
      '<input type="date" /> <input\n  onChange={e => go(e)}\n  type="month">',
      "<input type={typed && kind === 'date' ? 'date' : 'text'} />",
      '<input type={`datetime-local`} /> <TextField type="time" label="Время" /> <input type="week" />',
    ].join('\n');
    expect(countViolations(text)).toEqual({ 'native-date': 6 });
  });

  it('нативное поле даты считается в любом каталоге src/ и в его корне', () => {
    for (const dir of ['shared', 'app', ROOT])
      expect(countViolations('<input type="date" />', { dir })).toEqual({ 'native-date': 1 });
  });

  it('текстовое поле, чужой type и «date» вне type полем даты не считаются', () => {
    expect(countViolations([
      '<input type="text" placeholder="date" /> <input type="number" />',
      "<Field type=\"date\" /> <PrimitiveInput type={'date'} /> <input data-type=\"date\" />",
      "{field.type === 'date' && <input type=\"text\" />}",
    ].join('\n'), { dir: 'shared' })).toEqual({});
  });

  it('литерал из условия в type не считается — только то, чем type может стать', () => {
    expect(countViolations([
      "<input type={kind === 'date' ? 'text' : 'number'} />",
      "<input type={(kind === 'time' || kind === 'week') && 'text'} />",
    ].join('\n'))).toEqual({});
    expect(countViolations([
      "<input type={(on && 'month') || 'text'} />",
      "<input type={override ?? 'time'} />",
    ].join('\n'))).toEqual({ 'native-date': 2 });
  });

  it('TextField под другим именем при импорте — тоже поле', () => {
    const text = "import { TextField as Input, Select } from '@/shared/ui/TextField';\n<Input type=\"date\" /> <Select type=\"date\" />";
    expect(countViolations(text)).toEqual({ 'native-date': 1 });
  });

  // #1464: рамку поля даты рисует size, а не экран.
  it('своя рамка у DateInput — строкой и выражением; size, ширина и пометка border-…! — нет', () => {
    expect(countViolations([
      '<DateInput label="Дата" className="w-full border rounded px-2" />',
      '<DateInput label="Дата"\n  className={`flex-1 ${on ? \'border-brand-subtle\' : \'border-stroke\'}`} />',
    ].join('\n'))).toEqual({ 'date-frame': 2 });
    expect(countViolations([
      '<DateInput label="Дата" size="sm" className="flex-1" />',
      "<DateInput label=\"Дата\" size=\"sm\" className={`w-full ${hasDv ? 'border-brand-subtle!' : ''}`} />",
      '<DateInput label="Дата" compact className="w-full h-full focus-within:bg-brand-subtle" />',
      '<div className="border rounded" />',
    ].join('\n'))).toEqual({});
  });

  it('рамка, вынесенная в константу файла, — по её значению; пометка border-…! в константе — нет', () => {
    expect(countViolations([
      "const frame = 'border border-stroke rounded';",
      '<DateInput label="Дата" className={frame} />',
      '<DateInput label="Дата" className={`w-full ${frame}`} />',
    ].join('\n'))).toEqual({ 'date-frame': 2 });
    expect(countViolations([
      "const selectCls = hasDv ? 'border-brand-subtle!' : '';",
      '<DateInput label="Дата" size="sm" className={`w-full ${selectCls}`} />',
    ].join('\n'))).toEqual({});
  });

  /** Встроенный просмотрщик браузера во фрейме вместо своего (issue #1490, [UI-23]). */
  it('фрейм в features/ — тегом и через createElement, по одному на фрейм', () => {
    expect(countViolations([
      '<iframe src={url} />',
      '<embed\n  type="application/pdf"\n  src={url} /> <object data={url}><p>нет</p></object>',
      "const f = document.createElement('iframe'); const g = doc.createElement(`EMBED`); createElement(\"object\");",
    ].join('\n'))).toEqual({ 'native-pdf-frame': 6 });
  });

  /** Фрейм печати назван путём ([UI-26]): соседний файл shared/ui того же фрейма не получает. */
  it('фрейм в shared/ считается везде, кроме shared/ui/viewer/', () => {
    const frame = "const frame = document.createElement('iframe');";
    expect(countViolations(frame, { dir: 'shared', file: 'shared/ui/viewer/printPdf.ts' })).toEqual({});
    expect(countViolations(frame, { dir: 'shared', file: 'shared/ui/X.tsx' })).toEqual({ 'native-pdf-frame': 1 });
    expect(countViolations('<iframe src={url} />', { dir: 'shared', file: 'shared/api/x.tsx' }))
      .toEqual({ 'native-pdf-frame': 1 });
    // Префикс — каталог, а не начало имени: shared/ui/viewerX/ под исключение не попадает.
    expect(countViolations(frame, { dir: 'shared', file: 'shared/ui/viewerX/a.ts' })).toEqual({ 'native-pdf-frame': 1 });
  });

  it('обобщённый тип WeakMap<object, …> в shared/ — не фрейм ([UI-26])', () => {
    expect(countViolations('const seen = new WeakMap<object, unknown[]>();', { dir: 'shared', file: 'shared/utils/rowIdentity.ts' }))
      .toEqual({});
  });

  it('чужие теги и слово «iframe» в тексте — не фрейм', () => {
    expect(countViolations([
      "<PdfViewer source={bytes} /> document.createElement('div'); createElement(tag);",
      '<p>Раньше здесь стоял iframe, embed и object.</p> <Frame />',
    ].join('\n'))).toEqual({});
  });

  it('в shared/ считаются только правила, которые его смотрят (dirs)', () => {
    const text = '<table /> <select /> <button className="bg-danger text-white">x</button>';
    expect(countViolations(text, { dir: 'shared' })).toEqual({ 'white-on-fill': 1 });
    expect(countViolations(text)).toEqual({ table: 1, select: 1, 'white-on-fill': 1 });
  });

  it('токены и чёрно-белые слои палитрой не считаются', () => {
    expect(countViolations('bg-brand-subtle text-tertiary border-danger/30 bg-black/40 hover:bg-white/10 text-fg2'))
      .toEqual({});
  });
});

describe('храповик интерфейса: какие каталоги обходить', () => {
  const top = ['assets', 'features', 'shared', 'widgets'];

  it('правило со всем src/ добавляет корень и каждый каталог с диска, и новый тоже', () => {
    expect(dirsToScan([{ id: 'a' }, { id: 'b', everywhere: true }], top)).toEqual([ROOT, ...top]);
  });

  it('без него — только названные правилами каталоги, без повторов', () => {
    expect(dirsToScan([{ id: 'a' }, { id: 'b', dirs: ['features', 'shared'] }], top)).toEqual(['features', 'shared']);
  });

  it('настоящие правила обходят весь src/: native-date смотрит его целиком', () => {
    expect(dirsToScan(RULES, top)).toEqual([ROOT, ...top]);
    expect(scopeNote(RULES.find(r => r.id === 'native-date'))).toBe('native-date — весь src/');
    expect(scopeNote(RULES.find(r => r.id === 'white-on-fill'))).toBe('white-on-fill — ещё в shared/');
    expect(scopeNote(RULES.find(r => r.id === 'table'))).toBeNull();
    expect(scopeNote(RULES.find(r => r.id === 'native-pdf-frame')))
      .toBe('native-pdf-frame — ещё в shared/, кроме shared/ui/viewer/');
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

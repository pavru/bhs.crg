import { describe, expect, it } from 'vitest';
import { compare, countViolations as countAll, dirsToScan, scopeNote, defaultScopeNote, ICON_SCALE, RULES, ROOT } from './ui-ratchet-lib.mjs';

// Фикстуры правил — на ролях MD3 (sharedui-md3-02). Старые имена остались там, где проверяются они сами:
// одна фикстура white-on-fill (bg-brand text-white), имена без роли (bg-brand-hover, text-fg3) и блок
// legacy-class ниже. Здесь счёт прочих правил — без legacy-class, raw-state и dark-variant: фикстуры
// white-on-fill и пометок держат самодельное наведение нарочно (оно не их нарушение), а эти правила
// проверяют свои блоки. legacy-radius и legacy-shadow (sharedui-md3-06) — в ui-ratchet-shape.test.mjs: прежние фикстуры
// пишут rounded, rounded-lg и тени старыми классами, и это не их нарушение. legacy-type (sharedui-md3-07) — в
// ui-ratchet-type.test.mjs по той же причине: фикстуры пишут text-xs, font-semibold и uppercase.
const OWN_BLOCK = new Set(['legacy-class', 'raw-state', 'dark-variant', 'legacy-radius', 'legacy-shadow', 'legacy-type']);
const countViolations = (text, opts) =>
  Object.fromEntries(Object.entries(countAll(text, opts)).filter(([rule]) => !OWN_BLOCK.has(rule)));

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

  /** Строка медиазапроса — не ширина элемента: так ловился useMedia (#1469). */
  it('медиазапрос своей шириной не считается, ширина в style рядом — считается', () => {
    expect(countViolations("useMedia('(min-width: 768px)') matchMedia('(max-width:640px)')", { dir: 'shared', file: 'shared/hooks/useMedia.ts' }))
      .toEqual({});
    expect(countViolations('<div style={{ width: 120 }} />', { dir: 'shared', file: 'shared/filter/x.tsx' }))
      .toEqual({ 'fixed-width': 1 });
    // Та же запись строкой CSS — ширина элемента, а не условие медиазапроса.
    expect(countViolations("el.style.cssText = 'min-width: 520px'; box.style.width = 'x'; const css = \"max-width:200px\";"))
      .toEqual({ 'fixed-width': 2 });
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
      '<button className={`px-2 py-1 rounded-full ${on ? "bg-primary-container" : ""}`}>Только новые</button>',
      '<button aria-pressed={on} onClick={go}>Таблица</button>',
    ].join(' '))).toEqual({ chip: 2 });
  });

  it('своя пометка: пилюля с фоном или рамкой', () => {
    expect(countViolations([
      '<span className="text-xs px-2 py-0.5 rounded-full bg-surface-container text-fg3">строка</span>',
      '<span className={`rounded-full border px-1.5 ${cls}`}>внеш. документ</span>',
    ].join(' '))).toEqual({ badge: 2 });
  });

  it('круг заданного размера, подсветка наведением и компоненты пилюлями не считаются', () => {
    expect(countViolations([
      '<span className="w-2 h-2 rounded-full bg-primary" /> <div className="h-1.5 rounded-full bg-surface-container" />',
      '<div className="absolute inset-0 rounded-full bg-surface-container" />',
      '<span className="px-2 rounded-full hover:bg-surface-container">наведи</span>',
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

  /** Своя половина split-кнопки ([UI-83]): одностороннее скругление у кнопки, а не у плашки. */
  it('самодельная split-кнопка — кнопка с односторонним скруглением', () => {
    expect(countViolations([
      '<button className="h-8 rounded-l-full bg-primary">Сохранить</button>',
      '<IconButton label="Ещё" className="rounded-l-none bg-primary"><Icon icon={Down} size={18} /></IconButton>',
      '<Button className={`${x} active:rounded-tr-xs`}>Да</Button>',
      '<a href="/x" className="rounded-e-full">Ссылка</a>',
    ].join(' '))).toEqual({ 'split-button': 4 });
  });

  it('скругление плашки, панели и целой кнопки — не split', () => {
    expect(countViolations([
      '<div className="rounded-r-md border-l-2">условие</div>',
      '<span className="rounded-l-xs">a</span>',
      '<Button className="rounded-full">Да</Button>',
      '<button className="rounded-lg">Нет</button>',
    ].join(' '))).toEqual({});
  });

  it('свои вкладки по ARIA', () => {
    expect(countViolations('<div role="tablist"><button role="tab" aria-selected={on}>A</button></div> <div role="option" />'))
      .toEqual({ tabs: 2 });
  });

  /** Белое на светлой заливке тёмной темы (issue #1462): пара литералов — где бы ни стояла. */
  it('белое на сплошной заливке — в className целиком и в строке вне его', () => {
    expect(countViolations([
      '<button className="px-4 bg-primary text-white">Сохранить</button>',
      '<button className={`px-2 ${on ? \'bg-primary text-white\' : \'bg-surface text-fg3\'}`}>A</button>',
      '<span className={`bg-error ${big ? "text-white" : ""}`}>3</span>',
      '<span className={`w-1.5 h-1.5 rounded-full ${on ? \'bg-white\' : \'bg-brand\'}`} />',
      "const DANGER = 'bg-error text-white shadow';",
      "const sel = 'ring-2 ring-white/70 hover:bg-brand-hover';",
    ].join('\n'))).toEqual({ 'white-on-fill': 6 });
  });

  it('парный токен, контейнер роли и слой состояния белым на заливке не считаются', () => {
    expect(countViolations([
      '<button className="bg-primary text-on-primary hover:bg-brand-hover">A</button>',
      '<span className="bg-error text-on-error ring-on-error">!</span>',
      '<div className="bg-primary-container text-on-primary-container dark:hover:bg-white/10">B</div>',
      '<div className="bg-black/40 text-white">затемнение</div>',
      '<div className="bg-surface hover:bg-white/10" /> <Logo className="bg-primary" />',
    ].join('\n'))).toEqual({});
  });

  /** Ревью #1477: прозрачность, заливка модификатором, свои белые утилиты, произвольное значение, не-className. */
  it('белое на заливке — во всех записях заливки и белого', () => {
    expect(countViolations([
      '<span className="bg-primary/90 text-white" />',
      '<button className="hover:bg-brand-hover fill-white" />',
      '<div className="bg-success stroke-white divide-white" />',
      '<div className="bg-[var(--md-sys-color-primary)] text-white" /> <i className="bg-[var(--color-error)] shadow-white" />',
      '<Badge iconClassName="bg-warning text-white" />',
    ].join('\n'))).toEqual({ 'white-on-fill': 6 });
  });

  /** Роли MD3 и переменные темы — тот же сторож, что у старых имён (sharedui-md3-01, [UI-58]). */
  it('белое на заливке роли MD3 и её переменной; контейнер роли — не заливка', () => {
    expect(countViolations([
      '<button className="bg-primary text-white" />',
      '<span className="bg-[var(--md-sys-color-error)] text-white" />',
      '<i className="hover:bg-[var(--md-ext-color-success)] fill-white" /> <b className="bg-warning ring-white" />',
    ].join('\n'))).toEqual({ 'white-on-fill': 4 });
    expect(countViolations([
      '<div className="bg-primary-container text-white" />',
      '<div className="bg-[var(--md-sys-color-primary-container)] text-white" />',
      '<div className="bg-error-container text-white" /> <div className="bg-primary text-on-primary" />',
    ].join('\n'))).toEqual({});
  });

  /** Старые имена — пока живы псевдонимы ([UI-58]): ветка, начатая до перевода, пишет ими. */
  it('белое на заливке старым именем и его переменной; парный старый токен — не нарушение', () => {
    expect(countViolations([
      '<button className="bg-brand text-white" />',
      '<span className="bg-danger/90 text-white" />',
      '<i className="bg-[var(--color-danger)] shadow-white" />',
    ].join('\n'))).toEqual({ 'white-on-fill': 3 });
    expect(countViolations([
      '<button className="bg-brand text-on-brand hover:bg-brand-hover">A</button>',
      '<span className="bg-danger text-on-danger ring-on-danger">!</span>',
      '<div className="bg-brand-subtle text-on-brand-subtle dark:hover:bg-white/10">B</div>',
    ].join('\n'))).toEqual({});
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
      '<DateInput label="Дата"\n  className={`flex-1 ${on ? \'border-primary-container\' : \'border-outline-variant\'}`} />',
    ].join('\n'))).toEqual({ 'date-frame': 2 });
    expect(countViolations([
      '<DateInput label="Дата" size="sm" className="flex-1" />',
      "<DateInput label=\"Дата\" size=\"sm\" className={`w-full ${hasDv ? 'border-primary-container!' : ''}`} />",
      '<DateInput label="Дата" compact className="w-full h-full focus-within:bg-primary-container" />',
      '<div className="border rounded" />',
    ].join('\n'))).toEqual({});
  });

  it('рамка, вынесенная в константу файла, — по её значению; пометка border-…! в константе — нет', () => {
    expect(countViolations([
      "const frame = 'border border-outline-variant rounded';",
      '<DateInput label="Дата" className={frame} />',
      '<DateInput label="Дата" className={`w-full ${frame}`} />',
    ].join('\n'))).toEqual({ 'date-frame': 2 });
    expect(countViolations([
      "const selectCls = hasDv ? 'border-primary-container!' : '';",
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

  /** Граница — shared/ui/, а не shared/ (#1469): обвязка shared/ — такие же экраны, как features/. */
  it('обвязка shared/ считается всеми правилами, shared/ui/ — только теми, что его назвали (dirs)', () => {
    const text = '<table /> <select /> <button className="bg-error text-white">x</button>';
    const all = { table: 1, select: 1, 'white-on-fill': 1 };
    expect(countViolations(text)).toEqual(all);
    expect(countViolations(text, { dir: 'shared', file: 'shared/filter/x.tsx' })).toEqual(all);
    expect(countViolations(text, { dir: 'shared', file: 'shared/brandNew/x.tsx' })).toEqual(all);
    expect(countViolations(text, { dir: 'shared', file: 'shared/ui/x.tsx' })).toEqual({ 'white-on-fill': 1 });
  });

  it('нативный select в отборе — нарушение с выходом Select', () => {
    expect(countViolations('<select value={v} />', { dir: 'shared', file: 'shared/filter/ConditionEditor.tsx' }))
      .toEqual({ select: 1 });
    expect(RULES.find(r => r.id === 'select').instead).toMatch(/Select \(shared\/ui\/Select\)/);
  });

  it('токены и чёрно-белые слои палитрой не считаются', () => {
    expect(countViolations('bg-primary-container text-tertiary border-error/30 bg-black/40 hover:bg-white/10 text-on-surface-variant'))
      .toEqual({});
  });
});

describe('храповик интерфейса: словарь MD3 (sharedui-md3-01)', () => {
  it('токены Fluent — в коде и в стилях, по всему src/', () => {
    expect(countAll('<div className="shadow-[var(--f-shadow16)]" style={{ boxShadow: "var(--f-shadow28)" }} />',
      { dir: 'features', file: 'features/x.tsx' })).toEqual({ 'legacy-token': 2, 'legacy-shadow': 2 });
    expect(countAll('.x { outline: 2px solid var(--f-brand); }', { dir: ROOT, file: 'index.css' }))
      .toEqual({ 'legacy-token': 1 });
    expect(countAll('const c = "var(--legacy-fg3)";', { dir: 'shared', file: 'shared/ui/x.ts' }))
      .toEqual({ 'legacy-token': 1 });
  });

  it('сгенерированная тема своих --legacy-* и старых имён не считает; стили — только legacy-*', () => {
    expect(countAll(':root { --legacy-fg3: #5b5f67; } @theme inline { --color-fg3: var(--legacy-fg3); --color-x: var(--color-brand); }',
      { dir: 'theme', file: 'theme/md3-legacy.css' })).toEqual({});
    expect(countAll('.a { width: 520px; } .b { color: #fff; } <table>', { dir: ROOT, file: 'index.css' })).toEqual({});
  });

  it('старое имя в файле стилей — тоже legacy-class', () => {
    expect(countAll('.x { outline: 2px solid var(--color-brand); } /* var(--color-fg1) */',
      { dir: 'shared', file: 'shared/ui/viewer/textLayer.css' })).toEqual({ 'legacy-class': 1 });
  });

  it('старые имена — с вариантом, прозрачностью и через var(--color-…)', () => {
    expect(countAll([
      '<div className="hover:bg-brand-subtle text-fg1 border-stroke/50" />',
      "const s = { boxShadow: 'inset 0 2px 0 0 var(--color-brand)' }; const d = 'var(--color-fg3)';",
      '/* text-fg2 в комментарии не рисуется */',
    ].join('\n'), { dir: 'shared', file: 'shared/ui/x.tsx' })).toEqual({ 'legacy-class': 5, 'legacy-shadow': 1 });
  });

  /** Граница класса — та же, что у кодмода md3:migrate (sharedui-md3-02): что он переводит, то сторож и видит. */
  it('старые имена в селекторе и в строке внутри строки; путь файла — не класс', () => {
    expect(countAll([
      "querySelectorAll('span.bg-brand-subtle'); getByText('x', { selector: '.text-fg1' })",
      '.bg-brand:hover, .row.border-stroke { }',
      "const t = '${on ? \\'border-brand-subtle\\' : \\'\\'}';",
      "import icon from './bg-brand.svg'; url('text-fg1.png')",
    ].join('\n'), { dir: 'shared', file: 'shared/ui/x.tsx' })).toEqual({ 'legacy-class': 5 });
  });

  it('третий серый в новом файле — legacy-class с советом on-surface-variant (sharedui-md3-04, [UI-63])', () => {
    expect(countAll('<p className="text-label-small text-fg4" />', { dir: 'features', file: 'features/x/New.tsx' }))
      .toEqual({ 'legacy-class': 1 });
    const advice = RULES.find(r => r.id === 'legacy-class').instead;
    expect(advice).toContain('fg3 → on-surface-variant');
    expect(advice).toContain('fg4 → on-surface-variant');
  });

  it('снятое имя без псевдонима (warning-border, stroke-subtle) — всё равно старое: класса оно больше не даёт', () => {
    expect(countAll('<div className="border border-warning-border divide-stroke-subtle" />', { dir: 'features', file: 'features/x/Old.tsx' }))
      .toEqual({ 'legacy-class': 2 });
  });

  it('имена ролей, общие у обоих словарей, и размер text-base — не старые имена', () => {
    expect(countAll('text-success bg-surface text-tertiary text-base text-base/6 bg-primary text-on-surface-variant on-warning'))
      .toEqual({});
  });
});

describe('храповик интерфейса: текст на контейнере (sharedui-md3-04)', () => {
  const file = { dir: 'features', file: 'features/x/New.tsx' };

  it('цвет роли на её контейнере в одном списке — нарушение, с советом парной роли', () => {
    expect(countAll('<div className="bg-warning-container text-warning" />', file)).toEqual({ 'container-text': 1 });
    expect(countAll("<span className={on ? 'bg-error-container text-error' : ''} />", file)).toEqual({ 'container-text': 1 });
    expect(RULES.find(r => r.id === 'container-text').instead).toContain('text-on-warning-container');
  });

  it('парная роль, другой вариант, разнесённое и комментарий — не нарушение', () => {
    expect(countAll([
      '<div className="bg-warning-container text-on-warning-container" />',
      '<div className="text-warning hover:bg-warning-container" />',
      "const box = 'bg-error-container'; const label = 'text-error';",
      '// "bg-error-container text-error"',
    ].join('\n'), file)).toEqual({});
  });
});

describe('храповик интерфейса: состояния (sharedui-md3-05)', () => {
  const file = { dir: 'features', file: 'features/x/New.tsx' };
  const only = (text, rule) => countAll(text, file)[rule] ?? 0;

  it('самодельное наведение, подложка, яркость и наведение заливкой поверхности — raw-state', () => {
    expect(only('<button className="hover:bg-black/5" />', 'raw-state')).toBe(1);
    expect(only("const cls = 'bg-white/10';", 'raw-state')).toBe(1);
    expect(only('<button className="hover:brightness-95 active:brightness-90" />', 'raw-state')).toBe(2);
    expect(only('<li className="hover:bg-surface-container" />', 'raw-state')).toBe(1);
    expect(only('<li className="group-hover/row:bg-surface-container-low data-[highlighted]:bg-muted" />', 'raw-state')).toBe(2);
    expect(only("<span className={`p-1 ${on ? '' : 'dark:hover:bg-white/10'}`} />", 'raw-state')).toBe(1);
  });

  it('dark: в классе — dark-variant, вместе с raw-state у пары наведения', () => {
    expect(countAll('<button className="hover:bg-black/5 dark:hover:bg-white/10" />', file))
      .toEqual({ 'raw-state': 2, 'dark-variant': 1 });
    expect(only('<div className="shadow dark:shadow-none dark:outline" />', 'dark-variant')).toBe(2);
  });

  it('затемнение ролью scrim, заливка поверхностью без наведения, слой MD3 и регулярка — не нарушение', () => {
    expect(countAll([
      '<div className="fixed inset-0 bg-scrim/32" />',
      '<div className="bg-surface-container hover:underline" />',
      '<button className="state-layer focus-ring disabled-container [--_state-color:var(--color-error)]" />',
      "const looks = cls => cls.filter(c => !/^(dark:)?(hover|active):/.test(c));",
      '// hover:bg-black/5 dark:hover:bg-white/10 — так было до перехода',
    ].join('\n'), file)).toEqual({});
  });

  it('совет называет выход', () => {
    expect(RULES.find(r => r.id === 'raw-state').instead).toContain('state-layer');
    expect(RULES.find(r => r.id === 'raw-state').instead).toContain('bg-scrim/32');
    expect(RULES.find(r => r.id === 'dark-variant').instead).toContain(':root[data-theme="dark"]');
  });
});

describe('храповик интерфейса: значки (sharedui-md3-03)', () => {
  const screen = { dir: 'features', file: 'features/x.tsx' };

  it('прежний набор и пакет Symbols в обход набора — по всему src/', () => {
    expect(countViolations([
      "import { Trash2, type LucideIcon } from 'lucide-react';",
      "import del from '@material-symbols/svg-400/outlined/delete.svg';",
      "const lazy = import('lucide-react');",
      "import { Delete } from '../../shared/ui/icons';",
    ].join('\n'), screen)).toEqual({ 'lucide-import': 3 });
    expect(countViolations("import { X } from 'lucide-react';", { dir: 'shared', file: 'shared/ui/Chip.tsx' }))
      .toEqual({ 'lucide-import': 1 });
    // Пояснение в комментарии — не импорт.
    expect(countViolations("// раньше: import { X } from 'lucide-react';\n/* from '@material-symbols/svg-400' */", screen))
      .toEqual({});
  });

  it('вращающийся значок — у компонента и svg, в том числе в самом индикаторе', () => {
    expect(countViolations([
      '<Loader2 size={14} className="animate-spin" />',
      '<Icon icon={Sync} className={`text-primary ${busy ? \'animate-spin\' : \'\'}`} />',
      '<svg className="animate-spin h-4 w-4" />',
      '<div className="animate-spin rounded-full" /> <Icon icon={Sync} className="animate-spin-slow" />',
    ].join('\n'), screen)).toEqual({ 'spinner-icon': 3 });
    expect(countViolations('<svg className="animate-spin" />', { dir: 'shared', file: 'shared/ui/CircularProgress.tsx' }))
      .toEqual({ 'spinner-icon': 1 });
  });

  it('размер значка вне шкалы — литералом и ветвью условия; переменную держит тип', () => {
    expect(countViolations([
      "import { Icon } from '../shared/ui/Icon';",
      '<Icon icon={Close} size={13} />',
      '<Icon icon={Close} size={compact ? 16 : 20} />',
      '<Icon icon={Close} size={18} /> <Icon icon={Close} size={big ? 48 : 24} /> <Icon icon={Close} size={size} />',
      '<Icon icon={Close} /> <Chip size={13} />',
    ].join('\n'), screen)).toEqual({ 'icon-size': 2 });
    // Имя при импорте — тоже наш значок; локальный `Icon` (компонент из данных) — чужой.
    expect(countViolations("import { Icon as Glyph } from '@/shared/ui/Icon';\n<Glyph icon={Close} size={12} />", screen))
      .toEqual({ 'icon-size': 1 });
    expect(countViolations('const Icon = section.def.icon;\n<Icon size={20} />', screen)).toEqual({});
  });

  it('значок — только Icon из shared/ui/Icon, а не любой модуль с именем Icon', () => {
    const own = "import { Icon } from './Icon';\n<Icon size={16} />";
    expect(countViolations(own, screen)).toEqual({});
    expect(countViolations(own, { dir: 'shared', file: 'shared/ui/Chip.tsx' })).toEqual({ 'icon-size': 1 });
    expect(countViolations("import { Icon } from '../../widgets/Icon';\n<Icon size={16} />", screen)).toEqual({});
    expect(countViolations("import { Icon } from '../../shared/ui/Icon';\n<Icon size={16} />",
      { dir: 'features', file: 'features/costs/x.tsx' })).toEqual({ 'icon-size': 1 });
  });

  it('размер мимо size — классом или style', () => {
    expect(countViolations([
      "import { Icon } from '@/shared/ui/Icon';",
      '<Icon icon={Close} className="h-3.5 w-3.5" />',
      '<Icon icon={Close} className={cn("text-primary", dense && "sm:size-4")} />',
      '<Icon icon={Close} style={{ height: 13 }} />',
      '<Icon icon={Close} className="shrink-0 text-error" style={{ color: c }} />',
    ].join('\n'), screen)).toEqual({ 'icon-size': 3 });
  });

  it('шкала храповика — та же, что у типа IconSize', async () => {
    const { ICON_SIZES } = await import('../src/shared/ui/icons/types.ts');
    expect(ICON_SCALE).toEqual([...ICON_SIZES]);
  });

  it('кнопка из одного значка Icon — своя кнопка-иконка', () => {
    expect(countViolations('<button type="button" onClick={f}>\n  <Icon icon={Close} size={18} />\n</button>', screen))
      .toEqual({ 'icon-button': 1 });
  });
});

describe('храповик интерфейса: какие каталоги обходить', () => {
  const top = ['assets', 'features', 'shared', 'widgets'];

  it('правило со всем src/ добавляет корень и каждый каталог с диска, и новый тоже', () => {
    expect(dirsToScan([{ id: 'a' }, { id: 'b', everywhere: true }], top)).toEqual([ROOT, ...top]);
  });

  it('без него — только названные правилами каталоги, без повторов', () => {
    expect(dirsToScan([{ id: 'a', dirs: ['features'] }, { id: 'b', dirs: ['features', 'shared'] }], top)).toEqual(['features', 'shared']);
  });

  it('правило без dirs обходит features/ и shared/ (#1469)', () => {
    expect(dirsToScan([{ id: 'a' }], top)).toEqual(['features', 'shared']);
  });

  it('настоящие правила обходят весь src/: native-date смотрит его целиком', () => {
    expect(dirsToScan(RULES, top)).toEqual([ROOT, ...top]);
    expect(scopeNote(RULES.find(r => r.id === 'native-date'))).toBe('native-date — весь src/');
    expect(scopeNote(RULES.find(r => r.id === 'white-on-fill'))).toBe('white-on-fill — и в shared/ui/');
    expect(scopeNote(RULES.find(r => r.id === 'portal-container'))).toBe('portal-container — и в shared/ui/');
    expect(scopeNote(RULES.find(r => r.id === 'table'))).toBeNull();
    expect(scopeNote(RULES.find(r => r.id === 'legacy-token'))).toBe('legacy-token — весь src/ со стилями, кроме theme/');
    expect(scopeNote(RULES.find(r => r.id === 'legacy-class'))).toBe('legacy-class — весь src/ со стилями, кроме theme/');
    expect(scopeNote(RULES.find(r => r.id === 'native-pdf-frame')))
      .toBe('native-pdf-frame — и в shared/ui/, кроме shared/ui/viewer/');
    expect(defaultScopeNote()).toBe('features/ и shared/ без shared/ui/');
    // Уже области по умолчанию — тоже названо: иначе сводка приписала бы правилу shared/.
    expect(scopeNote({ id: 'x', dirs: ['features'] })).toBe('x — без shared/');
    expect(scopeNote({ id: 'y', dirs: ['features', 'shared', 'app'] })).toBe('y — и в app/, shared/ui/');
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

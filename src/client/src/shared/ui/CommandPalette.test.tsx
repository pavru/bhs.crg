// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import type { ReactNode } from 'react';
import type { AccessInfo } from '@/shared/api/access';
import type { RecordSearchGroup, RecordSearchState } from '@/shared/api/recordSearch';
import { CommandPalette } from './CommandPalette';
import { paletteLabels as labels, paletteOptions } from '@/shared/testing/palette';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { WorkspaceContext, useWorkspace } from './workspaceContext';

let access: AccessInfo;
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ setTheme: vi.fn() }) }));
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access }),
}));

// Ответ поиска записей — подставной: сервер ищет в правах, здесь проверяется, что палитра с ответом делает.
let records: RecordSearchState = { groups: undefined, failed: false, pending: false, tooLong: false };
const asked = vi.fn();
vi.mock('@/shared/api/recordSearch', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/recordSearch')>(),
  useRecordSearch: (q: string, enabled: boolean) => { asked(q, enabled); return records; },
}));

afterEach(() => { cleanup(); records = { groups: undefined, failed: false, pending: false, tooLong: false }; asked.mockClear(); });

const costs = [{ code: 'costs', title: 'Счета и накладные', available: true }];
const reader: AccessInfo = { permissions: ['costs.invoice.read'], modules: costs };
const clerk: AccessInfo = { permissions: ['costs.invoice.read', 'costs.invoice.edit'], modules: costs };

function Address() {
  const { pathname, search } = useLocation();
  return <output aria-label="адрес">{pathname + search}</output>;
}

function shown(who: AccessInfo, at = '/') {
  access = who;
  return render(
    <MemoryRouter initialEntries={[at]}>
      <NavigationGuardProvider>
        <Address />
        <CommandPalette open onOpenChange={() => {}} />
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
}

const search = () => screen.getByRole('textbox', { name: 'Поиск и действия', hidden: true });
function type(text: string) { fireEvent.change(search(), { target: { value: text } }); }
const address = () => screen.getByLabelText('адрес').textContent;

/** Вид — search view MD3 (sharedui-md3-15). */
describe('CommandPalette — вид', () => {
  it('scrim без размытия, поверхность surface-container-high', () => {
    shown(reader);
    const dialog = screen.getByRole('dialog', { name: 'Поиск и действия', hidden: true });
    expect(dialog.className).toContain('bg-surface-container-high');
    expect(dialog.className).toContain('rounded-xl');
    const scrim = document.querySelector<HTMLElement>('[class*="bg-scrim"]')!;
    expect(scrim).toBeTruthy();
    expect(scrim.style.backdropFilter).toBe('');
  });

  it('уже 768 — во весь экран, «Назад» закрывает, подсказки клавиш нет', () => {
    const wide = window.innerWidth;
    window.innerWidth = 375;
    try {
      const onOpenChange = vi.fn();
      access = reader;
      render(<MemoryRouter><NavigationGuardProvider><CommandPalette open onOpenChange={onOpenChange} /></NavigationGuardProvider></MemoryRouter>);
      const dialog = screen.getByRole('dialog', { name: 'Поиск и действия', hidden: true });
      expect(dialog.className).toContain('inset-0');
      expect(dialog.className).not.toContain('rounded-xl');
      expect(dialog.textContent).not.toContain('Esc');
      expect(dialog.textContent).not.toContain('выбрать');
      expect(dialog.textContent).not.toContain('Enter');
      fireEvent.click(screen.getByRole('button', { name: 'Назад', hidden: true }));
      expect(onOpenChange).toHaveBeenCalledWith(false);
    } finally {
      window.innerWidth = wide;
    }
  });
});

/** Командная палитра — один вход в «сделать» и «перейти» (issue #1433). */
describe('командная палитра', () => {
  it('действие ведёт на экран, который его исполнит', () => {
    shown(clerk);
    type('новый счет');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices?do=invoice-new');
  });

  /**
   * Ревью PR #1478: на своём экране действие дописывается к адресу. Адрес без `invoice` закрыл бы
   * открытый счёт вместе с правками, не спросив, — а спросить обязана кнопка экрана.
   */
  it('на своём экране открытая запись остаётся в адресе', () => {
    shown(clerk, '/invoices?invoice=5');
    type('новый счет');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices?invoice=5&do=invoice-new');
  });

  it('название раздела ведёт в раздел, а не в действие со словом раздела', () => {
    shown(clerk);
    type('счета');
    expect(labels()[0]).toBe('Счета');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices');
  });

  it('без права кнопки действия нет — раздел остаётся', () => {
    shown(reader);
    type('счёт');
    expect(labels()).not.toContain('Новый счёт');
    expect(labels()).not.toContain('Счёт из файла');
    expect(labels()).toContain('Счета');
  });

  it('чип области сужает выдачу', () => {
    shown(clerk);
    fireEvent.click(screen.getByRole('button', { name: 'Разделы', hidden: true }));
    expect(labels()).toContain('Счета');
    expect(labels()).not.toContain('Новый счёт');
    expect(labels()).not.toContain('Выйти');

    fireEvent.click(screen.getByRole('button', { name: 'Действия', hidden: true }));
    expect(labels()).toContain('Новый счёт');
    expect(labels()).toContain('Тема: тёмная');
    expect(labels()).not.toContain('Счета');
  });

  /** Ревью PR #1478: после щелчка по чипу стрелки и Enter слушает снова поле. */
  it('чип возвращает фокус в поле', () => {
    shown(clerk);
    fireEvent.click(screen.getByRole('button', { name: 'Разделы', hidden: true }));
    expect(document.activeElement).toBe(search());
  });

  it('группа пункта слышна диктору, а подсказка — из доступного', () => {
    shown(reader);
    type('счета');
    const first = paletteOptions()[0];
    expect(first.querySelector('.sr-only')?.textContent).toBe(', Учёт затрат');
    // У читателя «Нового счёта» нет — и подсказка его не называет.
    expect(screen.getByText(/Подсказка/).textContent).not.toContain('новый счёт');
  });

  it('«тёмная тема» находит тему, совпадение подсвечено', () => {
    shown(clerk);
    type('тёмная тема');
    expect(labels()).toEqual(['Тема: тёмная']);
    expect([...document.querySelectorAll('mark')].map(m => m.textContent)).toEqual(['Тема', 'тёмная']);
  });
});

const group = (code: string, recordType: string, items: RecordSearchGroup['items'], extra: Partial<RecordSearchGroup> = {}) =>
  ({ code, title: code, module: 'core', recordType, items, more: false, ...extra });
const invoices = (extra: Partial<RecordSearchGroup> = {}) =>
  group('invoices', 'СчётНаОплату', [{ id: 'i-418', title: '№ 418 · ООО «Кабель-Торг»', detail: 'кабель', amount: 12500 }], extra);
const sites = group('constructions', 'Стройка', [{ id: 'c-1', title: 'Щитовая', detail: 'ЩТ-7' }]);
const found = (...groups: RecordSearchGroup[]) => { records = { groups, failed: false, pending: false, tooLong: false }; };
const chip = (name: string) => screen.getByRole('button', { name, hidden: true });
const builder: AccessInfo = { permissions: ['core.constructions.read'], modules: [] };
const keeper: AccessInfo = {
  permissions: ['core.constructions.read', 'costs.waybill.read', 'costs.waybill.edit'], modules: costs };

/** Палитра находит записи (shell-05, [UI-30]). */
describe('командная палитра — записи', () => {
  it('найденный счёт — строкой с суммой, Enter открывает его на экране счетов', () => {
    found(invoices());
    shown(reader);
    type('418');
    expect(labels()[0]).toBe('№ 418 · ООО «Кабель-Торг»');
    expect(paletteOptions()[0].textContent).toContain('12');
    expect(paletteOptions()[0].textContent).toContain('кабель');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices?invoice=i-418');
  });

  // Набор данных открывается на экране своего уровня (решение «А»); уровень, чей экран закрыт, — строки нет.
  it('набор данных — на панели своего уровня, а закрытый уровень не показан', () => {
    found(group('datasets', 'НаборДанных', [
      { id: 'd-1', title: 'Журнал корпуса', place: 'Корпус 1',
        level: { scope: 'Section', scopeId: 's-1', constructionId: 'c-1' } },
      { id: 'd-2', title: 'Журнал системный', level: { scope: 'System' } },
    ]));
    shown({ permissions: ['core.datasets.read', 'core.constructions.read'], modules: [] });
    type('журнал');
    expect(labels().slice(0, 2)).toEqual(['Журнал корпуса', 'Журнал системный']);
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/document-sets/c-1/sections/s-1/datasets?file=d-1');
  });

  it('набор стройки без права на стройки не показан — системный показан', () => {
    found(group('datasets', 'НаборДанных', [
      { id: 'd-1', title: 'Журнал корпуса', level: { scope: 'Construction', scopeId: 'c-1', constructionId: 'c-1' } },
      { id: 'd-2', title: 'Журнал системный', level: { scope: 'System' } },
    ]));
    shown({ permissions: ['core.datasets.read'], modules: [] });
    type('журнал');
    expect(labels()).not.toContain('Журнал корпуса');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/datasets?file=d-2');
  });

  it('подпись раздела выше совпадения в данных', () => {
    found(group('invoices', 'СчётНаОплату', [{ id: 'i-1', title: 'Счета за май' }]));
    shown(reader);
    type('счета');
    expect(labels()[0]).toBe('Счета');
    expect(labels()).toContain('Счета за май');
  });

  it('группа без права не показана — ни записями, ни чипом', () => {
    // Сервер такой группы не пришлёт; пришедшая всё равно не показывается: экран счетов закрыт.
    found(invoices(), sites);
    shown(builder);
    type('418');
    expect(labels()).not.toContain('№ 418 · ООО «Кабель-Торг»');
    expect(labels()).toContain('Щитовая');
    expect(screen.queryByRole('button', { name: 'Счета', hidden: true })).toBeNull();
    expect(chip('Стройки')).toBeTruthy();
  });

  // Группы «только карточки» (shell-09, Р-2 — Б): комплекты и шаблоны помнят «Недавние», а палитра их не ищет.
  it('чипов и групп «Комплекты» и «Шаблоны» нет даже с правами на них', () => {
    // Даже пришедшая с сервера группа не показывается: она не из палитры.
    found(sites, group('sets', 'КомплектДокументов', [{ id: 's-1', title: 'Комплект щитовая' }]));
    shown({ permissions: ['core.constructions.read', 'id.document.read', 'id.config.edit'], modules: [] });
    type('щитовая');
    expect(labels()).not.toContain('Комплект щитовая');
    expect(screen.queryByRole('button', { name: 'Комплекты', hidden: true })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Шаблоны', hidden: true })).toBeNull();
    expect(chip('Стройки')).toBeTruthy();
  });

  it('отказ поиска сказан словами, а действия и разделы ищутся', () => {
    records = { groups: undefined, failed: true, pending: false, tooLong: false };
    shown(reader);
    type('счета');
    expect(screen.getByRole('status', { name: 'Отказ поиска', hidden: true }).textContent).toContain('поиск не ответил');
    expect(labels()).toContain('Счета');
  });

  // Вставленный абзац сервер не ищет (400) — палитра говорит это, а не «Ничего не найдено».
  it('запрос длиннее предела — сказано, что записи не ищутся', () => {
    records = { groups: undefined, failed: false, pending: false, tooLong: true };
    shown(reader);
    type('щ'.repeat(201));
    expect(screen.getByRole('status', { name: 'Отказ поиска', hidden: true }).textContent)
      .toBe('Записи не ищутся: запрос длиннее 200 знаков.');
    expect(screen.queryByText('Ничего не найдено')).toBeNull();
  });

  it('отказ группы — у неё, а не «Ничего не найдено»', () => {
    found(invoices({ items: [], refused: 'нет ответа за 3 с — запрос можно повторить' }));
    shown(reader);
    type('zzz');
    expect(screen.getByRole('status', { name: 'Отказ поиска', hidden: true }).textContent)
      .toBe('Счета не найдены: нет ответа за 3 с — запрос можно повторить');
    expect(screen.queryByText('Ничего не найдено')).toBeNull();
  });

  it('пока ответ едет, «Ничего не найдено» не говорится', () => {
    records = { groups: undefined, failed: false, pending: true, tooLong: false };
    shown(reader);
    type('zzz');
    expect(screen.queryByText('Ничего не найдено')).toBeNull();
    expect(screen.getByText('Ищу записи…')).toBeTruthy();
  });

  it('стройка вне пространства помечена, и пометку слышит диктор', () => {
    access = builder;
    found(sites);
    function Outside({ children }: { children: ReactNode }) {
      return <WorkspaceContext.Provider value={{ ...useWorkspace(), inside: () => false }}>{children}</WorkspaceContext.Provider>;
    }
    render(
      <MemoryRouter><NavigationGuardProvider><Outside>
        <Address /><CommandPalette open onOpenChange={() => {}} />
      </Outside></NavigationGuardProvider></MemoryRouter>,
    );
    type('щит');
    const row = paletteOptions().find(o => o.textContent?.includes('Щитовая'))!;
    expect(row.querySelector('.sr-only')?.textContent).toBe(', Стройки — вне пространства');
    fireEvent.click(row);
    expect(address()).toBe('/document-sets/c-1');
  });

  it('«ещё» ведёт в раздел', () => {
    found(invoices({ more: true }));
    shown(reader);
    type('418');
    fireEvent.click(paletteOptions().find(o => o.textContent?.includes('Ещё в разделе «Счета»'))!);
    expect(address()).toBe('/invoices');
  });
});

/** Чипы области и Tab ([UI-31]). */
describe('командная палитра — область и Tab', () => {
  // Группу отбирает палитра из ответа «везде» — чип не новый запрос (ревью #1915); чип действий — без поиска.
  it('чип группы отбирает из ответа «везде», чип действий не ищет вовсе', () => {
    found(invoices(), sites);
    shown({ permissions: ['costs.invoice.read', 'core.constructions.read'], modules: costs });
    type('418');
    expect(asked).toHaveBeenLastCalledWith('418', true);
    fireEvent.click(chip('Счета'));
    expect(asked).toHaveBeenLastCalledWith('418', true);
    expect(labels()).toContain('№ 418 · ООО «Кабель-Торг»');
    expect(labels()).not.toContain('Щитовая');
    fireEvent.click(chip('Действия'));
    expect(asked).toHaveBeenLastCalledWith('418', false);
  });

  // Ни одной группы данных — по данным не искать: ни запроса, ни «Ищу записи…» (ревью #1915).
  it('без групп данных поиск записей выключен', () => {
    shown({ permissions: [], modules: [] });
    type('418');
    expect(asked).toHaveBeenLastCalledWith('418', false);
  });

  it('Tab на экране счетов выбирает «Счета», повторный — «Везде»; подвал говорит об этом заранее', () => {
    shown(reader, '/invoices');
    expect(screen.getByText(/искать только в счетах/)).toBeTruthy();
    type('418');
    fireEvent.keyDown(search(), { key: 'Tab' });
    expect(chip('Счета').getAttribute('aria-pressed')).toBe('true');
    expect(document.activeElement).toBe(search());
    fireEvent.keyDown(search(), { key: 'Tab' });
    expect(chip('Везде').getAttribute('aria-pressed')).toBe('true');
  });

  it('Tab на «Сверке» область не меняет, и строки о Tab нет', () => {
    shown({ permissions: ['costs.invoice.read', 'core.reconciliation.run'], modules: costs }, '/reconciliations');
    expect(screen.queryByText(/искать только в/)).toBeNull();
    expect(fireEvent.keyDown(search(), { key: 'Tab' })).toBe(false);
    expect(chip('Везде').getAttribute('aria-pressed')).toBe('true');
    expect(document.activeElement).toBe(search());
  });
});

/** Действие с найденной записью ([UI-32]). */
describe('командная палитра — действие с записью', () => {
  // Enter открывает запись ([UI-30]): действие с ней стоит за ней, а не заводит черновик первым (ревью #1915).
  it('у найденной стройки — «Новая накладная на стройку» первой в «Действиях», но за самой стройкой', () => {
    found(sites);
    shown(keeper);
    type('щит');
    expect(labels().slice(0, 2)).toEqual(['Щитовая', 'Новая накладная на стройку «Щитовая»']);
    fireEvent.keyDown(search(), { key: 'ArrowDown' });
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/waybills?do=waybill-new&record=c-1');
  });

  it('Enter без стрелок открывает найденную стройку', () => {
    found(sites);
    shown(keeper);
    type('щит');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/document-sets/c-1');
  });

  // Выделение — пунктом: ответ, вставший в выдачу, не подменяет выбранное стрелкой (ревью #1915).
  it('пришедший ответ не сдвигает выделенное стрелкой', () => {
    found(sites);
    const view = shown(keeper);
    type('щит');
    fireEvent.keyDown(search(), { key: 'ArrowDown' });
    const chosen = labels()[1];
    // Пришла вторая стройка: она встаёт в выдаче на место, где было выделенное.
    found(group('constructions', 'Стройка', [{ id: 'c-1', title: 'Щитовая' }, { id: 'c-2', title: 'Щит-2' }]));
    view.rerender(
      <MemoryRouter initialEntries={['/']}>
        <NavigationGuardProvider>
          <Address />
          <CommandPalette open onOpenChange={() => {}} />
        </NavigationGuardProvider>
      </MemoryRouter>,
    );
    expect(labels()[1]).toBe('Щит-2');
    const active = document.getElementById(search().getAttribute('aria-activedescendant')!);
    expect(chosen).toBe('Новая накладная на стройку «Щитовая»');
    expect(active?.textContent).toContain(chosen);
  });

  it('без права кнопки действия нет, стройка найдена', () => {
    found(sites);
    shown({ ...keeper, permissions: ['core.constructions.read', 'costs.waybill.read'] });
    type('щит');
    expect(labels()).toEqual(['Щитовая']);
  });
});

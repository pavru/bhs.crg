// @vitest-environment jsdom
import { useState, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import type { FieldSuggestion, SuggestSource } from '../api/fieldSuggestions';
import { EditGrid } from './EditGrid';
import { Modal } from './Modal';
import { SuggestField } from './SuggestField';
import { matchStart } from './suggestMatch';
import { resetHistoryLayers } from './historyLayers';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

vi.mock('../api/client', () => ({ apiClient: { get: vi.fn() } }));

const SOURCE: SuggestSource = { type: 't1', field: 'mark', scope: 'Set', scopeId: 's1' };
const ITEMS: FieldSuggestion[] = [
  { value: 'Кабель ВВГнг 3×2,5', here: true },
  { value: 'Кабель ВВГнг 5×4', here: true },
  { value: 'Кабельный лоток', here: false },
];

function answer(items: FieldSuggestion[]) {
  vi.mocked(apiClient.get).mockResolvedValue({ data: { items } });
}

let changes: string[] = [];

function Field({ label = 'Марка', compact = false, arrowOpens, initial = '', source = SOURCE }: {
  label?: string; compact?: boolean; arrowOpens?: boolean; initial?: string; source?: SuggestSource;
}) {
  const [value, setValue] = useState(initial);
  return (
    <SuggestField label={label} source={source} compact={compact} arrowOpens={arrowOpens} value={value}
      onChange={v => { changes.push(v); setValue(v); }} />
  );
}

function wrap(children: ReactNode) {
  const client = new QueryClient();
  return render(<QueryClientProvider client={client}>{children}</QueryClientProvider>);
}

const box = (name = 'Марка') => screen.getByRole('combobox', { name }) as HTMLInputElement;
// В jsdom у якоря нулевой прямоугольник, и `hideWhenDetached` прячет слой (visibility: hidden) —
// поэтому пункты ищутся и среди скрытых: проверяется состав и разметка, а не раскладка.
const list = () => screen.queryByRole('listbox', { hidden: true });
const options = () => screen.getAllByRole('option', { hidden: true });
// Имя у скрытого узла не вычисляется — пункт ищется по тексту.
const option = (text: string) => options().find(o => o.textContent === text)!;
/** Набор по клавише, быстрее задержки: каждое событие — новое значение поля. */
const typeFast = (input: HTMLInputElement, text: string) => {
  for (let i = 1; i <= text.length; i++) fireEvent.change(input, { target: { value: text.slice(0, i) } });
};
const opened = () => waitFor(() => expect(list()).not.toBeNull());
const pause = (ms: number) => act(() => new Promise(r => setTimeout(r, ms)));

beforeEach(() => { changes = []; });
afterEach(() => { cleanup(); vi.resetAllMocks(); });

describe('SuggestField: когда зовётся адрес (UI-1, UI-5)', () => {
  it('фокус без ввода запроса не шлёт и списка не открывает', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    await pause(300);
    expect(apiClient.get).not.toHaveBeenCalled();
    expect(box().getAttribute('aria-expanded')).toBe('false');
  });

  it('три быстрые клавиши — один запрос, с набранным целиком', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    expect(apiClient.get).toHaveBeenCalledTimes(1);
    expect(vi.mocked(apiClient.get).mock.calls[0][1]?.params).toMatchObject({ q: 'Каб', type: 't1', field: 'mark' });
  });

  it('в форме ↓ открывает список и в пустом поле — верх по полю', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    await opened();
    expect(vi.mocked(apiClient.get).mock.calls[0][1]?.params).toMatchObject({ q: '' });
  });

  it('в ячейке ↓ при закрытом списке остаётся сетке, Alt+↓ открывает', async () => {
    answer(ITEMS);
    wrap(<Field compact />);
    box().focus();
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    await pause(250);
    expect(apiClient.get).not.toHaveBeenCalled();
    fireEvent.keyDown(box(), { key: 'ArrowDown', altKey: true });
    await opened();
  });

  it('голое поле формы (compact + arrowOpens) — ↓ открывает, как у поля с подписью', async () => {
    answer(ITEMS);
    wrap(<Field compact arrowOpens />);
    box().focus();
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    await opened();
  });

  it('без источника — обычное поле: не combobox и адрес не зовётся', async () => {
    function Plain() {
      const [value, setValue] = useState('');
      return <SuggestField label="Номер" source={null} value={value} onChange={setValue} />;
    }
    wrap(<Plain />);
    const input = screen.getByLabelText('Номер');
    expect(input.getAttribute('role')).toBeNull();
    typeFast(input as HTMLInputElement, 'Каб');
    await pause(250);
    expect(apiClient.get).not.toHaveBeenCalled();
  });
});

describe('SuggestField: принятие только явное (UI-2)', () => {
  it('до стрелки ни один пункт не активен; листание не зовёт onChange; Enter — один onChange', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    expect(box().getAttribute('aria-activedescendant')).toBeNull();
    expect(options().some(o => o.getAttribute('aria-selected') === 'true')).toBe(false);

    const typed = changes.length;
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    fireEvent.keyDown(box(), { key: 'ArrowUp' });
    expect(changes.length).toBe(typed);
    expect(box().value).toBe('Каб');
    const active = screen.getByRole('option', { selected: true, hidden: true });
    expect(box().getAttribute('aria-activedescendant')).toBe(active.id);
    expect(active.textContent).toBe('Кабель ВВГнг 3×2,5');

    const enter = fireEvent.keyDown(box(), { key: 'Enter' });
    expect(enter).toBe(false); // preventDefault: форма не отправляется
    expect(changes.slice(typed)).toEqual(['Кабель ВВГнг 3×2,5']);
    expect(box().value).toBe('Кабель ВВГнг 3×2,5');
    await waitFor(() => expect(list()).toBeNull());
  });

  it('↑ с первого пункта — снова ни один, и Enter ничего не подставляет', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    fireEvent.keyDown(box(), { key: 'ArrowUp' });
    expect(box().getAttribute('aria-activedescendant')).toBeNull();
    const typed = changes.length;
    expect(fireEvent.keyDown(box(), { key: 'Enter' })).toBe(true);
    expect(changes.length).toBe(typed);
  });

  it('«Каб» → Tab оставляет «Каб» и уводит фокус дальше', async () => {
    answer(ITEMS);
    wrap(<><Field /><input aria-label="Следующее" /></>);
    const user = userEvent.setup();
    await user.click(box());
    await user.keyboard('Каб');
    await opened();
    const typed = changes.length;
    await user.tab();
    expect(box().value).toBe('Каб');
    expect(changes.length).toBe(typed);
    expect(document.activeElement).toBe(screen.getByLabelText('Следующее'));
    await waitFor(() => expect(list()).toBeNull());
  });

  it('щелчок по пункту принимает его одним onChange, фокус остаётся в поле', async () => {
    answer(ITEMS);
    wrap(<Field />);
    const user = userEvent.setup();
    await user.click(box());
    await user.keyboard('Каб');
    await opened();
    const typed = changes.length;
    // Не user.click: у скрытого jsdom-ом слоя `pointer-events: none` (см. `list`). Проверяется сам
    // механизм — нажатие гасится (фокус не уходит из поля), щелчок принимает.
    expect(fireEvent.mouseDown(option('Кабельный лоток'))).toBe(false);
    fireEvent.click(option('Кабельный лоток'));
    expect(changes.slice(typed)).toEqual(['Кабельный лоток']);
    expect(document.activeElement).toBe(box());
  });

  it('в окне первый Esc закрывает список, второй — окно', async () => {
    answer(ITEMS);
    const onOpenChange = vi.fn();
    wrap(<Modal open onOpenChange={onOpenChange} title="Окно" isDirty={false}><Field /></Modal>);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();

    fireEvent.keyDown(box(), { key: 'Escape' });
    await pause(0);
    await waitFor(() => expect(list()).toBeNull());
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(box().value).toBe('Каб');

    fireEvent.keyDown(box(), { key: 'Escape' });
    await pause(0);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('щелчок по пункту не закрывает окно', async () => {
    answer(ITEMS);
    const onOpenChange = vi.fn();
    wrap(<Modal open onOpenChange={onOpenChange} title="Окно"><Field /></Modal>);
    const user = userEvent.setup();
    await user.click(box());
    await user.keyboard('Каб');
    await opened();
    await user.click(option('Кабельный лоток'));
    expect(box().value).toBe('Кабельный лоток');
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});

describe('SuggestField: тишина вместо пустоты и отказа (UI-3)', () => {
  it('пустой ответ — списка нет и надписи нет', async () => {
    answer([]);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Зз');
    await waitFor(() => expect(apiClient.get).toHaveBeenCalled());
    await pause(20);
    expect(list()).toBeNull();
    expect(box().getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByText(/не найдено/i)).toBeNull();
  });

  it.each([503, 403])('отказ %i — список не открыт, набранное цело, ввод продолжается', async status => {
    vi.mocked(apiClient.get).mockRejectedValue(Object.assign(new Error(String(status)), { response: { status } }));
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await waitFor(() => expect(apiClient.get).toHaveBeenCalled());
    await pause(20);
    expect(list()).toBeNull();
    expect(box().value).toBe('Каб');
    fireEvent.change(box(), { target: { value: 'Кабе' } });
    expect(box().value).toBe('Кабе');
  });
});

describe('SuggestField: вид (UI-4)', () => {
  it('обе группы — свои выше разделителя «С других строек», чужие ниже', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    const order = [...list()!.children].map(n => n.getAttribute('role') === 'option' ? n.textContent : `|${n.textContent}|`);
    expect(order).toEqual(['Кабель ВВГнг 3×2,5', 'Кабель ВВГнг 5×4', '|С других строек|', 'Кабельный лоток']);
  });

  it('только чужие значения — разделителя нет', async () => {
    answer(ITEMS.map(i => ({ ...i, here: false })));
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    expect(screen.queryByText('С других строек')).toBeNull();
  });

  it('совпадение — жирным, с начала слова, без регистра и с ё=е', async () => {
    answer([{ value: 'Провод ПуГВ', here: true }]);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'пуг');
    await opened();
    expect(options()[0].querySelector('.font-semibold')?.textContent).toBe('ПуГ');

    expect(matchStart('Ёлка', 'ел')).toBe(0);
    expect(matchStart('Кабель-канал', 'кан')).toBe(7);
    expect(matchStart('Распаечная', 'паеч')).toBe(-1); // середина слова — не совпадение
  });
});

describe('SuggestField: доступность (UI-6)', () => {
  it('два поля на форме — разные списки в aria-controls; закрытый — aria-expanded="false"', () => {
    wrap(<><Field label="Марка" /><Field label="Трасса" /></>);
    const a = box('Марка');
    const b = box('Трасса');
    expect(a.getAttribute('aria-controls')).toBeTruthy();
    expect(a.getAttribute('aria-controls')).not.toBe(b.getAttribute('aria-controls'));
    expect(a.getAttribute('aria-expanded')).toBe('false');
    expect(a.getAttribute('aria-activedescendant')).toBeNull();
    expect(a.getAttribute('aria-autocomplete')).toBe('list');
    expect(a.getAttribute('autocomplete')).toBe('off');
  });

  it('список — listbox с именем поля; число подсказок объявляется при открытии', async () => {
    answer(ITEMS);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    expect(list()!.id).toBe(box().getAttribute('aria-controls'));
    expect(list()!.getAttribute('aria-label')).toBe('Введённое ранее: Марка');
    expect(document.querySelector('[aria-live="polite"]')?.textContent).toBe('Подсказок: 3');
  });
});

describe('SuggestField: телефон (UI-7)', () => {
  beforeEach(() => {
    resetHistoryLayers();
    window.matchMedia = vi.fn().mockImplementation((q: string) => ({
      matches: !q.includes('min-width'), addEventListener() {}, removeEventListener() {},
    })) as unknown as typeof window.matchMedia;
    window.history.replaceState({ idx: 0, key: 'base' }, '');
  });

  it('открытый список записи истории не кладёт; «Назад» закрывает окно, а не список', async () => {
    answer(ITEMS);
    const onOpenChange = vi.fn();
    wrap(<Modal open onOpenChange={onOpenChange} title="Окно" isDirty={false}><Field /></Modal>);
    await pause(30);
    const withWindow = window.history.state;

    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    await pause(30);
    expect(window.history.state).toEqual(withWindow);

    window.history.back();
    await pause(30);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});

describe('SuggestField в сетке правки (UI-8)', () => {
  it('↓ при закрытом списке — строка ниже; после ввода ↓ листает; Esc закрывает; ↓ — снова строкой', async () => {
    answer(ITEMS);
    const rows = [{ k: 'a' }, { k: 'b' }, { k: 'c' }];
    wrap(<EditGrid rows={rows} rowKey={r => r.k}
      columns={[{ key: 'mark', label: 'Марка', cell: (_, i) => <Field compact label={`Марка, строка ${i + 1}`} /> }]} />);
    box('Марка, строка 1').focus();
    fireEvent.keyDown(box('Марка, строка 1'), { key: 'ArrowDown' });
    expect(document.activeElement).toBe(box('Марка, строка 2'));

    const second = box('Марка, строка 2');
    typeFast(second, 'Каб');
    await opened();
    fireEvent.keyDown(second, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(second);
    expect(screen.getByRole('option', { selected: true, hidden: true }).textContent).toBe('Кабель ВВГнг 3×2,5');

    fireEvent.keyDown(second, { key: 'Escape' });
    await waitFor(() => expect(list()).toBeNull());
    fireEvent.keyDown(second, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(box('Марка, строка 3'));
  });
});

describe('SuggestField: прошлый ответ не переживает своё открытие (ревью #1587)', () => {
  /** Ответ по набранному; набор из `pending` не отвечает вовсе — «ответ ещё едет». */
  function answerBy(byQ: Record<string, FieldSuggestion[]>, pending: string[] = []) {
    vi.mocked(apiClient.get).mockImplementation((_url, config) => {
      const q = (config?.params as { q: string }).q;
      return pending.includes(q) ? new Promise(() => {}) : Promise.resolve({ data: { items: byQ[q] ?? [] } });
    });
  }
  const asked = () => vi.mocked(apiClient.get).mock.calls.map(c => (c[1]?.params as { q: string }).q);
  const ROADS: FieldSuggestion[] = [{ value: 'Рабочая документация', here: true }];

  it('открытое заново поле не спрашивает о прошлом наборе и не показывает его ответ', async () => {
    answerBy({ Каб: ITEMS, Р: ROADS });
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    fireEvent.keyDown(box(), { key: 'Escape' });
    await waitFor(() => expect(list()).toBeNull());

    fireEvent.change(box(), { target: { value: 'Р' } });
    await pause(50);
    expect(list()).toBeNull(); // до задержки — ни старого списка из кэша, ни запроса о «Каб»
    await opened();
    expect(asked()).toEqual(['Каб', 'Р']);
    expect(options().map(o => o.textContent)).toEqual(['Рабочая документация']);
  });

  it('дописанный текст снимает выбор стрелкой: Enter не подставляет пункт прежнего списка', async () => {
    answerBy({ Каб: ITEMS }, ['Кабельн']);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    expect(box().getAttribute('aria-activedescendant')).not.toBeNull();

    fireEvent.change(box(), { target: { value: 'Кабельн' } });
    await pause(250);
    expect(list()).not.toBeNull(); // ответ ещё едет — держится прошлый список
    expect(box().getAttribute('aria-activedescendant')).toBeNull();
    const typed = changes.length;
    expect(fireEvent.keyDown(box(), { key: 'Enter' })).toBe(true);
    expect(changes.length).toBe(typed);
    expect(box().value).toBe('Кабельн');
  });

  it('пока едет новый ответ, подсветка — по набору показанного списка', async () => {
    answerBy({ Каб: ITEMS }, ['Кабе']);
    wrap(<Field />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();
    fireEvent.change(box(), { target: { value: 'Кабе' } });
    await pause(250);
    expect(asked()).toEqual(['Каб', 'Кабе']);
    expect(options().map(o => o.querySelector('.font-semibold')?.textContent)).toEqual(['Каб', 'Каб', 'Каб']);
  });

  it('другое поле в той же клетке не показывает значений прежнего', async () => {
    const other: SuggestSource = { ...SOURCE, field: 'route' };
    answerBy({ Каб: ITEMS }, ['Л']);
    const view = wrap(<Field source={SOURCE} />);
    box().focus();
    typeFast(box(), 'Каб');
    await opened();

    view.rerender(<QueryClientProvider client={new QueryClient()}><Field source={other} /></QueryClientProvider>);
    box().focus();
    fireEvent.change(box(), { target: { value: 'Л' } });
    await pause(250);
    expect(list()).toBeNull();
  });
});

describe('SuggestField: разметка вокруг поля (ревью #1587)', () => {
  it('живая область — внутри оболочки поля: в стопке экрана поле остаётся единственным', () => {
    wrap(<div data-testid="stack"><Field /></div>);
    const stack = screen.getByTestId('stack');
    expect(stack.children.length).toBe(1);
    expect(stack.querySelector('[aria-live="polite"]')).not.toBeNull();
  });

  it('поле без источника обходится без QueryClientProvider', () => {
    function Plain() {
      const [value, setValue] = useState('');
      return <SuggestField label="Номер" source={null} value={value} onChange={setValue} />;
    }
    expect(() => render(<Plain />)).not.toThrow();
    expect(screen.getByLabelText('Номер')).toBeTruthy();
  });
});

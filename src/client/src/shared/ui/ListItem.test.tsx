// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createRef } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { LIST_CONTAINER, ListItem } from './ListItem';
import { ListGroupHeader } from './ListGroupHeader';
import { Description } from './icons';

/** Строка списка list-detail и заголовок сворачиваемой группы ([UI-84], sharedui-md3-18). */

afterEach(cleanup);

// Охрана ухода по ссылке — подменена: проверяется, что щелчок до неё доходит.
const guard = vi.hoisted(() => vi.fn());
vi.mock('./NavigationGuard', () => ({ useGuardedNavClick: () => () => guard }));

const classes = (el: Element) => el.className.split(/\s+/);

describe('ListItem', () => {
  it('высота — минимумом: 48 без второй строки, 64 со второй; подпись body-large, вторая — body-medium', () => {
    render(<>
      <ListItem data-testid="one" headline="Акт" onClick={() => {}} />
      <ListItem data-testid="two" headline="Строка" supporting="string" onClick={() => {}} />
    </>);
    expect(classes(screen.getByTestId('one'))).toContain('min-h-12');
    const two = screen.getByTestId('two');
    expect(classes(two)).toContain('min-h-16');
    expect(two.className).not.toMatch(/(^|\s)h-\d/);
    expect(screen.getByText('Акт').className).toContain('text-body-large');
    expect(screen.getByText('string').className).toContain('text-body-medium');
  });

  it('выбранная — secondary-container и aria-current; прочие — без aria-current и без заливки', () => {
    render(<>
      <ListItem data-testid="sel" headline="АОСР" selected onClick={() => {}} />
      <ListItem data-testid="plain" headline="Журнал" onClick={() => {}} />
    </>);
    const sel = screen.getByTestId('sel');
    expect(sel.getAttribute('aria-current')).toBe('true');
    expect(classes(sel.parentElement!)).toEqual(expect.arrayContaining(['bg-secondary-container', 'text-on-secondary-container']));
    const plain = screen.getByTestId('plain');
    expect(plain.hasAttribute('aria-current')).toBe(false);
    expect(plain.parentElement!.className).not.toContain('bg-');
    expect(plain.parentElement!.className).not.toContain('primary-container');
  });

  it('действие и флажок — соседи цели, а не внутри кнопки; нажатие на действие не нажимает строку', () => {
    const onClick = vi.fn();
    const onAction = vi.fn();
    render(<ListItem data-testid="row" headline="Шаблон" onClick={onClick}
      leadingAction={<input type="checkbox" aria-label="Участвует" />}
      action={<button type="button" onClick={onAction}>Действия</button>} />);
    const target = screen.getByTestId('row');
    const action = screen.getByRole('button', { name: 'Действия' });
    expect(target.contains(action)).toBe(false);
    expect(target.contains(screen.getByRole('checkbox'))).toBe(false);
    expect(action.parentElement!.parentElement).toBe(target.parentElement);
    fireEvent.click(action);
    expect(onAction).toHaveBeenCalledOnce();
    expect(onClick).not.toHaveBeenCalled();
    fireEvent.click(target);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('слой и кольцо — у строки (state-layer-row), цель помечена data-li-target и без своего кольца', () => {
    render(<ListItem data-testid="row" headline="Роль" onClick={() => {}} />);
    const target = screen.getByTestId('row');
    expect(target.hasAttribute('data-li-target')).toBe(true);
    expect(classes(target.parentElement!)).toEqual(expect.arrayContaining(['state-layer', 'state-layer-row']));
    expect(target.className).not.toContain('state-layer');
    expect(target.className).not.toContain('focus-ring');
  });

  it('полная подпись — в title, и только у обрезанной: меряется на наведении', () => {
    render(<>
      <ListItem headline="Запись реестра документов (протоколы измерений)" onClick={() => {}} />
      <ListItem headline={<b>Счёт 12</b>} title="Счёт 12 от 01.11.2026" onClick={() => {}} />
      <ListItem headline="Каталог" onClick={() => {}} />
    </>);
    const cut = (el: HTMLElement, scroll: number, client: number) => {
      Object.defineProperty(el, 'scrollWidth', { configurable: true, value: scroll });
      Object.defineProperty(el, 'clientWidth', { configurable: true, value: client });
      fireEvent.pointerEnter(el);
      return el;
    };
    const long = screen.getByText('Запись реестра документов (протоколы измерений)');
    expect(long.hasAttribute('title')).toBe(false);
    expect(cut(long, 420, 200).getAttribute('title')).toBe('Запись реестра документов (протоколы измерений)');
    expect(cut(screen.getByText('Счёт 12').parentElement!, 300, 200).getAttribute('title')).toBe('Счёт 12 от 01.11.2026');
    expect(cut(screen.getByText('Каталог'), 80, 200).hasAttribute('title')).toBe(false);
    // Окно шире стало — подсказка снимается.
    expect(cut(long, 420, 480).hasAttribute('title')).toBe(false);
  });

  it('вторая строка 0 — строка: и рисуется, и даёт 64; пустая — ни того, ни другого', () => {
    render(<>
      <ListItem data-testid="zero" headline="a" supporting={0} onClick={() => {}} />
      <ListItem data-testid="empty" headline="b" supporting="" onClick={() => {}} />
    </>);
    expect(classes(screen.getByTestId('zero'))).toContain('min-h-16');
    expect(screen.getByTestId('zero').textContent).toContain('0');
    expect(classes(screen.getByTestId('empty'))).toContain('min-h-12');
    expect(screen.getByTestId('empty').querySelectorAll('.text-body-medium')).toHaveLength(0);
  });

  it('ref и data-testid — на цели; to — ссылка', () => {
    const ref = createRef<HTMLElement>();
    render(<MemoryRouter><ListItem ref={ref} data-testid="invoice-row" headline="Счёт" to="/costs/invoices/1" icon={Description} /></MemoryRouter>);
    const target = screen.getByTestId('invoice-row');
    expect(ref.current).toBe(target);
    expect(target.tagName).toBe('A');
    expect(target.getAttribute('href')).toBe('/costs/invoices/1');
    expect(target.querySelector('svg')!.getAttribute('width')).toBe('24');
  });

  it('trailing false и пустая строка — слота нет (cond && … у вызывающих), 0 — есть', () => {
    render(<>
      <ListItem data-testid="f" headline="a" trailing={false} onClick={() => {}} />
      <ListItem data-testid="e" headline="b" trailing="" onClick={() => {}} />
      <ListItem data-testid="z" headline="c" trailing={0} onClick={() => {}} />
    </>);
    expect(screen.getByTestId('f').querySelectorAll('.text-label-small')).toHaveLength(0);
    expect(screen.getByTestId('e').querySelectorAll('.text-label-small')).toHaveLength(0);
    expect(screen.getByTestId('z').querySelector('.text-label-small')!.textContent).toBe('0');
  });

  it('узкий список (LIST_CONTAINER) уводит замыкающий слот под пояснение: решает ширина списка, не экрана', () => {
    expect(LIST_CONTAINER).toBe('@container/list');
    render(<ListItem data-testid="r" headline="Кабель" supporting="слева: строка 3" trailing="120 / 118"
      onClick={() => {}} action={<button type="button">Разобрать</button>} />);
    const slot = screen.getByText('120 / 118');
    const text = screen.getByText('Кабель').parentElement!;
    const pair = slot.parentElement!;
    // Подпись и слот — соседи одной обёртки; действие строки — вне её, у края всегда.
    expect(text.parentElement).toBe(pair);
    expect(pair.className).toContain('@max-md/list:flex-wrap');
    expect(text.className).toContain('@max-md/list:basis-full');
    expect(pair.contains(screen.getByRole('button', { name: 'Разобрать' }))).toBe(false);
    // Варианты — по именованному контейнеру: без списка с LIST_CONTAINER слот всегда справа.
    expect(pair.className).not.toMatch(/(^|\s)(sm|md|max-sm|max-md):/);
  });

  it('hint — подсказка цели: описание у цели и показ по фокусу с клавиатуры', () => {
    render(<ListItem data-testid="row" headline="17" hint="Счёт оплачен и из очереди ушёл" onClick={() => {}} />);
    const target = screen.getByTestId('row');
    expect(target.getAttribute('aria-description')).toBe('Счёт оплачен и из очереди ушёл');
    expect(target.hasAttribute('data-li-target')).toBe(true);
  });

  it('hint у ссылки не снимает охрану перехода: щелчок подсказки и охрана — оба', () => {
    guard.mockClear();
    render(<MemoryRouter><ListItem data-testid="row" headline="Счёт" to="/costs/invoices/1" hint="Подсказка" /></MemoryRouter>);
    fireEvent.click(screen.getByTestId('row'));
    expect(guard).toHaveBeenCalledOnce();
  });

  it('iconTone — цвет знака и на выбранной; без него знак тоном строки', () => {
    render(<>
      <ListItem data-testid="acc" headline="a" icon={Description} iconTone="text-error" selected onClick={() => {}} />
      <ListItem data-testid="q" headline="b" icon={Description} selected onClick={() => {}} />
    </>);
    expect(screen.getByTestId('acc').querySelector('svg')!.getAttribute('class')).toContain('text-error');
    expect(screen.getByTestId('q').querySelector('svg')!.getAttribute('class')).toContain('text-on-secondary-container');
  });

  it('соседи цели помечены data-li-side — их наведение гасит слой строки (state-layer-row)', () => {
    render(<ListItem data-testid="row" headline="Шаблон" onClick={() => {}}
      leadingAction={<input type="checkbox" aria-label="Участвует" />} action={<button type="button">⋮</button>} />);
    const sides = screen.getByTestId('row').parentElement!.querySelectorAll(':scope > [data-li-side]');
    expect(sides).toHaveLength(2);
  });

  it('depth — 16 на уровень', () => {
    render(<>
      <ListItem data-testid="d0" headline="a" onClick={() => {}} />
      <ListItem data-testid="d2" headline="b" depth={2} onClick={() => {}} />
    </>);
    expect(classes(screen.getByTestId('d0'))).toContain('pl-4');
    expect(classes(screen.getByTestId('d2'))).toContain('pl-12');
  });
});

describe('ListGroupHeader', () => {
  it('title-small, aria-expanded по open; выбранным не бывает', () => {
    const onToggle = vi.fn();
    const { rerender } = render(<ListGroupHeader label="Акты" count={4} open onToggle={onToggle} />);
    const head = screen.getByRole('button', { name: /Акты/ });
    expect(head.getAttribute('aria-expanded')).toBe('true');
    expect(head.className).toContain('text-title-small');
    expect(head.hasAttribute('aria-current')).toBe(false);
    expect(head.parentElement!.className).not.toContain('container');
    fireEvent.click(head);
    expect(onToggle).toHaveBeenCalledOnce();
    rerender(<ListGroupHeader label="Акты" count={4} open={false} onToggle={onToggle} />);
    expect(screen.getByRole('button', { name: /Акты/ }).getAttribute('aria-expanded')).toBe('false');
  });

  it('действие группы — сосед заголовка', () => {
    render(<ListGroupHeader label="АОСР — основной" open onToggle={() => {}} action={<button type="button">Действия шаблона</button>} />);
    const head = screen.getByRole('button', { name: /АОСР/ });
    expect(head.contains(screen.getByRole('button', { name: 'Действия шаблона' }))).toBe(false);
  });
});

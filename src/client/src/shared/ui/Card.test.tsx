// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { Card } from './Card';
import { Divider } from './Divider';

/** Карточка и разделитель MD3 ([UI-77], sharedui-md3-13 PR 2б). */

afterEach(cleanup);

const classes = (el: Element) => el.className.split(/\s+/);

describe('Card', () => {
  it('вид по variant: outlined — обводка border-outline-variant без тени, filled — контейнер, elevated — уровень 1', () => {
    render(<>
      <Card data-testid="o" title="Шаг 1">тело</Card>
      <Card data-testid="f" variant="filled" title="Итоги">тело</Card>
      <Card data-testid="e" variant="elevated" title="Смета">тело</Card>
    </>);
    const o = classes(screen.getByTestId('o'));
    expect(o).toEqual(expect.arrayContaining(['rounded-md', 'bg-surface', 'border', 'border-outline-variant']));
    expect(o.some(c => c.startsWith('shadow-'))).toBe(false);
    const f = classes(screen.getByTestId('f'));
    expect(f).toContain('bg-surface-container-highest');
    expect(f).not.toContain('border');
    expect(classes(screen.getByTestId('e'))).toEqual(expect.arrayContaining(['bg-surface-container-low', 'shadow-elevation-1']));
  });

  it('шапка — заголовок title-medium, меню у замыкающего края; действия — под телом', () => {
    render(<Card title="Смета" menu={<button type="button">Действия</button>} actions={<button type="button">Открыть</button>}>тело</Card>);
    expect(screen.getByRole('heading', { name: 'Смета' }).className).toContain('text-title-medium');
    expect(screen.getByRole('button', { name: 'Действия' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Открыть' })).toBeTruthy();
  });

  it('обычная карточка — без цели; кликабельная — цель поверх карточки со слоем, возвышение и кольцо у карточки', () => {
    const onClick = vi.fn();
    render(<MemoryRouter>
      <Card data-testid="plain" title="Стройка">тело</Card>
      <Card data-testid="btn" title="Участок" onClick={onClick}>тело</Card>
      <Card data-testid="link" variant="elevated" title="Смета" to="/estimates/1">тело</Card>
    </MemoryRouter>);
    expect(screen.getByTestId('plain').querySelector('button, a')).toBeNull();

    const card = screen.getByTestId('btn');
    expect(classes(card)).toEqual(expect.arrayContaining(['hover:shadow-elevation-1', 'has-[:focus-visible]:focus-ring-mark']));
    // Заголовок — не внутри кнопки: читалка видит его заголовком, а кнопку зовёт по нему.
    const btn = screen.getByRole('button', { name: 'Участок' });
    expect(btn.contains(screen.getByRole('heading', { name: 'Участок' }))).toBe(false);
    expect(classes(btn)).toEqual(expect.arrayContaining(['absolute', 'inset-0', 'state-layer']));
    fireEvent.click(btn);
    expect(onClick).toHaveBeenCalledTimes(1);

    const link = screen.getByRole('link', { name: 'Смета' });
    expect(link.getAttribute('href')).toBe('/estimates/1');
    expect(classes(screen.getByTestId('link'))).toContain('hover:shadow-elevation-2');
  });

  it('состояния ролью: тянут — слой 16 % по data-dragging и уровень выше; цель — обводка 2 px primary; ошибка — error', () => {
    render(<>
      <Card data-testid="calm" title="Поле">тело</Card>
      <Card data-testid="drag" title="Поле" dragged>тело</Card>
      <Card data-testid="drag-e" variant="elevated" title="Поле" dragged>тело</Card>
      <Card data-testid="drop" title="Группа" dropTarget>тело</Card>
      <Card data-testid="bad" variant="filled" title="Движок" invalid>тело</Card>
    </>);
    const calm = screen.getByTestId('calm');
    expect(calm.hasAttribute('data-dragging')).toBe(false);
    expect(classes(calm)).not.toContain('state-layer');

    const drag = screen.getByTestId('drag');
    expect(drag.hasAttribute('data-dragging')).toBe(true);
    expect(classes(drag)).toEqual(expect.arrayContaining(['state-layer', 'shadow-elevation-3']));
    const dragE = classes(screen.getByTestId('drag-e'));
    expect(dragE).toContain('shadow-elevation-4');
    expect(dragE).not.toContain('shadow-elevation-1');

    expect(classes(screen.getByTestId('drop'))).toEqual(expect.arrayContaining(['outline-2', 'outline-primary']));

    // У filled своей обводки нет — ошибка её получает; обычная обводка outline-variant уходит.
    const bad = classes(screen.getByTestId('bad'));
    expect(bad).toEqual(expect.arrayContaining(['border', 'border-error']));
    expect(bad).not.toContain('border-outline-variant');
  });

  it('события перетаскивания — у корня карточки; flush — тело без отступов', () => {
    const onDragOver = vi.fn();
    const onDrop = vi.fn();
    const onDragStart = vi.fn();
    render(<>
      <Card data-testid="dnd" draggable onDragStart={onDragStart} onDragOver={onDragOver} onDrop={onDrop} flush>
        <div data-testid="row">строка до краёв</div>
      </Card>
      <Card data-testid="padded">тело</Card>
    </>);
    const card = screen.getByTestId('dnd');
    expect(card.getAttribute('draggable')).toBe('true');
    fireEvent.dragStart(card);
    fireEvent.dragOver(card);
    fireEvent.drop(card);
    expect([onDragStart, onDragOver, onDrop].map(f => f.mock.calls.length)).toEqual([1, 1, 1]);
    const body = classes(screen.getByTestId('row').parentElement!);
    expect(body.some(c => /^(p[xytb]?|gap)-/.test(c))).toBe(false);
    expect(classes(screen.getByTestId('padded').firstElementChild!)).toEqual(expect.arrayContaining(['px-4', 'pt-4']));
  });

  it('кликабельной карточке состояния и события перетаскивания не положены (типы)', () => {
    render(<MemoryRouter>
      {/* @ts-expect-error — цель накрывает карточку, тянуть нечего */}
      <Card title="Стройка" to="/sites/1" dragged />
      {/* @ts-expect-error — бросать на кнопку некуда */}
      <Card title="Участок" onClick={() => {}} onDrop={() => {}} />
      {/* @ts-expect-error — ошибка у кликабельной не рисуется */}
      <Card title="Смета" onClick={() => {}} invalid />
    </MemoryRouter>);
    expect(screen.getAllByRole('heading')).toHaveLength(3);
  });

  it('пустое тело — null, false, несработавшее условие — отступа не рисует', () => {
    render(<>
      <Card data-testid="empty" title="Итоги">{null}{false}</Card>
      <Card data-testid="full" title="Итоги">тело</Card>
    </>);
    expect(screen.getByTestId('empty').children).toHaveLength(1);
    expect(screen.getByTestId('full').children).toHaveLength(2);
  });
});

describe('Divider', () => {
  it('для читалки — separator; decorative скрыт; inset — отступ от ведущего края', () => {
    render(<>
      <Divider />
      <Divider orientation="vertical" />
      <div data-testid="deco"><Divider decorative /></div>
      <div data-testid="inset"><Divider inset="start" decorative /></div>
    </>);
    const [h, v] = screen.getAllByRole('separator');
    expect(h.tagName).toBe('HR');
    expect(classes(h)).toEqual(expect.arrayContaining(['h-px', 'bg-outline-variant']));
    expect(v.getAttribute('aria-orientation')).toBe('vertical');
    expect(screen.getAllByRole('separator')).toHaveLength(2);
    expect(screen.getByTestId('deco').firstElementChild?.getAttribute('aria-hidden')).toBe('true');
    expect(classes(screen.getByTestId('inset').firstElementChild!)).toContain('ms-4');
  });
});

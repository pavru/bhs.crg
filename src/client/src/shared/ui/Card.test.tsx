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

  it('обычная карточка — без слоя состояния; кликабельная — слой и возвышение под курсором', () => {
    const onClick = vi.fn();
    render(<MemoryRouter>
      <Card data-testid="plain" title="Стройка">тело</Card>
      <Card data-testid="btn" title="Стройка" onClick={onClick}>тело</Card>
      <Card data-testid="link" variant="elevated" title="Смета" to="/estimates/1">тело</Card>
    </MemoryRouter>);
    expect(classes(screen.getByTestId('plain'))).not.toContain('state-layer');
    const btn = screen.getByTestId('btn');
    expect(btn.tagName).toBe('BUTTON');
    expect(classes(btn)).toEqual(expect.arrayContaining(['state-layer', 'hover:shadow-elevation-1']));
    fireEvent.click(btn);
    expect(onClick).toHaveBeenCalledTimes(1);
    const link = screen.getByTestId('link');
    expect(link.getAttribute('href')).toBe('/estimates/1');
    expect(classes(link)).toContain('hover:shadow-elevation-2');
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

// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Archive } from './icons';
import { Checkbox } from './Checkbox';
import { PageHeader } from './PageHeader';
import { Chip } from './Chip';
import { StatusBadge } from './StatusBadge';
import { TabPanel, Tabs, type TabItem } from './Tabs';

// Примитивы интерфейса (issue #1351, #1352): поведение, которое экраны получают даром, — имя для диктора,
// состояние, клавиатура. Вид проверяется глазами и сторожем палитры, не здесь.
afterEach(cleanup);

describe('Checkbox', () => {
  it('подпись даёт флажку имя и переключает его щелчком', () => {
    const onChange = vi.fn();
    render(<Checkbox checked={false} onChange={onChange} label="Запомнить меня" />);
    fireEvent.click(screen.getByText('Запомнить меня'));
    expect(onChange).toHaveBeenCalledWith(true);
    expect(screen.getByRole('checkbox', { name: 'Запомнить меня' })).toBeTruthy();
  });

  it('«отмечены не все» ставится свойством элемента', () => {
    const { rerender } = render(<Checkbox checked={false} onChange={() => {}} indeterminate aria-label="Все" />);
    const box = screen.getByRole('checkbox', { name: 'Все' }) as HTMLInputElement;
    expect(box.indeterminate).toBe(true);
    rerender(<Checkbox checked onChange={() => {}} aria-label="Все" />);
    expect(box.indeterminate).toBe(false);
  });

  it('недоступный не переключается', () => {
    const onChange = vi.fn();
    render(<Checkbox checked={false} onChange={onChange} label="Нельзя" disabled />);
    fireEvent.click(screen.getByText('Нельзя'));
    expect(onChange).not.toHaveBeenCalled();
  });

  it('две подписи подряд — два блока, и каждая переключает свой флажок [UI-41]', () => {
    const onEncrypt = vi.fn();
    const onProxy = vi.fn();
    render(<div>
      <Checkbox checked={false} onChange={onEncrypt} label="Шифрование" />
      <Checkbox checked={false} onChange={onProxy} label="Через прокси" />
    </div>);
    // jsdom раскладки не считает, поэтому здесь сторожится только класс подписи: строку делят строчные
    // блоки, блочный flex стоит своей строкой. Что две подписи на экране и правда стоят друг под другом,
    // видит живой прогон `settings` (настройки почты).
    for (const text of ['Шифрование', 'Через прокси']) {
      const label = screen.getByText(text).closest('label')!;
      expect(label.className).toMatch(/(^|\s)flex(\s|$)/);
      expect(label.className).not.toMatch(/inline-flex/);
    }
    fireEvent.click(screen.getByText('Через прокси'));
    expect(onProxy).toHaveBeenCalledWith(true);
    expect(onEncrypt).not.toHaveBeenCalled();
  });

  it('boxClassName остаётся на квадратике и с подписью, className — на внешнем элементе', () => {
    const { rerender } = render(<Checkbox checked={false} onChange={() => {}} aria-label="Право" boxClassName="mt-1" className="opacity-70" />);
    let box = screen.getByRole('checkbox', { name: 'Право' });
    expect(box.className).toMatch(/(^|\s)mt-1(\s|$)/);
    expect(box.className).toMatch(/opacity-70/);
    rerender(<Checkbox checked={false} onChange={() => {}} label="Право" boxClassName="mt-1" className="opacity-70" />);
    box = screen.getByRole('checkbox', { name: 'Право' });
    expect(box.className).toMatch(/(^|\s)mt-1(\s|$)/);
    expect(box.className).not.toMatch(/opacity-70/);
    expect(box.closest('label')!.className).toMatch(/opacity-70/);
  });
});

describe('Chip', () => {
  it('переключатель называет состояние aria-pressed', () => {
    render(<Chip selected={false} icon={Archive}>В архиве</Chip>);
    expect(screen.getByRole('button', { name: 'В архиве' }).getAttribute('aria-pressed')).toBe('false');
  });

  it('простой чип — не переключатель', () => {
    render(<Chip>Добавить</Chip>);
    expect(screen.getByRole('button', { name: 'Добавить' }).hasAttribute('aria-pressed')).toBe(false);
  });

  it('число входит в имя, снятие — отдельная кнопка со своим именем', () => {
    const onClick = vi.fn();
    const onRemove = vi.fn();
    render(<Chip selected count={3} onClick={onClick} onRemove={onRemove} removeLabel="Снять отбор">Ошибки</Chip>);
    fireEvent.click(screen.getByRole('button', { name: 'Снять отбор' }));
    expect(onRemove).toHaveBeenCalledOnce();
    expect(onClick).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: /Ошибки.*3/ }).getAttribute('aria-pressed')).toBe('true');
  });
});

describe('StatusBadge', () => {
  it('показывает текст; значок скрыт от диктора', () => {
    const { container } = render(<StatusBadge tone="success" icon={Archive}>оплачен</StatusBadge>);
    expect(screen.getByText('оплачен')).toBeTruthy();
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});

describe('Tabs', () => {
  const ITEMS: TabItem<'a' | 'b' | 'c'>[] = [
    { value: 'a', label: 'Реквизиты' },
    { value: 'b', label: 'Качество', disabled: true },
    { value: 'c', label: 'Генерация' },
  ];

  function Harness({ veto = false }: { veto?: boolean }) {
    const [tab, setTab] = useState<'a' | 'b' | 'c'>('a');
    return (
      <>
        <Tabs id="t" aria-label="Разделы" items={ITEMS} value={tab} onChange={next => { if (!veto) setTab(next); }} />
        <TabPanel id="t" value={tab}>панель {tab}</TabPanel>
      </>
    );
  }

  it('выбранная вкладка одна в порядке обхода и связана с панелью', () => {
    render(<Harness />);
    const tab = screen.getByRole('tab', { name: 'Реквизиты' });
    expect(tab.getAttribute('aria-selected')).toBe('true');
    expect(tab.tabIndex).toBe(0);
    expect(screen.getByRole('tab', { name: 'Генерация' }).tabIndex).toBe(-1);
    expect(screen.getByRole('tabpanel', { name: 'Реквизиты' }).textContent).toBe('панель a');
  });

  it('стрелки двигают фокус мимо недоступной, но не переключают', () => {
    render(<Harness />);
    const first = screen.getByRole('tab', { name: 'Реквизиты' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'Генерация' }));
    expect(first.getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(document.activeElement!, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(first);
    fireEvent.keyDown(first, { key: 'End' });
    expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'Генерация' }));
  });

  it('щелчок — просьба: экран может отказать', () => {
    const { unmount } = render(<Harness veto />);
    fireEvent.click(screen.getByRole('tab', { name: 'Генерация' }));
    expect(screen.getByRole('tab', { name: 'Реквизиты' }).getAttribute('aria-selected')).toBe('true');
    unmount();
    render(<Harness />);
    fireEvent.click(screen.getByRole('tab', { name: 'Генерация' }));
    expect(screen.getByRole('tabpanel').textContent).toBe('панель c');
  });
});

describe('PageHeader', () => {
  it('заголовок — h1 страницы, действия и подзаголовок рядом', () => {
    render(<PageHeader title="Стройки" subtitle="Все стройки компании" actions={<button type="button">Новая стройка</button>} />);
    expect(screen.getByRole('heading', { level: 1, name: 'Стройки' })).toBeTruthy();
    expect(screen.getByText('Все стройки компании')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Новая стройка' })).toBeTruthy();
  });
});

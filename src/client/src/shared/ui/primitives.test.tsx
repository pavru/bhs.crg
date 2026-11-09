// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Archive, Save } from './icons';
import { Checkbox } from './Checkbox';
import { PageHeader } from './PageHeader';
import { Badge } from './Badge';
import { NavItem } from './ListDetailShell';
import { ValueIssueCount } from './ValueIssue';
import { CountText } from './CountText';
import { StatusText } from './StatusText';
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

  it('boxClassName остаётся на следе флажка и с подписью, className — на внешнем элементе', () => {
    // След — обёртка input и квадрата ([UI-69]): отступ места двигает обоих.
    const { rerender } = render(<Checkbox checked={false} onChange={() => {}} aria-label="Право" boxClassName="mt-1" className="ml-2" />);
    let box = screen.getByRole('checkbox', { name: 'Право' }).parentElement!;
    expect(box.className).toMatch(/(^|\s)mt-1(\s|$)/);
    expect(box.className).toMatch(/(^|\s)ml-2(\s|$)/);
    rerender(<Checkbox checked={false} onChange={() => {}} label="Право" boxClassName="mt-1" className="ml-2" />);
    box = screen.getByRole('checkbox', { name: 'Право' }).parentElement!;
    expect(box.className).toMatch(/(^|\s)mt-1(\s|$)/);
    expect(box.className).not.toMatch(/(^|\s)ml-2(\s|$)/);
    expect(box.closest('label')!.className).toMatch(/(^|\s)ml-2(\s|$)/);
  });
});

describe('StatusText', () => {
  it('показывает текст; значок скрыт от диктора', () => {
    const { container } = render(<StatusText tone="success" icon={Archive}>оплачен</StatusText>);
    expect(screen.getByText('оплачен')).toBeTruthy();
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  // Пометка — текст роли без заливки и рамки ([UI-74]); у error, warning и success — знак: не только цветом.
  it('error — цвет роли и залитый знак, без заливки и рамки', () => {
    const { container } = render(<StatusText tone="error">не сформирован</StatusText>);
    const mark = container.querySelector('span')!;
    expect(mark.className).toContain('text-error');
    expect(mark.className).toContain('text-label-medium');
    expect(mark.className).not.toMatch(/(?<![\w:-])(?:bg-|border\b|rounded)/);
    expect(container.querySelector('svg')).not.toBeNull();
  });

  it('свой знак залит только у тонов со знаком: quiet с Save — контуром', () => {
    const glyph = (tone: 'quiet' | 'success') =>
      render(<StatusText tone={tone} icon={Save}>сохранено</StatusText>).container.querySelector('svg')!.innerHTML;
    const quiet = glyph('quiet');
    cleanup();
    expect(glyph('success')).not.toBe(quiet);
  });

  it('соседние пометки делит точка; отступ — у точки, отступ места не перебит', () => {
    const { container } = render(<div>
      <StatusText tone="error" className="ml-1.5">2</StatusText>
      <StatusText tone="warning" className="ml-1.5">1</StatusText>
    </div>);
    for (const mark of container.querySelectorAll('div > span')) {
      expect(mark.className).toContain("[&+&]:before:content-['·']");
      expect(mark.className).not.toMatch(/\[&\+&\]:m[se]-/);
      expect(mark.className).toContain('ml-1.5');
    }
  });

  it('ValueIssueCount — пометка warning с числом; ноль — ничего', () => {
    const { container, rerender } = render(<ValueIssueCount count={4} />);
    expect(container.querySelector('span')!.className).toContain('text-warning');
    expect(container.textContent).toBe('4');
    rerender(<ValueIssueCount count={0} />);
    expect(container.innerHTML).toBe('');
  });

  it('neutral — без знака', () => {
    const { container } = render(<StatusText>черновик</StatusText>);
    expect(container.querySelector('svg')).toBeNull();
    expect(container.querySelector('span')!.className).toContain('text-on-surface-variant');
  });
});

describe('CountText и Badge', () => {
  it('CountText — число текстом, цифры одной ширины, без заливки', () => {
    const { container } = render(<CountText>3</CountText>);
    const count = container.querySelector('span')!;
    expect(count.textContent).toBe('3');
    expect(count.className).toContain('tabular-nums');
    expect(count.className).not.toMatch(/(?<![\w:-])bg-/);
  });

  // Больше 999 — «999+» на виду, а точное число — в доступном имени того, к чему badge прикреплён ([UI-74]).
  it('Badge 1200 — «999+», а имя вкладки называет 1200', () => {
    render(<button type="button">Замечания <Badge count={1200} /></button>);
    expect(screen.getByText('999+').getAttribute('aria-hidden')).toBe('true');
    expect(screen.getByRole('button', { name: 'Замечания 1200' })).toBeTruthy();
  });

  it('CountText недоступного пункта — цвет недоступного текста', () => {
    const { container } = render(<CountText disabled>2</CountText>);
    expect(container.querySelector('span')!.className).toContain('text-on-surface/38');
  });

  it('Badge без числа — точка, скрытая от диктора; цвет — только error', () => {
    const { container } = render(<Badge />);
    const dot = container.querySelector('span')!;
    expect(dot.getAttribute('aria-hidden')).toBe('true');
    expect(dot.className).toContain('bg-error');
  });
});

describe('NavItem: число и отметки', () => {
  const shownNumber = (props: Partial<Parameters<typeof NavItem>[0]>) =>
    render(<NavItem label="Раздел" onClick={() => {}} {...props} />).container.querySelector('.tabular-nums')!;

  it('count — CountText; у выбранного пункта цветом его контейнера — secondary-container ([UI-84])', () => {
    expect(shownNumber({ count: 5 }).className).toContain('text-on-surface-variant');
    cleanup();
    expect(shownNumber({ count: 5, active: true }).className).toContain('text-on-secondary-container');
  });

  it('выбранный пункт — строка ListItem на secondary-container, не пилюля primary-container; ребёнок с шевроном не выбран', () => {
    render(<>
      <NavItem label="Каталог" active onClick={() => {}} />
      <NavItem label="Раздел 1" active chevron count={3} onClick={() => {}} />
    </>);
    const sel = screen.getByRole('button', { name: /Каталог/ });
    expect(sel.getAttribute('aria-current')).toBe('true');
    const row = sel.parentElement!.className;
    expect(row).toContain('bg-secondary-container');
    expect(row).not.toContain('bg-primary-container');
    expect(sel.className).not.toContain('rounded-full');
    const child = screen.getByRole('button', { name: /Раздел 1/ });
    expect(child.hasAttribute('aria-current')).toBe(false);
    expect(child.parentElement!.className).not.toContain('bg-secondary-container');
  });

  it('progress — пометка: 100 % success, иначе neutral', () => {
    expect(shownNumber({ progress: 100 }).className).toContain('text-success');
    cleanup();
    expect(shownNumber({ progress: 40 }).className).toContain('text-on-surface-variant');
  });

  it('alert — warning, расхождение системы — error; больше 99 — «99+»', () => {
    expect(shownNumber({ alert: 3 }).className).toContain('text-warning');
    cleanup();
    const danger = shownNumber({ alert: 120, alertDanger: true });
    expect(danger.className).toContain('text-error');
    expect(danger.textContent).toBe('99+');
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

describe('Tabs primary', () => {
  type Top = 'revisions' | 'attachments';
  type Rev = 'r1' | 'r2' | 'r3';
  const TOP: TabItem<Top>[] = [
    { value: 'revisions', label: 'Редакции' },
    { value: 'attachments', label: 'Приложения', badge: <CountText>3</CountText> },
  ];
  const REVS: TabItem<Rev>[] = [
    { value: 'r1', label: 'Ред. 1' }, { value: 'r2', label: 'Ред. 2' }, { value: 'r3', label: 'Ред. 3' },
  ];

  // Ряд primary над underline в его панели ([UI-91]): у каждого ряда свой id.
  function Card() {
    const [top, setTop] = useState<Top>('revisions');
    const [rev, setRev] = useState<Rev>('r2');
    return (
      <>
        <Tabs id="top" variant="primary" aria-label="Смета" items={TOP} value={top} onChange={setTop} />
        <TabPanel id="top" value={top}>
          {top === 'revisions' ? (
            <>
              <Tabs id="rev" aria-label="Редакции сметы" items={REVS} value={rev} onChange={setRev} />
              <TabPanel id="rev" value={rev}>строки {rev}</TabPanel>
            </>
          ) : 'приложения'}
        </TabPanel>
      </>
    );
  }

  const indicatorOf = (tab: HTMLElement) => tab.querySelector('[data-tab-indicator]');
  const width = window.innerWidth;
  afterEach(() => { window.innerWidth = width; });

  it('индикатор только у выбранной и по содержимому, а не у кнопки', () => {
    render(<Card />);
    const revisions = screen.getByRole('tab', { name: 'Редакции' });
    const attachments = screen.getByRole('tab', { name: /^Приложения/ });
    const bar = indicatorOf(revisions)!;
    expect(bar).toBeTruthy();
    expect(bar.parentElement!.hasAttribute('data-tab-content')).toBe(true);
    expect(bar.className).toContain('h-0.75');
    expect(bar.className).toContain('bg-primary');
    expect(revisions.className).toContain('text-primary');
    expect(revisions.className).toContain('h-12');
    expect(revisions.className).not.toContain('border-b-2');
    expect(indicatorOf(attachments)).toBeNull();
    expect(attachments.className).toContain('text-on-surface-variant');
  });

  it('стрелка двигает фокус, Enter переключает раздел', () => {
    render(<Card />);
    const revisions = screen.getByRole('tab', { name: 'Редакции' });
    revisions.focus();
    fireEvent.keyDown(revisions, { key: 'ArrowRight' });
    const attachments = screen.getByRole('tab', { name: /^Приложения/ });
    expect(document.activeElement).toBe(attachments);
    expect(attachments.getAttribute('aria-selected')).toBe('false');
    fireEvent.click(attachments);
    expect(attachments.getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tabpanel', { name: /^Приложения/ }).textContent).toBe('приложения');
  });

  it('у каждого ряда одна остановка Tab и своя панель', () => {
    render(<Card />);
    const stops = screen.getAllByRole('tab').filter(t => t.tabIndex === 0);
    expect(stops.map(t => t.textContent)).toEqual(['Редакции', 'Ред. 2']);
    expect(document.getElementById(stops[0].getAttribute('aria-controls')!)!.contains(stops[1])).toBe(true);
    expect(document.getElementById(stops[1].getAttribute('aria-controls')!)!.textContent).toBe('строки r2');
  });

  it('пустой badge не меняет текст вкладки: экраны ищут её по подписи', () => {
    // Редактор документа отдаёт badge фрагментом всегда, даже пустым (живой прогон ищет /^Генерация$/).
    render(<Tabs id="b" variant="primary" aria-label="Разделы" value="a" onChange={() => {}}
      items={[{ value: 'a', label: 'Генерация', badge: <>{false}</> }, { value: 'b', label: 'Ошибки', badge: <CountText>0</CountText> }]} />);
    expect(screen.getByRole('tab', { name: 'Генерация' }).textContent).toBe('Генерация');
    expect(screen.getByRole('tab', { name: /^Ошибки/ }).textContent).toBe('Ошибки0');
  });

  it('панель названа только у выбранной: невыбранной ссылаться не на что', () => {
    render(<Card />);
    expect(screen.getByRole('tab', { name: 'Редакции' }).getAttribute('aria-controls')).toBe('top-panel-revisions');
    expect(screen.getByRole('tab', { name: /^Приложения/ }).hasAttribute('aria-controls')).toBe(false);
  });

  it('не влезающий ряд прокручивается, подписи не переносятся', () => {
    render(<Card />);
    const list = screen.getByRole('tablist', { name: 'Смета' });
    expect(list.className).toContain('overflow-x-auto');
    for (const t of screen.getAllByRole('tab').slice(0, 2)) {
      expect(t.className).toContain('whitespace-nowrap');
      expect(t.className).toContain('shrink-0');
    }
  });

  it('недоступная вкладка — вид недоступной, стрелка её пропускает', () => {
    render(<Tabs id="d" variant="primary" aria-label="Разделы" value="a" onChange={() => {}} items={[
      { value: 'a', label: 'Смета' }, { value: 'b', label: 'Анализ', disabled: true }, { value: 'c', label: 'ВОР' },
    ]} />);
    const off = screen.getByRole('tab', { name: 'Анализ' }) as HTMLButtonElement;
    expect(off.disabled).toBe(true);
    expect(off.className).toContain('disabled-content');
    const first = screen.getByRole('tab', { name: 'Смета' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'ВОР' }));
  });

  it('со значком — значок над подписью, ряд 64', () => {
    render(<Tabs id="i" variant="primary" aria-label="Разделы" value="a" onChange={() => {}}
      items={[{ value: 'a', label: 'Архив', icon: Archive }, { value: 'b', label: 'Черновики' }]} />);
    const tab = screen.getByRole('tab', { name: 'Архив' });
    const content = tab.querySelector('[data-tab-content]')!;
    expect(content.className).toContain('flex-col');
    expect(content.firstElementChild!.tagName.toLowerCase()).toBe('svg');
    for (const t of screen.getAllByRole('tab')) expect(t.className).toContain('h-16');
    // У вкладки без значка — пустое место под него: подписи ряда на одной высоте.
    const bare = screen.getByRole('tab', { name: 'Черновики' }).querySelector('[data-tab-content]')!;
    expect(bare.firstElementChild!.hasAttribute('data-tab-icon-slot')).toBe(true);
    expect(tab.querySelector('[data-tab-icon-slot]')).toBeNull();
  });

  it('на телефоне вкладки делят ряд поровну, на рабочем столе — по содержимому', () => {
    const { unmount } = render(<Card />);
    expect(screen.getByRole('tab', { name: 'Редакции' }).className).not.toContain('flex-1');
    unmount();
    window.innerWidth = 375;
    render(<Card />);
    for (const name of ['Редакции', /^Приложения/]) {
      const tab = screen.getByRole('tab', { name });
      expect(tab.className).toContain('flex-1 min-w-max');
      expect(tab.className).not.toContain('shrink-0');
    }
    expect(screen.getByRole('tab', { name: 'Ред. 2' }).className).not.toContain('flex-1');
  });
});

describe('PageHeader', () => {
  it('заголовок — h1 страницы, действия и подзаголовок рядом', () => {
    render(<PageHeader title="Стройки" subtitle="Все стройки компании" actions={<button type="button">Новая стройка</button>} />);
    expect(screen.getByRole('heading', { level: 1, name: 'Стройки' })).toBeTruthy();
    expect(screen.getByText('Все стройки компании')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Новая стройка' })).toBeTruthy();
  });

  it('заголовок набран title-large — 22/28 весом 400 (sharedui-md3-15)', () => {
    render(<PageHeader title="Реестр счетов" />);
    const h1 = screen.getByRole('heading', { level: 1, name: 'Реестр счетов' });
    expect(h1.className).toContain('text-title-large');
    expect(h1.className).not.toContain('font-semibold');
  });
});

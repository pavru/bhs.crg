// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { DateField } from './DateField';
import { IconButton } from './Button';
import { Close } from './icons';
import { TextAreaField } from './TextAreaField';
import { TextField } from './TextField';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Оболочка поля по MD3 ([UI-69], sharedui-md3-11): два размера, «только для чтения», знак ошибки. */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(cleanup);

const classes = (el: Element) => el.className.split(/\s+/);
const marks = (root: ParentNode = document) => root.querySelectorAll('[data-field-error-mark]');
/** Что диктор прочтёт описанием поля: тексты элементов из `aria-describedby`. */
const description = (el: Element) => (el.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean)
  .map(id => document.getElementById(id)?.textContent ?? '').join(' ');

describe('OutlinedField: размер', () => {
  it('поле формы — 56, текст body-large, подпись в вырезе', () => {
    const { container } = render(<TextField label="Номер счёта" />);
    expect(classes(screen.getByLabelText('Номер счёта'))).toEqual(expect.arrayContaining(['h-14', 'text-body-large']));
    expect(container.querySelector('legend')).not.toBeNull();
  });

  it('плотное — 40, body-medium, выреза нет, а поле находится по подписи', () => {
    const { container } = render(<TextField label="Колонка" density="dense" />);
    const input = screen.getByLabelText('Колонка');
    expect(classes(input)).toEqual(expect.arrayContaining(['h-10', 'text-body-medium']));
    expect(container.querySelector('legend')).toBeNull();
    // Подпись — только для диктора: видимое имя плотному полю даёт строка или шапка колонки.
    expect(classes(container.querySelector('label')!)).toContain('sr-only');
  });
});

describe('OutlinedField: только для чтения', () => {
  it('обводка border-outline-variant без подложки', () => {
    const { container } = render(<TextField label="Состояние" readOnly value="Черновик" onChange={() => {}} />);
    expect(classes(container.querySelector('fieldset')!)).toContain('border-outline-variant');
    expect(container.querySelector('.bg-surface-container')).toBeNull();
    expect(container.querySelector('.isolate')).toBeNull();
  });
});

describe('OutlinedField: знак ошибки', () => {
  it('при ошибке знак есть и стоит перед хвостом', () => {
    render(<TextField label="Пароль" error="Слишком короткий"
      trailing={<IconButton label="Показать пароль" icon={Close} />} />);
    const [mark] = marks();
    const button = screen.getByRole('button', { name: 'Показать пароль' });
    expect(mark).toBeDefined();
    expect(mark.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(mark.getAttribute('aria-hidden')).toBe('true');
  });

  it('без ошибки знака нет', () => {
    render(<TextField label="ИНН" hint="10 или 12 цифр" />);
    expect(marks()).toHaveLength(0);
  });

  it('у поля даты с ошибкой ровно один знак', () => {
    render(<DateField label="Дата платежа" value="2026-10-31" onChange={() => {}} invalid hint="Дата позже сегодняшней" />);
    expect(marks()).toHaveLength(1);
  });

  it('диктор получает текст ошибки через aria-describedby', () => {
    render(<TextField label="ИНН" error="Сервер отказал: ИНН не найден" />);
    expect(description(screen.getByLabelText('ИНН'))).toBe('Сервер отказал: ИНН не найден');
  });

  it('подпись в покое кончается до знака и хвоста, всплывшая — у рамки', () => {
    const { container } = render(<TextField label="Пароль" error="Слишком короткий"
      trailing={<IconButton label="Показать пароль" icon={Close} />} />);
    const label = classes(container.querySelector('label')!);
    expect(label).toContain('max-w-[calc(100%-3.25rem-var(--field-mark,0px))]');
    expect(label).toContain('peer-focus:max-w-[calc(100%-1.5rem)]');
    expect((container.querySelector('fieldset')!.parentElement as HTMLElement).style.getPropertyValue('--field-mark')).toBe('28px');
  });

  it('слот знака — по первой строке: у многострочного поля не посреди текста', () => {
    render(<TextAreaField label="Комментарий" rows={6} error="Слишком длинный" />);
    const slot = marks()[0].parentElement!;
    expect(classes(slot)).toContain('top-7');
    expect(classes(slot)).not.toContain('top-1/2');
  });

  it('у плотного поля текст ошибки — для диктора, а знак 20', () => {
    const { container } = render(<TextField label="Количество" density="dense" error="Больше строки" />);
    expect(description(screen.getByLabelText('Количество'))).toBe('Больше строки');
    expect(classes(container.querySelector('p')!)).toContain('sr-only');
    expect(marks()[0].querySelector('svg')!.getAttribute('width')).toBe('20');
  });
});

// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Modal, ModalExit, type ModalProps } from './Modal';
import { DISMISSAL, modalState, type ModalState } from './modalDismissal';

/**
 * Правило закрытия диалога (issue #1235) — по строке на состояние: чем закрывается и чем нет.
 * Проверяется на живом компоненте, а не на таблице: таблицу можно прочитать верно и подключить
 * не к тому событию Radix.
 */

afterEach(cleanup);

type Extra = Partial<ModalProps>;

function open(extra: Extra = {}, body: React.ReactNode = <input aria-label="поле" />) {
  const onOpenChange = vi.fn();
  const props = {
    open: true, onOpenChange, title: 'Окно',
    footer: <><ModalExit /><button type="button">Сохранить</button></>,
    ...extra,
  } as ModalProps;
  render(<Modal {...props}>{body}</Modal>);
  return onOpenChange;
}

const closed = (fn: ReturnType<typeof vi.fn>) => fn.mock.calls.some(c => c[0] === false);

/**
 * Клик по затемнению. Radix подписывается на `pointerdown` документа не сразу (таймером), а на
 * нажатие левой кнопкой отвечает только по следующему `click` — поэтому событий два.
 */
async function clickOutside() {
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  fireEvent.pointerDown(document.body);
  fireEvent.click(document.body);
}
/** Esc: диалог отвечает не сразу, а когда клавиша дошла до документа никем не занятой. */
async function escape() {
  fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
}
const cross = () => screen.queryByRole('button', { name: 'Закрыть' });
const cancel = () => screen.getByRole('button', { name: 'Отмена' });
const type = () => fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
const asked = () => screen.queryByText('Закрыть без сохранения?') !== null;

describe('таблица правила', () => {
  it('состояние — первое подошедшее по силе', () => {
    expect(modalState({})).toBe('plain');
    expect(modalState({ touched: true })).toBe('touched');
    expect(modalState({ touched: true, nothingToLose: true })).toBe('light');
    expect(modalState({ touched: true, isDirty: false })).toBe('plain');
    expect(modalState({ isDirty: true })).toBe('dirty');
    expect(modalState({ isDirty: true, acknowledge: 'Понятно' })).toBe('result');
    expect(modalState({ isDirty: true, acknowledge: 'Понятно', busy: true })).toBe('busy');
  });

  it('клик мимо закрывает только лёгкий диалог', () => {
    const closing = (Object.keys(DISMISSAL) as ModalState[]).filter(s => DISMISSAL[s].outside === 'close');
    expect(closing).toEqual(['light']);
  });
});

describe('лёгкий (nothingToLose)', () => {
  it('закрывают клик мимо, Esc и крестик', async () => {
    const a = open({ nothingToLose: true, footer: undefined });
    await clickOutside();
    expect(closed(a)).toBe(true);
    cleanup();

    const b = open({ nothingToLose: true, footer: undefined });
    await escape();
    expect(closed(b)).toBe(true);
    cleanup();

    const c = open({ nothingToLose: true, footer: undefined });
    fireEvent.click(cross()!);
    expect(closed(c)).toBe(true);
  });

  it('набранный запрос поиска правкой не считается', async () => {
    const onOpenChange = open({ nothingToLose: true, footer: undefined });
    type();
    await escape();
    expect(closed(onOpenChange)).toBe(true);
  });
});

describe('обычный, терять нечего', () => {
  it('клик мимо не закрывает и обводит «Отмену»', async () => {
    const onOpenChange = open();
    await clickOutside();
    expect(closed(onOpenChange)).toBe(false);
    expect(cancel().hasAttribute('data-modal-hint')).toBe(true);
    // Фокус клик мимо не переносит: следующий пробел не должен стать «Отменой».
    expect(document.activeElement).not.toBe(cancel());
  });

  it('без «Отмены» обводится крестик', async () => {
    open({ footer: undefined });
    await clickOutside();
    expect(cross()!.hasAttribute('data-modal-hint')).toBe(true);
  });

  it('Esc, крестик и «Отмена» закрывают', async () => {
    const a = open();
    await escape();
    expect(closed(a)).toBe(true);
    cleanup();

    const b = open();
    fireEvent.click(cross()!);
    expect(closed(b)).toBe(true);
    cleanup();

    const c = open();
    fireEvent.click(cancel());
    expect(closed(c)).toBe(true);
  });

  it('начальный фокус — не на крестике', () => {
    open();
    expect(document.activeElement).not.toBe(cross());
    expect(document.activeElement?.getAttribute('role')).toBe('dialog');
  });
});

describe('в содержимом набирали, а вызывающий о правках молчит', () => {
  it('Esc не закрывает, а ставит фокус на «Отмену»', async () => {
    const onOpenChange = open();
    type();
    await escape();
    expect(closed(onOpenChange)).toBe(false);
    expect(document.activeElement).toBe(cancel());
  });

  it('явный выход закрывает без вопроса', () => {
    const onOpenChange = open();
    type();
    fireEvent.click(cancel());
    expect(asked()).toBe(false);
    expect(closed(onOpenChange)).toBe(true);
  });

  it('вызывающий, передавший isDirty={false}, знает точнее', async () => {
    const onOpenChange = open({ isDirty: false });
    type();
    await escape();
    expect(closed(onOpenChange)).toBe(true);
  });
});

describe('есть несохранённые правки (isDirty)', () => {
  it('клик мимо и Esc не закрывают и вопроса не задают', async () => {
    const onOpenChange = open({ isDirty: true });
    await clickOutside();
    await escape();
    expect(closed(onOpenChange)).toBe(false);
    expect(asked()).toBe(false);
    expect(document.activeElement).toBe(cancel());
  });

  it('крестик и «Отмена» подвала спрашивают', async () => {
    const onOpenChange = open({ isDirty: true });
    fireEvent.click(cross()!);
    expect(asked()).toBe(true);
    // Esc во внутреннем вопросе — «Продолжить редактирование».
    await escape();
    expect(asked()).toBe(false);

    fireEvent.click(cancel());
    expect(asked()).toBe(true);
    expect(closed(onOpenChange)).toBe(false);
  });

  it('выход, позванный из функции-подвала, идёт той же охраной', () => {
    const onOpenChange = open({
      isDirty: true,
      footer: requestClose => <button type="button" onClick={requestClose}>Выйти</button>,
    });
    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }));
    expect(asked()).toBe(true);
    expect(closed(onOpenChange)).toBe(false);
  });
});

describe('показан результат (acknowledge)', () => {
  it('выход один, и называет его диалог', async () => {
    const onOpenChange = open({ acknowledge: 'Понятно', footer: undefined });
    expect(cross()).toBeNull();
    await clickOutside();
    await escape();
    expect(closed(onOpenChange)).toBe(false);

    const exit = screen.getByRole('button', { name: 'Понятно' });
    expect(document.activeElement).toBe(exit);
    fireEvent.click(exit);
    expect(closed(onOpenChange)).toBe(true);
  });
});

describe('наблюдение за вводом', () => {
  // Диалог замечает ввод, но не имеет права его съесть. Перехват `input` React исполняет раньше
  // `onChange` поля и сразу перерисовывает — управляемое поле возвращается к прежнему значению, и
  // первый набранный знак пропадает молча.
  function Controlled() {
    const [value, setValue] = useState('2026');
    return <input aria-label="поле" value={value} onChange={e => setValue(e.target.value)} />;
  }

  it('первый набранный знак в управляемом поле не теряется', () => {
    open({}, <Controlled />);
    const field = screen.getByLabelText('поле') as HTMLInputElement;
    fireEvent.input(field, { target: { value: '20' } });
    expect(field.value).toBe('20');
  });

  // События React всплывают по дереву компонентов: запрос поиска в пикере, открытом из формы,
  // дошёл бы до формы и записал бы «тронуто» ей.
  function Picker() {
    const [shown, setShown] = useState(true);
    return <Modal open={shown} onOpenChange={setShown} title="Пикер" nothingToLose><input aria-label="поиск" /></Modal>;
  }

  it('ввод во вложенном окне — не правка этого', async () => {
    const outer = open({}, <Picker />);
    fireEvent.change(screen.getByLabelText('поиск'), { target: { value: 'запрос' } });
    await escape();   // закрывает пикер
    expect(screen.queryByLabelText('поиск')).toBeNull();
    expect(closed(outer)).toBe(false);
    await escape();   // внешнему окну по-прежнему нечего терять
    expect(closed(outer)).toBe(true);
  });
});

describe('Esc, которым распорядилось поле', () => {
  // Поле обработало клавишу (бросило строчную правку, очистило поиск, закрыло подсказку) и
  // остановило её всплытие — диалог не закрывается и фокус из поля не уводит.
  const own = <input aria-label="поле" onKeyDown={e => { if (e.key === 'Escape') e.stopPropagation(); }} />;

  it('пустой диалог не закрывает', async () => {
    const onOpenChange = open({}, own);
    screen.getByLabelText('поле').focus();
    await escape();
    expect(closed(onOpenChange)).toBe(false);
  });

  it('из формы с набранным фокус не уводит', async () => {
    const onOpenChange = open({ isDirty: true }, own);
    const field = screen.getByLabelText('поле');
    field.focus();
    await escape();
    expect(closed(onOpenChange)).toBe(false);
    expect(document.activeElement).toBe(field);
    expect(document.querySelector('[data-modal-hint]')).toBeNull();
  });
});

describe('идёт запрос (busy)', () => {
  it('мертвы клик мимо, Esc, крестик и выход подвала — и отклика нет', async () => {
    const onOpenChange = open({
      busy: true, isDirty: true,
      footer: requestClose => <><ModalExit /><button type="button" onClick={requestClose}>Выйти</button></>,
    });
    await clickOutside();
    await escape();
    expect((cross() as HTMLButtonElement).disabled).toBe(true);
    expect((cancel() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(cross()!);
    fireEvent.click(cancel());
    // Выход, переданный в подвал своей кнопкой: вызывающий `disabled` не ставил.
    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }));

    expect(onOpenChange).not.toHaveBeenCalled();
    expect(asked()).toBe(false);
    expect(document.querySelector('[data-modal-hint]')).toBeNull();
  });
});

describe('типы', () => {
  it('nothingToLose вместе с isDirty или результатом не компилируется', () => {
    const base = { open: false, onOpenChange: () => {}, title: '' };
    // @ts-expect-error лёгкий диалог не бывает «грязным»
    void (<Modal {...base} nothingToLose isDirty>x</Modal>);
    // @ts-expect-error и результатом не бывает
    void (<Modal {...base} nothingToLose acknowledge="Понятно">x</Modal>);
    expect(true).toBe(true);
  });
});

// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Modal, ModalExit, type ModalProps } from './Modal';
import { WindowStep } from './WindowStep';
import { intersect } from '@/shared/testing/intersectionObserver';
import { DISMISSAL, modalState, type ModalState } from './modalDismissal';

/**
 * Правило закрытия диалога (issue #1235) — по строке на состояние: чем закрывается и чем нет.
 * Проверяется на живом компоненте, а не на таблице: таблицу можно прочитать верно и подключить
 * не к тому событию Radix.
 */

afterEach(cleanup);

type Extra = Partial<ModalProps>;

function open(extra: Extra = {}, body: ModalProps['children'] = <input aria-label="поле" />) {
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
/** «Закрыть» подвала — её окно ставит само, когда своих действий нет. Крестика у окна нет ([UI-75]). */
const closeButton = () => screen.queryByRole('button', { name: 'Закрыть' });
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
  it('закрывают клик мимо, Esc и «Закрыть» подвала', async () => {
    const a = open({ nothingToLose: true, footer: undefined });
    await clickOutside();
    expect(closed(a)).toBe(true);
    cleanup();

    const b = open({ nothingToLose: true, footer: undefined });
    await escape();
    expect(closed(b)).toBe(true);
    cleanup();

    const c = open({ nothingToLose: true, footer: undefined });
    fireEvent.click(closeButton()!);
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

  it('окну без подвала окно ставит «Закрыть» — её и обводит', async () => {
    open({ footer: undefined });
    await clickOutside();
    expect(closeButton()!.hasAttribute('data-modal-hint')).toBe(true);
  });

  it('Esc и «Отмена» закрывают', async () => {
    const a = open();
    await escape();
    expect(closed(a)).toBe(true);
    cleanup();

    const c = open();
    fireEvent.click(cancel());
    expect(closed(c)).toBe(true);
  });

  it('крестика нет: у окна с подвалом единственный выход — его «Отмена»', () => {
    open();
    expect(closeButton()).toBeNull();
    expect(screen.getAllByRole('button').filter(b => b.hasAttribute('data-modal-exit'))).toEqual([cancel()]);
  });

  it('начальный фокус — на самом окне, а не на кнопке', () => {
    open();
    expect(document.activeElement).not.toBe(closeButton());
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

  it('«Отмена» подвала спрашивает — отдельным окном над окном', async () => {
    const onOpenChange = open({ isDirty: true });
    const exit = cancel();
    exit.focus();   // `fireEvent.click` фокус сам не ставит
    fireEvent.click(cancel());
    expect(asked()).toBe(true);
    // Вопрос — своё окно Radix над окном формы, а не блок внутри неё ([UI-75]).
    const question = screen.getByRole('alertdialog');
    expect(question.contains(screen.getByText('Закрыть без сохранения?'))).toBe(true);
    // Окно под вопросом Radix прячет от чтения с экрана — потому `hidden`.
    expect(screen.getByRole('dialog', { hidden: true }).contains(question)).toBe(false);
    // Вопрос забирает фокус на безопасный ответ, а форма под ним недоступна — одной обёрткой.
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Продолжить редактирование' }));
    expect(screen.getByLabelText('поле').closest('[inert]')).not.toBeNull();
    expect(exit.closest('[inert]')).toBe(screen.getByLabelText('поле').closest('[inert]'));
    // Esc в вопросе — «Продолжить редактирование», и фокус возвращается на «Отмену».
    await escape();
    expect(asked()).toBe(false);
    expect(document.activeElement).toBe(cancel());
    expect(screen.getByLabelText('поле').closest('[inert]')).toBeNull();

    fireEvent.click(cancel());
    expect(asked()).toBe(true);
    expect(closed(onOpenChange)).toBe(false);
  });

  it('щелчок мимо вопроса — «Продолжить редактирование»: окно с набранным остаётся', async () => {
    const onOpenChange = open({ isDirty: true });
    fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
    fireEvent.click(cancel());
    expect(asked()).toBe(true);
    await clickOutside();
    expect(asked()).toBe(false);
    expect(closed(onOpenChange)).toBe(false);
    expect((screen.getByLabelText('поле') as HTMLInputElement).value).toBe('набрано');
  });

  it('«Закрыть» вопроса закрывает окно', () => {
    const onOpenChange = open({ isDirty: true });
    fireEvent.click(cancel());
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(closed(onOpenChange)).toBe(true);
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
    expect(closeButton()).toBeNull();
    await clickOutside();
    await escape();
    expect(closed(onOpenChange)).toBe(false);

    const exit = screen.getByRole('button', { name: 'Понятно' });
    expect(document.activeElement).toBe(exit);
    // «Понятно» — text, как в `ConfirmDialog` ([UI-75]): у кнопки нет заливки.
    expect(exit.className).not.toMatch(/\bbg-(primary|secondary-container)\b/);
    fireEvent.click(exit);
    expect(closed(onOpenChange)).toBe(true);
  });
});

describe('наблюдение за вводом', () => {
  // ⚠️ Чего здесь НЕТ: проверки, что наблюдение не съедает первый набранный знак (перехват `input`
  // перерисовывал управляемое поле раньше его `onChange`). В jsdom это не воспроизводится — тест
  // оставался зелёным при сломанном коде. Ловят живые прогоны: `date-input-discards-partial-input-
  // on-blur` (shared-ui), `ref-picker-search-narrows-and-restores` и `paste-detects-header-and-
  // counts-rows` (fields) — все три краснели на этой поломке.

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
  it('мертвы клик мимо, Esc и выход подвала — и отклика нет', async () => {
    const onOpenChange = open({
      busy: true, isDirty: true,
      footer: requestClose => <><ModalExit /><button type="button" onClick={requestClose}>Выйти</button></>,
    });
    await clickOutside();
    await escape();
    // Недоступна, но в фокусе остаться может: `aria-disabled`, а не `disabled`.
    expect(cancel().getAttribute('aria-disabled')).toBe('true');
    expect((cancel() as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(cancel());
    // Выход, переданный в подвал своей кнопкой: вызывающий `disabled` не ставил.
    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }));

    expect(onOpenChange).not.toHaveBeenCalled();
    expect(asked()).toBe(false);
    expect(document.querySelector('[data-modal-hint]')).toBeNull();
  });
});

describe('ревью PR #1310', () => {
  it('начавшийся запрос снимает вопрос вместе с его кнопкой «Закрыть»', () => {
    const onOpenChange = vi.fn();
    const view = (busy: boolean) => (
      <Modal open onOpenChange={onOpenChange} title="Окно" isDirty busy={busy}><input aria-label="поле" /></Modal>
    );
    const { rerender } = render(view(false));
    fireEvent.click(closeButton()!);
    expect(asked()).toBe(true);
    rerender(view(true));
    expect(asked()).toBe(false);
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it('запрос поиска правкой не считается', async () => {
    const onOpenChange = open({}, <input aria-label="поле" data-search="" />);
    type();
    await escape();
    expect(closed(onOpenChange)).toBe(true);
  });

  it('кольцо не остаётся на прежней кнопке, когда выход сменился', async () => {
    const onOpenChange = vi.fn();
    const view = () => (
      <Modal open onOpenChange={onOpenChange} title="Окно" footer={<><ModalExit /><ModalExit>Закрыть</ModalExit></>}>
        <input aria-label="поле" />
      </Modal>
    );
    const { rerender } = render(view());
    await clickOutside();
    const first = cancel();
    expect(first.hasAttribute('data-modal-hint')).toBe(true);
    // «Отмена» стала недоступна — выходом осталась «Закрыть»; сама кнопка на месте.
    first.setAttribute('disabled', '');
    rerender(view());
    await clickOutside();
    expect(closeButton()!.hasAttribute('data-modal-hint')).toBe(true);
    expect(first.hasAttribute('data-modal-hint')).toBe(false);
  });

  it('Esc при наборе через IME диалог не трогает', async () => {
    const onOpenChange = open();
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape', isComposing: true });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(closed(onOpenChange)).toBe(false);
  });
});

describe('вид — basic dialog ([UI-75])', () => {
  it('поверхность, форма и заголовок ролями MD3, без рамки и размытого затемнения', () => {
    open();
    const dialog = screen.getByRole('dialog');
    const own = dialog.className.split(/\s+/);
    expect(own).toEqual(expect.arrayContaining(['bg-surface-container-high', 'rounded-xl', 'shadow-elevation-3']));
    expect(own.some(c => c === 'border' || c.startsWith('border-'))).toBe(false);
    expect(screen.getByText('Окно', { selector: 'span' }).className).toContain('text-headline-small');
    const scrim = [...document.querySelectorAll<HTMLElement>('.bg-scrim\\/32')];
    expect(scrim).toHaveLength(1);
    expect(scrim[0].style.backdropFilter).toBe('');
  });
});

describe('черта под шапкой и над подвалом — только при прокрутке ([UI-75])', () => {
  /** Шапка, подвал и часовые у краёв прокрутки тела. */
  function parts() {
    const dialog = screen.getByRole('dialog');
    const scroll = dialog.querySelector<HTMLElement>('.overflow-y-auto')!;
    return {
      header: screen.getByText('Окно', { selector: 'span' }).parentElement!,
      footer: cancel().closest<HTMLElement>('.border-t')!,
      start: scroll.firstElementChild!, end: scroll.lastElementChild!, scroll,
    };
  }
  const lined = (el: HTMLElement) => el.classList.contains('border-outline-variant');

  it('тело помещается — черт нет', () => {
    open();
    const { header, footer } = parts();
    expect(lined(header)).toBe(false);
    expect(lined(footer)).toBe(false);
  });

  it('прокрутка: черта у того края, за которым есть содержимое', () => {
    open();
    const { header, footer, start, end } = parts();
    // Середина длинного тела: часовые обоих краёв за пределами видимого.
    act(() => { intersect(start, false); intersect(end, false); });
    expect(lined(header)).toBe(true);
    expect(lined(footer)).toBe(true);
    // Докрутили до низа: над подвалом черты нет, под шапкой — есть.
    act(() => intersect(end, true));
    expect(lined(header)).toBe(true);
    expect(lined(footer)).toBe(false);
    // Вернулись к верху: черты под шапкой нет.
    act(() => intersect(start, true));
    expect(lined(header)).toBe(false);
  });

  it('часовые — по краям видимого тела: тело шага ложится между ними', () => {
    function WithStep() {
      const [step, setStep] = useState(false);
      return (
        <Modal open onOpenChange={() => {}} title="Окно" footer={<ModalExit />}>
          <button type="button" onClick={() => setStep(true)}>Шаг</button>
          {step && <WindowStep title="Шаг окна" onBack={() => setStep(false)}><p>тело шага</p></WindowStep>}
        </Modal>
      );
    }
    render(<WithStep />);
    fireEvent.click(screen.getByRole('button', { name: 'Шаг' }));
    const scroll = screen.getByRole('dialog').querySelector<HTMLElement>('.overflow-y-auto')!;
    const body = screen.getByText('тело шага');
    expect(scroll.contains(body)).toBe(true);
    expect(scroll.firstElementChild!.compareDocumentPosition(body) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(scroll.lastElementChild!.compareDocumentPosition(body) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy();
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

describe('фокус после закрытия', () => {
  /** Окно с полем `autoFocus`: Radix запоминал фокус уже на этом поле и после закрытия отдавал его body. */
  function Opener() {
    const [open, setOpen] = useState(false);
    return (<>
      <button type="button" onClick={() => setOpen(true)}>Открыть</button>
      <Modal open={open} onOpenChange={setOpen} title="Окно" footer={<ModalExit />}>
        <input aria-label="поле" autoFocus />
      </Modal>
    </>);
  }

  it('возвращается на открывшую кнопку, даже если поле взяло фокус само', async () => {
    render(<Opener />);
    const opener = screen.getByRole('button', { name: 'Открыть' });
    await act(async () => { opener.focus(); fireEvent.click(opener); });
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'поле' }));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(document.activeElement).toBe(opener);
  });
});

// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, createEvent, fireEvent, render, screen } from '@testing-library/react';
import { FileDropZone, type FileDropZoneProps } from './FileDropZone';
import { FOLDER_REFUSAL, SEVERAL_REFUSAL } from './fileDrop';

// Площадка выбора и броска файла ([UI-89]).
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const file = (name: string, size = 2048) => new File([new Uint8Array(size)], name);

/** Перенос файлов, как его видит обработчик: `types` с «Files», без списка элементов — берутся `files`. */
const transfer = (files: File[], folders: string[] = []) => ({
  types: ['Files'],
  files,
  items: folders.length === 0 ? [] : [
    ...files.map(f => ({ kind: 'file', getAsFile: () => f, webkitGetAsEntry: () => ({ isDirectory: false }) })),
    ...folders.map(name => ({ kind: 'file', getAsFile: () => file(name, 0), webkitGetAsEntry: () => ({ isDirectory: true }) })),
  ],
  dropEffect: 'none',
});
/** Выход перетаскивания: у jsdom нет `DragEvent`, и `relatedTarget` с точкой событию ставятся руками. */
const leave = (el: Element, related: Element | null, at: { clientX: number; clientY: number } = { clientX: 0, clientY: 0 }) => {
  const event = createEvent.dragLeave(el, { dataTransfer: transfer([file('a.gsfx')]) });
  Object.defineProperty(event, 'relatedTarget', { value: related });
  Object.defineProperty(event, 'clientX', { value: at.clientX });
  Object.defineProperty(event, 'clientY', { value: at.clientY });
  fireEvent(el, event);
};
const textTransfer = () => ({ types: ['text/plain'], files: [], items: [], dropEffect: 'none' });

const setup = (props: Partial<FileDropZoneProps> = {}) => {
  const onFile = vi.fn();
  const view = render(<FileDropZone label="Файл сметы" hint=".gsfx или .xml" onFile={onFile} {...props} />);
  const zone = () => screen.getByRole('button').closest('[data-disabled], div.rounded-md') as HTMLElement;
  const input = () => screen.getByLabelText('Файл сметы') as HTMLInputElement;
  const pick = (f: File) => fireEvent.change(input(), { target: { files: [f] } });
  const drop = (data: object) => fireEvent.drop(zone(), { dataTransfer: data });
  return { onFile, zone, input, pick, drop, ...view };
};

const gsfxOnly = (f: File) => (/\.(gsfx|xml)$/i.test(f.name) ? null : 'не .gsfx или .xml');

describe('FileDropZone', () => {
  it('бросок и выбор отдают один и тот же файл', () => {
    const { onFile, pick, drop } = setup();
    const one = file('смета.gsfx');
    drop(transfer([one]));
    pick(one);
    expect(onFile).toHaveBeenNthCalledWith(1, one);
    expect(onFile).toHaveBeenNthCalledWith(2, one);
  });

  it('тот же файл второй раз подряд — снова отдаётся: поле сброшено', () => {
    const { onFile, pick, input } = setup();
    const one = file('смета.gsfx');
    pick(one);
    expect(input().value).toBe('');
    pick(one);
    expect(onFile).toHaveBeenCalledTimes(2);
  });

  it('отказ проверки экрана — «имя — причина», файл не отдаётся; окно выбора проверяется так же', () => {
    const { onFile, drop, pick } = setup({ check: gsfxOnly });
    drop(transfer([file('счёт.pdf')]));
    expect(screen.getByRole('alert').textContent).toBe('счёт.pdf — не .gsfx или .xml');
    pick(file('акт.pdf'));
    expect(screen.getByRole('alert').textContent).toBe('акт.pdf — не .gsfx или .xml');
    expect(onFile).not.toHaveBeenCalled();
  });

  it('несколько файлов и папка — отказ словами площадки', () => {
    const { onFile, drop } = setup();
    drop(transfer([file('a.gsfx'), file('b.gsfx'), file('c.gsfx')]));
    expect(screen.getByRole('alert').textContent).toBe(`3 файла — ${SEVERAL_REFUSAL}`);
    drop(transfer([], ['Сканы']));
    expect(screen.getByRole('alert').textContent).toBe(`Сканы — ${FOLDER_REFUSAL}`);
    expect(onFile).not.toHaveBeenCalled();
  });

  it('тот же отказ дважды — узел alert новый: диктор читает оба раза', () => {
    const { drop } = setup({ check: gsfxOnly });
    drop(transfer([file('счёт.pdf')]));
    const first = screen.getByRole('alert');
    drop(transfer([file('счёт.pdf')]));
    const second = screen.getByRole('alert');
    expect(second.textContent).toBe(first.textContent);
    expect(second).not.toBe(first);
  });

  it('следующий годный файл снимает отказ', () => {
    const { onFile, drop } = setup({ check: gsfxOnly });
    drop(transfer([file('счёт.pdf')]));
    expect(screen.queryByRole('alert')).not.toBeNull();
    const good = file('смета.xml');
    drop(transfer([good]));
    expect(screen.queryByRole('alert')).toBeNull();
    expect(onFile).toHaveBeenCalledWith(good);
  });

  it('в загрузке бросок не принят, «наготове» нет, кнопка выключена, ход назван', () => {
    const { onFile, zone, drop } = setup({ busy: 'Загружаем «смета.gsfx»', file: { name: 'смета.gsfx', size: 10 } });
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('b.gsfx')]) });
    const data = transfer([file('b.gsfx')]);
    fireEvent.dragOver(zone(), { dataTransfer: data });
    expect(data.dropEffect).toBe('none');
    expect(screen.queryByText('Бросьте файл сюда')).toBeNull();
    expect(screen.queryByText('Отпустите — возьмём файл')).toBeNull();
    drop(transfer([file('b.gsfx')]));
    expect(onFile).not.toHaveBeenCalled();
    expect((screen.getByRole('button') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Загружаем «смета.gsfx»', { selector: 'p' })).toBeTruthy();
  });

  it('выключена — причина вместо заголовка, «наготове» нет', () => {
    setup({ disabled: 'Сначала выберите стройку' });
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Сначала выберите стройку')).toBeTruthy();
    expect(screen.queryByText('Бросьте файл сюда')).toBeNull();
    expect((screen.getByRole('button') as HTMLButtonElement).disabled).toBe(true);
  });

  it('тащат не файл — площадка не меняется и бросок не берёт', () => {
    const { onFile, zone, drop } = setup();
    fireEvent.dragEnter(document.body, { dataTransfer: textTransfer() });
    fireEvent.dragOver(zone(), { dataTransfer: textTransfer() });
    expect(screen.getByText('Перетащите файл сюда')).toBeTruthy();
    drop(textTransfer());
    expect(onFile).not.toHaveBeenCalled();
  });

  it('выбранный файл заменяется броском', () => {
    const { onFile, drop } = setup({ file: { name: 'старая.gsfx', size: 10 } });
    expect(screen.getByText('старая.gsfx')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Другой файл' })).toBeTruthy();
    const next = file('новая.gsfx');
    drop(transfer([next]));
    expect(onFile).toHaveBeenCalledWith(next);
  });

  it('наготове → отпустите → за край → наготове; бросок и конец перетаскивания снимают оба', () => {
    const { zone } = setup();
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Бросьте файл сюда')).toBeTruthy();
    fireEvent.dragOver(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Отпустите — возьмём файл')).toBeTruthy();
    // Кнопка в «отпустите» скрыта, но в дереве.
    expect(screen.getByRole('button').className).toContain('invisible');
    // Внутрь площадки — не уход. Браузер шлёт вход в новый узел раньше выхода из прежнего.
    const button = screen.getByRole('button');
    fireEvent.dragEnter(button, { dataTransfer: transfer([file('a.gsfx')]) });
    leave(zone(), button);
    expect(screen.getByText('Отпустите — возьмём файл')).toBeTruthy();
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    leave(button, document.body);
    expect(screen.getByText('Бросьте файл сюда')).toBeTruthy();

    fireEvent.dragOver(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    fireEvent.dragEnd(window);
    expect(screen.getByText('Перетащите файл сюда')).toBeTruthy();

    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    fireEvent.dragOver(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    fireEvent.drop(window, { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Перетащите файл сюда')).toBeTruthy();
  });

  it('кнопка описана label и hint, живая область называет выбранный файл', () => {
    const { rerender, onFile } = setup();
    const described = (b: HTMLElement) => b.getAttribute('aria-describedby')!.split(' ')
      .map(id => document.getElementById(id)?.textContent).join(' · ');
    expect(described(screen.getByRole('button', { name: 'Выбрать файл' }))).toBe('Файл сметы · .gsfx или .xml');
    rerender(<FileDropZone label="Файл сметы" hint=".gsfx или .xml" onFile={onFile}
      file={{ name: 'Корпус 3 — ЭОМ.gsfx', size: 2.4 * 1024 * 1024 }} />);
    const live = document.querySelector('[aria-live="polite"]')!;
    expect(live.textContent).toBe('Выбран файл Корпус 3 — ЭОМ.gsfx, 2,4 МБ');
    expect(described(screen.getByRole('button', { name: 'Другой файл' }))).toBe('Файл сметы · .gsfx или .xml');
  });

  it('имя label — только у скрытого поля: getByLabel находит ровно его', () => {
    setup();
    expect(screen.getAllByLabelText('Файл сметы').map(e => e.tagName)).toEqual(['INPUT']);
  });

  it('у скрытого поля имя label и фильтр accept', () => {
    const { input } = setup({ accept: '.gsfx,.xml' });
    expect(input().type).toBe('file');
    expect(input().accept).toBe('.gsfx,.xml');
    expect(input().multiple).toBe(false);
  });

  it('Safari: выход без relatedTarget судится точкой — над скрытой кнопкой «отпустите» не мигает', () => {
    const { zone } = setup();
    vi.spyOn(zone(), 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 400, bottom: 200 } as DOMRect);
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    fireEvent.dragOver(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    // Кнопка скрылась — под курсором сама площадка: вход в неё, затем выход из кнопки без relatedTarget.
    fireEvent.dragEnter(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    leave(screen.getByRole('button'), null, { clientX: 200, clientY: 100 });
    expect(screen.getByText('Отпустите — возьмём файл')).toBeTruthy();
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    leave(zone(), null, { clientX: 450, clientY: 100 });
    expect(screen.getByText('Бросьте файл сюда')).toBeTruthy();
  });

  it('отказ снимается, когда у экрана сменился файл или пошла загрузка', () => {
    const { drop, rerender, onFile } = setup({ check: gsfxOnly, file: { name: 'A.gsfx', size: 10 } });
    drop(transfer([file('счёт.pdf')]));
    expect(screen.getByRole('alert')).toBeTruthy();
    rerender(<FileDropZone label="Файл сметы" onFile={onFile} check={gsfxOnly} file={{ name: 'A.gsfx', size: 10 }}
      busy="Загружаем «A.gsfx»" />);
    expect(screen.queryByRole('alert')).toBeNull();
    rerender(<FileDropZone label="Файл сметы" onFile={onFile} check={gsfxOnly} file={{ name: 'A.gsfx', size: 10 }} />);
    drop(transfer([file('счёт.pdf')]));
    expect(screen.getByRole('alert')).toBeTruthy();
    rerender(<FileDropZone label="Файл сметы" onFile={onFile} check={gsfxOnly} file={null} />);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('выключенная строка файла гасится вся: у знака и размера своего цвета нет', () => {
    setup({ disabled: true, file: { name: 'смета.gsfx', size: 2048 } });
    expect(screen.getByText('2 КБ').className).not.toContain('text-on-surface-variant');
    expect(document.querySelector('svg')!.getAttribute('class') ?? '').not.toContain('text-on-surface-variant');
  });

  const coarsePointer = () => vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(pointer: coarse)', media: query,
    addEventListener: () => {}, removeEventListener: () => {},
  }));

  it('сенсорный экран с броском (iPad): «наготове» и «отпустите» названы', () => {
    coarsePointer();
    const { zone } = setup();
    fireEvent.dragEnter(document.body, { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Бросьте файл сюда')).toBeTruthy();
    fireEvent.dragOver(zone(), { dataTransfer: transfer([file('a.gsfx')]) });
    expect(screen.getByText('Отпустите — возьмём файл')).toBeTruthy();
  });

  it('телефон: нет «перетащите», кнопка называет файл', () => {
    coarsePointer();
    setup({ pickLabel: 'Выбрать файл сметы' });
    expect(screen.queryByText('Перетащите файл сюда')).toBeNull();
    expect(screen.queryByText('или')).toBeNull();
    expect(screen.getByRole('button', { name: 'Выбрать файл сметы' })).toBeTruthy();
  });
});

// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { clipKind, fullText, truncatedAt, truncatedIn, type ClipStyle } from './truncation';

/** Измеритель обрезки ([UI-52] [UI-53]): обрезка объявлена И случилась; поле и свой показ — мимо. */

const style = (s: Partial<ClipStyle>): ClipStyle => ({
  overflow: 'visible', overflowX: 'visible', overflowY: 'visible', textOverflow: 'clip', webkitLineClamp: 'none', ...s,
});

describe('clipKind', () => {
  it('многоточие и line-clamp — обрезка; без overflow — нет', () => {
    expect(clipKind(style({ overflow: 'hidden', textOverflow: 'ellipsis' }))).toBe('ellipsis');
    // Браузер выводит оси из сокращения; ось без сокращения — тоже обрезка.
    expect(clipKind(style({ overflowX: 'hidden', textOverflow: 'ellipsis' }))).toBe('ellipsis');
    expect(clipKind(style({ overflow: 'hidden', webkitLineClamp: '2' }))).toBe('clamp');
    // Многоточие без скрытого переполнения не рисуется — текст выходит за край, а не обрезается.
    expect(clipKind(style({ textOverflow: 'ellipsis' }))).toBeNull();
    // Скрытое переполнение без многоточия — обрезка не объявлена (прокрутка, обрезка рамки).
    expect(clipKind(style({ overflow: 'hidden' }))).toBeNull();
  });
});

afterEach(() => { document.body.innerHTML = ''; });

/** Клетка с разметкой; элементам с data-clip — многоточие и размеры «шире места». */
function cell(html: string, sizes: { scroll?: number; client?: number } = {}): HTMLTableCellElement {
  document.body.innerHTML = `<table><tbody><tr><td>${html}</td></tr></tbody></table>`;
  const td = document.querySelector('td')!;
  td.querySelectorAll<HTMLElement>('[data-clip]').forEach(el => {
    el.style.overflow = 'hidden';
    el.style.textOverflow = el.dataset.clip === 'clamp' ? 'clip' : 'ellipsis';
    if (el.dataset.clip === 'clamp') el.style.setProperty('-webkit-line-clamp', '2');
    const [scroll, client] = [sizes.scroll ?? 300, sizes.client ?? 100];
    Object.defineProperty(el, el.dataset.clip === 'clamp' ? 'scrollHeight' : 'scrollWidth', { configurable: true, value: scroll });
    Object.defineProperty(el, el.dataset.clip === 'clamp' ? 'clientHeight' : 'clientWidth', { configurable: true, value: client });
  });
  return td;
}

describe('truncatedAt', () => {
  it('обрезанный элемент под указателем и у его потомка', () => {
    const td = cell('<span data-clip id="a">Длинное <b id="b">наименование</b></span>');
    expect(truncatedAt(document.getElementById('b')!, td)?.id).toBe('a');
  });

  it('line-clamp — по высоте', () => {
    const td = cell('<p data-clip="clamp" id="a">три строки текста</p>');
    expect(truncatedAt(document.getElementById('a')!, td)?.id).toBe('a');
  });

  it('объявлено, но поместилось — ничего', () => {
    const td = cell('<span data-clip id="a">коротко</span>', { scroll: 100, client: 100 });
    expect(truncatedAt(document.getElementById('a')!, td)).toBeNull();
  });

  it('указатель на отступе клетки: обрезающий потомок один — он, два — ничего', () => {
    expect(truncatedAt(cell('<span data-clip id="a">x</span>'), document.querySelector('td')!)?.id).toBe('a');
    const two = cell('<span data-clip>x</span><span data-clip>y</span>');
    expect(truncatedAt(two, two)).toBeNull();
  });

  it('поле и свой показ — мимо', () => {
    const field = cell('<input data-clip id="a" value="очень длинное значение" />');
    expect(truncatedAt(document.getElementById('a')!, field)).toBeNull();
    const own = cell('<button data-tooltip id="b"><span data-clip id="a">имя</span></button>');
    expect(truncatedAt(document.getElementById('a')!, own)).toBeNull();
    // Значок со своей подсказкой рядом с обрезанным именем: наводят на значок — показ значка, не сетки.
    const icon = cell('<span data-clip>имя</span><span data-tooltip id="i">!</span>');
    expect(truncatedAt(document.getElementById('i')!, icon)).toBeNull();
    // Нативный title — тоже свой показ (браузерный): из компонента клетки храповик его не видит.
    const native = cell('<span data-clip id="a" title="имя">имя</span>');
    expect(truncatedAt(document.getElementById('a')!, native)).toBeNull();
  });

  it('запасной поиск (указатель на отступе) не берёт поле и свой показ', () => {
    const own = cell('<span data-clip data-tooltip>имя</span>');
    expect(truncatedAt(own, own)).toBeNull();
    const field = cell('<input data-clip value="очень длинное значение" />');
    expect(truncatedAt(field, field)).toBeNull();
  });

  it('запасной поиск не заходит во вложенную сетку', () => {
    const outer = cell('<table><tbody><tr><td><span data-clip>чужое</span></td></tr></tbody></table>');
    expect(truncatedAt(outer, outer)).toBeNull();
  });

  it('переполнение меньше пикселя: целые меры равны, многоточие уже нарисовано — обрезано', () => {
    const td = cell('<span data-clip id="a">почти влезло</span>', { scroll: 100, client: 100 });
    const el = document.getElementById('a')!;
    el.getBoundingClientRect = () => ({ width: 100 }) as DOMRect;
    const make = document.createRange.bind(document);
    document.createRange = () => Object.assign(make(), { getBoundingClientRect: () => ({ width: 100.4 }) as DOMRect });
    try {
      expect(truncatedAt(el, td)?.id).toBe('a');
      document.createRange = () => Object.assign(make(), { getBoundingClientRect: () => ({ width: 100 }) as DOMRect });
      expect(truncatedAt(el, td)).toBeNull();
    } finally {
      document.createRange = make;
    }
  });
});

describe('truncatedIn (фокус)', () => {
  it('сам фокусируемый или обрезающий потомок в нём', () => {
    const self = cell('<a href="#" data-clip id="a">ссылка</a>');
    expect(truncatedIn(document.getElementById('a')!, self)?.id).toBe('a');
    const inner = cell('<button id="b"><span data-clip id="a">имя</span></button>');
    expect(truncatedIn(document.getElementById('b')!, inner)?.id).toBe('a');
  });
});

describe('fullText', () => {
  it('сжимает пробелы, переносы оставляет', () => {
    const el = document.createElement('span');
    el.textContent = '  Кабель   ВВГнг\n   3×2,5  ';
    expect(fullText(el)).toBe('Кабель ВВГнг\n3×2,5');
  });
});

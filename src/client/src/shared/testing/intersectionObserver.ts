/**
 * `IntersectionObserver` для jsdom — там его нет вовсе. Без заглушки каждый тест с окном (`Modal` следит за
 * часовыми прокрутки, `useScrollEdges`) падал бы на конструкторе.
 *
 * <p>Сама заглушка молчит: тело окна «помещается», и черт нет. Тест, которому нужна прокрутка, зовёт
 * `intersect(элемент, false)` — наблюдатели этого элемента получают запись, как от браузера.</p>
 *
 * Подключена в `vite.config.ts` (`test.setupFiles`); в окружении node окна нет, и файл ничего не делает.
 */
type Callback = (entries: IntersectionObserverEntry[], observer: IntersectionObserver) => void;

const observers = new Set<StubObserver>();

class StubObserver {
  readonly root: Element | Document | null;
  readonly rootMargin = '0px';
  readonly thresholds = [0];
  readonly targets = new Set<Element>();
  private readonly callback: Callback;

  constructor(callback: Callback, options?: IntersectionObserverInit) {
    this.callback = callback;
    this.root = options?.root ?? null;
    observers.add(this);
  }
  observe(target: Element) { this.targets.add(target); }
  unobserve(target: Element) { this.targets.delete(target); }
  disconnect() { this.targets.clear(); observers.delete(this); }
  takeRecords(): IntersectionObserverEntry[] { return []; }

  fire(target: Element, isIntersecting: boolean) {
    const rect = target.getBoundingClientRect();
    this.callback([{
      target, isIntersecting, time: 0, rootBounds: null,
      boundingClientRect: rect, intersectionRect: rect, intersectionRatio: isIntersecting ? 1 : 0,
    }], this as unknown as IntersectionObserver);
  }
}

/** Сообщить наблюдателям элемента, что он вошёл в видимую часть прокрутки или ушёл из неё. */
export function intersect(target: Element, isIntersecting: boolean) {
  for (const o of [...observers]) if (o.targets.has(target)) o.fire(target, isIntersecting);
}

if (typeof window !== 'undefined' && typeof window.IntersectionObserver !== 'function') {
  window.IntersectionObserver = StubObserver as unknown as typeof IntersectionObserver;
}

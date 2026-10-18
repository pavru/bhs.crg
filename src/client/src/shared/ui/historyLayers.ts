import { useLayoutEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import { WIDE } from '@/shared/hooks/useMedia';

/**
 * Системная «Назад» на телефоне закрывает верхний слой, а не страницу под ним (issue #1418): меню,
 * шаг окна, само окно — и только потом уход со страницы под охраной ухода.
 *
 * <p>Слой при открытии кладёт в историю свою запись — тот же адрес и то же состояние роутера, с
 * пометкой `layer` и глубиной `depth` над записью страницы. «Назад» снимает её, роутер о шаге не
 * узнаёт, а слою достаётся его собственный выход (`onBack`): окно с вводом спросит, шаг окна вернётся
 * к форме. Остался слой открытым — запись кладётся заново. Закрыли его своими средствами (крестик,
 * Esc, выбор пункта) — запись снимается нашим же шагом назад, который никто, кроме нас, не видит.
 * Так «Назад» и «Вперёд» не расходятся с видимым, а слой, открытый на первой записи вкладки
 * (приложение с ярлыка), не уводит «Назад» из приложения.</p>
 *
 * <p>Только на телефоне: там «Назад» — системный жест, которым закрывают всё. На рабочем столе окно
 * закрывают Esc и крестиком, а запись истории под каждым окном стирала бы «Вперёд».</p>
 *
 * <p>Порядок с охраной ухода: свой шаг слоёв проглатывается раньше всех, затем охрана шага страницы
 * (`NavigationGuard`), затем закрытие слоя. Записи слоёв лежат на ТОМ ЖЕ `idx`, что страница, —
 * охрана их шаги не видит, а слой не видит её: каждый шаг достаётся ровно одному из них.</p>
 */

interface Entry { idx?: unknown; layer?: string; depth?: number; usr?: unknown; key?: unknown }

interface Layer {
  id: string;
  back: { current: () => boolean };
  /** Где лежит запись слоя: `idx` страницы и глубина над ней. */
  idx: unknown;
  depth: number;
  pushed: boolean;
  /** Слой ответил «Назад» закрытием и вот-вот уйдёт: его больше нет для истории. */
  closing: boolean;
  /** Вопрос охраны ухода — записи не кладёт, см. `holdNextLayer`. */
  held: boolean;
}

const stack: Layer[] = [];
let seq = 0;
/** Наши шаги назад, ещё не дошедшие: их `popstate` проглатывается. */
let ownBacks = 0;
/** Адрес и состояние роутера, которые переезжают на запись, куда ведёт наш шаг назад. */
let carry: { url: string; usr: unknown; key: unknown } | null = null;
/** Где мы сейчас — последняя известная запись. Нужна, чтобы знать направление шага. */
let here: Entry = {};
/** Записи слоёв, снятые «Назад»: «Вперёд» на такую запись — шаг в никуда, его отменяем. */
const consumed = new Set<string>();
/** Шаг, которым мы проходим мёртвую запись: пока он идёт, историю не выравниваем. */
let skipping = false;
let previous: Entry = {};
/** Уходим со страницы мимо слоёв — записи до прихода не трогаем. */
let suspendedAt: unknown = undefined;
let suspended = false;
let hold = false;

const entry = (s: unknown): Entry => (s && typeof s === 'object' ? s : {}) as Entry;
const depthOf = (e: Entry) => (e.layer ? e.depth ?? 0 : 0);
const idxOf = (e: Entry) => (typeof e.idx === 'number' ? e.idx : 0);
const live = () => stack.filter(l => !l.closing && !l.held);

function pushEntry(l: Layer) {
  const st = entry(window.history.state);
  const depth = depthOf(st) + 1;
  window.history.pushState({ ...st, layer: l.id, depth }, '');
  Object.assign(l, { idx: st.idx, depth, pushed: true });
  here = entry(window.history.state);
}

/**
 * Привести историю к открытым слоям: над записью страницы — по записи на каждый, по порядку
 * открытия. Лишние сверху (слой закрыли своими средствами) снимаются нашим шагом назад, недостающие
 * кладутся.
 */
function sync() {
  if (ownBacks > 0 || suspended || skipping) return;
  const st = entry(window.history.state);
  const open = live();
  const cur = depthOf(st);
  let k = 0;
  while (k < open.length && k < cur && open[k].pushed && open[k].idx === st.idx && open[k].depth === k + 1) k++;
  if (cur > k) {
    // Роутер видит ту запись, с которой мы уходим (`replace` поверх записи слоя меняет её адрес), —
    // и запись, куда придём, обязана стать ею, иначе адрес разошёлся бы с показанным.
    carry = { url: window.location.href, usr: st.usr, key: st.key };
    ownBacks++;
    window.history.go(k - cur);
    return;
  }
  for (const l of open.slice(k)) pushEntry(l);
}

let syncQueued = false;
function scheduleSync() {
  if (syncQueued) return;
  syncQueued = true;
  queueMicrotask(() => { syncQueued = false; sync(); });
}

/**
 * Первым в цепочке `popstate`: наш собственный шаг назад. Его не видит ни охрана, ни роутер.
 * Возвращает `true`, если шаг проглочен.
 */
export function layersFirst(e: PopStateEvent): boolean {
  previous = here;
  here = entry(e.state);
  skipping = false;
  if (suspended && here.idx !== suspendedAt) suspended = false;
  if (ownBacks === 0) return false;
  e.stopImmediatePropagation();
  if (--ownBacks === 0) {
    const st = entry(window.history.state);
    if (carry && (carry.url !== window.location.href || carry.key !== st.key)) {
      window.history.replaceState({ ...st, usr: carry.usr, key: carry.key }, '', carry.url);
      here = entry(window.history.state);
    }
    carry = null;
    scheduleSync();
  }
  return true;
}

/** После охраны шага: «Назад» снял запись верхнего слоя — слой выходит; пришли на мёртвую запись — проходим её. */
export function layersAfter(e: PopStateEvent) {
  const to = here;
  const open = live();
  const top = open.at(-1);
  if (top?.pushed && top.idx === to.idx && depthOf(to) < top.depth) {
    e.stopImmediatePropagation();
    for (const l of open) if (l.depth > depthOf(to)) l.pushed = false;
    if (top.back.current()) {
      top.closing = true;
      consumed.add(top.id);
      // Слой сказал «закрываюсь», но не ушёл (родитель не закрыл окно) — вернуть ему запись.
      setTimeout(() => { if (stack.includes(top) && top.closing) { top.closing = false; sync(); } }, 1000);
    }
    scheduleSync();
    return;
  }
  // Запись слоя, которого уже нет (его закрыл переход по ссылке из меню, или «Назад»): шаг на неё ничего
  // не показывает — проходим её туда же, куда шли. «Вперёд» на запись, снятую «Назад», ведёт в никуда:
  // за ней только такие же, — этот шаг отменяем.
  if (to.layer && !open.some(l => l.pushed && l.id === to.layer)) {
    const forward = idxOf(to) !== idxOf(previous) ? idxOf(to) > idxOf(previous) : depthOf(to) > depthOf(previous);
    if (forward && consumed.has(to.layer)) { ownBacks++; window.history.back(); return; }
    // Шаг «Вперёд» за последнюю запись не придёт вовсе — ждём его не дольше, чем он идёт.
    skipping = true;
    setTimeout(() => { if (skipping) { skipping = false; scheduleSync(); } }, 300);
    if (forward) window.history.forward();
    else window.history.back();
  }
}

/**
 * Уйти со страницы на `delta` записей мимо записей слоёв над ней: выход страницы «туда, откуда
 * пришли» и решённый уход охраны. Слои страницы уходят вместе с ней — их записи до прихода не
 * трогаем, иначе закрывшийся вопрос ухода шагнул бы назад наперерез.
 */
export function goPastLayers(delta: number) {
  const st = entry(window.history.state);
  suspended = true;
  suspendedAt = st.idx;
  setTimeout(() => { suspended = false; scheduleSync(); }, 1000);
  window.history.go(delta < 0 ? delta - depthOf(st) : delta);
}

/**
 * Следующий открытый слой — вопрос охраны ухода, заданный на шаге истории: записи он не кладёт.
 * Иначе «Уйти» ушло бы на его запись, а при шаге «Вперёд» его запись стёрла бы ту, куда уходят.
 * «Назад» при таком вопросе — снова шаг под охраной: вопрос остаётся.
 */
export function holdNextLayer() {
  hold = true;
  setTimeout(() => { hold = false; }, 1000);
}

const phone = () => typeof window.matchMedia === 'function' && !window.matchMedia(WIDE).matches;

/**
 * Слой истории: пока `open` (и это телефон), системная «Назад» достаётся `onBack`, а не странице.
 * `onBack` возвращает `true`, если слой закрывается, и `false`, если остаётся (спросил о правках,
 * вернулся с шага, занят).
 */
export function useHistoryLayer(open: boolean, onBack: () => boolean) {
  const back = useRef(onBack);
  useLayoutEffect(() => { back.current = onBack; });
  useLayoutEffect(() => {
    if (!open || !phone()) return;
    const layer: Layer = { id: `l${++seq}`, back, idx: undefined, depth: 0, pushed: false, closing: false, held: hold };
    // Удержание гаснет после этой отрисовки, а не на первом слое: StrictMode монтирует его дважды, и
    // второй экземпляр вопроса иначе положил бы запись.
    if (hold) queueMicrotask(() => { hold = false; });
    stack.push(layer);
    scheduleSync();
    return () => {
      stack.splice(stack.indexOf(layer), 1);
      scheduleSync();
    };
  }, [open]);
}

/**
 * Хост внутри роутера: следит, где мы, после каждого перехода. Переход `replace` поверх записи слоя
 * стирает её пометку — пометка возвращается, иначе «Назад» ушла бы мимо слоя; а если слоя уже нет,
 * `sync` снимет эту запись, перенеся её адрес на запись страницы.
 */
export function HistoryLayersHost() {
  const { key } = useLocation();
  useLayoutEffect(() => {
    const st = entry(window.history.state);
    if (!st.layer && here.layer && st.idx === here.idx) {
      window.history.replaceState({ ...st, layer: here.layer, depth: here.depth }, '');
    }
    here = entry(window.history.state);
    scheduleSync();
  }, [key]);
  return null;
}

/** Для тестов: сбросить состояние модуля. */
export function resetHistoryLayers() {
  stack.length = 0;
  ownBacks = 0;
  carry = null;
  here = {};
  previous = {};
  consumed.clear();
  suspended = false;
  skipping = false;
  hold = false;
}

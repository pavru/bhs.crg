import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';
import { apiError } from '@/shared/utils/apiError';
import { useSnackbar } from '@/shared/ui/Snackbar';

/**
 * Очередь записи формы счёта — одна на счёт (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5).
 *
 * <p><b>Части две — реквизиты и строки, — версия у счёта одна.</b> Поэтому в полёте не больше одного
 * запроса, и если грязны обе части, первыми идут реквизиты: строки запоминают соответствия за поставщиком,
 * который ЛЕЖИТ в счёте (#1657), и берут версию из ответа реквизитов. Две независимые очереди спорили бы за
 * одну версию.</p>
 *
 * <p><b>Пометка — «часть готова к записи»</b> (`mark`): её ставит повод — уход с поля, уход из строки, выбор,
 * вставка. Пометка во время записи ставит часть в очередь снова. Тело записи собирает шаг В МОМЕНТ
 * ОТПРАВКИ — из того, что на экране, а не из того, что было при пометке: набранное во время записи не
 * теряется и уходит следующей записью (#1656).</p>
 *
 * <p><b>Отказ останавливает очередь.</b> Строки после отказа реквизитов не уходят. Отказ держится, пока
 * человек не исправит значение (новая пометка) или не нажмёт «Повторить». Своих циклов повтора у сбоя связи
 * нет: поток запросов в мёртвую связь не нужен.</p>
 *
 * <p>`flush` дописывает всё грязное — его зовут смена счёта, уход, «Разобран» в режиме «Изменить» (D-12.4) и
 * «Сохранить» оплаченного черновика в «Сроках и оплате» (D-12.5).</p>
 */

export type SavePart = 'header' | 'lines';

/** Запись реквизитов: вид из ответа. Отказ — исключением (его разберёт очередь). */
export type HeaderStep = () => Promise<InvoiceView>;

/** Запись строк — с видом из ответа реквизитов, если они шли прямо перед ней. Отказ — исключением. */
export type LinesStep = (after: InvoiceView | null) => Promise<InvoiceView>;

/** Состояние записи — то, что говорит носитель в сводке (D-12.1). */
export type SaveStatus =
  | { kind: 'idle' }
  | { kind: 'saving' }
  /** Сбой связи (или сервера — 5xx): «Не сохранено — повторить». */
  | { kind: 'failed' }
  /** Отказ сервера его словами: «Не сохранено: <отказ>». */
  | { kind: 'refused'; text: string };

/** Чем кончилась `flush`: всё ли записалось. */
export type SaveOutcome =
  | { ok: true }
  /** `reported` — о сбое уже сказал снэкбар (5xx с «Сообщить»). */
  | { ok: false; status: Exclude<SaveStatus, { kind: 'idle' | 'saving' }>; reported: boolean };

/** Что очередь видит в форме на миг решения — свежим, а не из замыкания пометки. */
export interface SaveSides {
  header: HeaderStep;
  /** `null` — таблица строк своего шага не отдала (заперта, исправление, ещё не смонтирована). */
  lines: LinesStep | null;
  dirty: Record<SavePart, boolean>;
}

/** Ответ записи, которой не дошла связь, и ответ сервера — различает очередь: от этого зависит носитель. */
export function classify(error: unknown): { status: Exclude<SaveStatus, { kind: 'idle' | 'saving' }>; report: boolean } {
  const status = (error as { response?: { status?: number } })?.response?.status;
  if (status === undefined) {
    // Без ответа — связь, только если это запрос axios. Иное исключение — ошибка программы (бросило уже после
    // ответа, при слиянии или сборке тела): «нет связи» спрятало бы её, а запись, возможно, уже легла.
    if ((error as { isAxiosError?: boolean })?.isAxiosError) return { status: { kind: 'failed' }, report: false };
    console.error(error);
    return { status: { kind: 'failed' }, report: true };
  }
  // Сбой сервера — снэкбаром с «Сообщить» по `traceId`: носителю его некуда поставить (D-12.7).
  if (status >= 500) return { status: { kind: 'failed' }, report: true };
  return { status: { kind: 'refused', text: apiError(error, 'сервер отказал') }, report: false };
}

/** Строки грязные, а шага нет — отказ, а не «сохранено» (ревью PR #1787). */
export const LINES_NOT_READY = 'строки правлены, но таблица строк к записи не готова';

/** Записи прошли, а правка так и не легла: набранное менялось всю `flush`. Носитель обязан это назвать — «Сохранено» солгало бы. */
export const FLUSH_UNFINISHED = 'правка менялась, пока шла запись, — запись не закончена';

/**
 * Очередь без React — её и проверяют тесты. `read` отдаёт стороны формы на миг решения; `notify` — новое
 * состояние; `report` — сбой сервера, о котором скажет снэкбар.
 *
 * @param rendered ждёт отрисовки после записи. ⚠️ Без неё следующая запись взяла бы шаг из замыкания ДО ответа:
 *   основа черновика ещё не приняла свой ответ (`adopt` — состояние), запись назвала бы прежнюю версию и получила
 *   409 от самой себя (#1656).
 */
export function createSaveQueue(
  read: () => SaveSides,
  notify: (status: SaveStatus, pending: boolean) => void,
  report: (error: unknown) => void = () => {},
  rendered: () => Promise<void> = () => Promise.resolve(),
) {
  const marked = new Set<SavePart>();
  let status: SaveStatus = { kind: 'idle' };
  let running = false;
  // Ответ реквизитов, ещё не принятый строками: строки, идущие прямо за ними, называют его версию.
  let after: InvoiceView | null = null;
  let waiters: ((outcome: SaveOutcome) => void)[] = [];
  // Вид из ответа последней записи этой `flush`: переход следом («Разобран» режима «Изменить», D-12.4) называет его
  // версию. Из кадра её брать нельзя — кадр с ответом мог ещё не отрисоваться, и переход получил бы 409 от своей записи.
  let written: InvoiceView | null = null;

  const stopped = () => status.kind === 'failed' || status.kind === 'refused';
  const pending = () => running || marked.size > 0 || stopped();
  const set = (next: SaveStatus) => { status = next; notify(next, pending()); };

  function settle(outcome: SaveOutcome) {
    const ready = waiters;
    waiters = [];
    ready.forEach(resolve => resolve(outcome));
  }

  /** Следующая часть к записи: реквизиты раньше строк; помеченное, но уже чистое снимается. */
  function next(sides: SaveSides): SavePart | null {
    for (const part of ['header', 'lines'] as const) {
      if (!marked.has(part)) continue;
      if (sides.dirty[part]) return part;
      marked.delete(part);
    }
    return null;
  }

  /** Очередь встала: опустела (`null`) или остановилась на отказе. Признак полёта снимается ДО слова наружу. */
  function stop(refused: Exclude<SaveStatus, { kind: 'idle' | 'saving' }> | null, reported = false) {
    running = false;
    after = null;
    set(refused ?? { kind: 'idle' });
    settle(refused ? { ok: false, status: refused, reported } : { ok: true });
  }

  async function pump(): Promise<void> {
    if (running || stopped()) return;
    running = true;
    for (;;) {
      const sides = read();
      const part = next(sides);
      if (part === null) { stop(null); return; }
      // Строки грязные, а шага нет — отказ ДО реквизитов: иначе уход со счёта прошёл бы, унеся строки.
      if (marked.has('lines') && sides.dirty.lines && !sides.lines) { stop({ kind: 'refused', text: LINES_NOT_READY }); return; }
      marked.delete(part);
      set({ kind: 'saving' });
      try {
        if (part === 'header') written = after = await sides.header();
        else {
          const from = after;
          after = null;
          written = await sides.lines!(from);
        }
        await rendered();
      } catch (error) {
        // Часть остаётся помеченной: «Повторить» и следующая пометка отправят её снова.
        marked.add(part);
        const { status: refused, report: tell } = classify(error);
        if (tell) report(error);
        stop(refused, tell);
        return;
      }
    }
  }

  return {
    /** Часть готова к записи. Новая пометка — и повтор после отказа: человек исправил значение. */
    mark(part: SavePart) {
      marked.add(part);
      set(stopped() ? { kind: 'idle' } : status);
    },
    /** Начать запись помеченного — зовёт хук после отрисовки, когда стороны формы свежие. */
    pump,
    /** Дописать всё грязное. Обещание кончается, когда очередь опустела или остановилась. */
    flush(): Promise<SaveOutcome> {
      if (!running) written = null;
      const { dirty } = read();
      (['header', 'lines'] as const).forEach(part => { if (dirty[part]) marked.add(part); });
      if (stopped()) set({ kind: 'idle' });
      if (!running && marked.size === 0) return Promise.resolve({ ok: true });
      const done = new Promise<SaveOutcome>(resolve => waiters.push(resolve));
      void pump();
      return done;
    },
    /** Записи прошли, а грязь осталась (`FLUSH_UNFINISHED`) — встать отказом: грязное остаётся помеченным. */
    refuse(text: string) {
      const { dirty } = read();
      (['header', 'lines'] as const).forEach(part => { if (dirty[part]) marked.add(part); });
      set({ kind: 'refused', text });
    },
    /** «Повторить» — снова то, что не дошло. */
    retry() {
      if (stopped()) set({ kind: 'idle' });
      void pump();
    },
    /** Грязного больше нет (перечитали, отменили), а носитель всё ещё говорит об отказе — снять. */
    forgetRefusal() {
      if (!stopped()) return;
      const { dirty } = read();
      if (dirty.header || dirty.lines) return;
      marked.clear();
      set({ kind: 'idle' });
    },
    get status() { return status; },
    /** Вид из ответа последней записи начатой `flush`; `null` — она ничего не писала. */
    get written() { return written; },
    /** Есть ли что дописывать: помеченное, запись в полёте или остановка на отказе. */
    get pending() { return pending(); },
  };
}

export type SaveQueue = ReturnType<typeof createSaveQueue>;

/** Сколько раз `flush` дописывает набранное во время своей же записи, прежде чем сказать «не дошло». */
const FLUSH_ROUNDS = 3;

/**
 * Очередь записи формы: реквизиты дают свой шаг, строки регистрируют свой (`lines.current`) — таблица строк
 * держит свой черновик и свою основу сама.
 *
 * @param auto пишет ли форма сама (черновик и отклонённый без оплаты, D-12.3). Без него поводы записи не
 *   действуют, и пишет только `flush` — «Разобран» режима «Изменить» и «Сохранить» в «Сроках и оплате»; отказ
 *   называет носитель словами сервера (тоста нет — он повторял бы носитель).
 */
export function useInvoiceSave({ headerDirty, linesDirty, saveHeader, auto }: {
  headerDirty: boolean;
  linesDirty: boolean;
  saveHeader: HeaderStep;
  auto: boolean;
}) {
  const lines = useRef<LinesStep | null>(null);
  const toast = useSnackbar();
  // Стороны — слойным эффектом: запись, начатая сразу за кадром, застала бы обычный эффект неотработавшим.
  const sides = useRef<SaveSides>({ header: saveHeader, lines: null, dirty: { header: headerDirty, lines: linesDirty } });
  useLayoutEffect(() => {
    sides.current = { header: saveHeader, lines: lines.current, dirty: { header: headerDirty, lines: linesDirty } };
  });
  const autoNow = useRef(auto);
  useLayoutEffect(() => { autoNow.current = auto; });
  // Что очередь говорит наружу — состоянием: сама очередь живёт вне отрисовки и читается только в событиях и эффектах.
  const [said, setSaid] = useState<{ status: SaveStatus; pending: boolean }>({ status: { kind: 'idle' }, pending: false });
  // Ждущие отрисовки после записи — отпускаются слойным эффектом, когда стороны формы уже свежие.
  const commits = useRef<(() => void)[]>([]);
  const [, setRender] = useState(0);
  useLayoutEffect(() => {
    const ready = commits.current;
    commits.current = [];
    ready.forEach(resolve => resolve());
  });
  // Форма ушла посреди записи (счёт закрыт, пересоздан ключом) — отрисовки больше не будет: ждущих отпустить, иначе
  // очередь не встанет никогда, и `flush`, которого ждёт уход, не кончится.
  const gone = useRef(false);
  useEffect(() => () => {
    gone.current = true;
    const ready = commits.current;
    commits.current = [];
    ready.forEach(resolve => resolve());
  }, []);
  const made = useRef<SaveQueue | null>(null);
  const queue = () => {
    made.current ??= createSaveQueue(
      () => ({ ...sides.current, lines: lines.current }),
      (status, pending) => setSaid({ status, pending }),
      error => toast.apiError(error, 'Счёт не сохранён'),
      () => (gone.current ? Promise.resolve()
        : new Promise<void>(resolve => { commits.current.push(resolve); setRender(n => n + 1); })),
    );
    return made.current;
  };
  // Пометка ставится в обработчике события — тем же, что меняет черновик. Писать сразу нельзя: стороны формы
  // ещё прежние. Запись начинается после отрисовки, когда тело собирается из нового.
  const [kick, setKick] = useState(0);
  useEffect(() => { if (kick > 0) void queue().pump(); }, [kick]); // eslint-disable-line react-hooks/exhaustive-deps -- очередь одна на форму
  // Перечитали или отменили — грязного нет, и носитель не держит отказ о том, чего уже нет на экране.
  useEffect(() => { if (!headerDirty && !linesDirty) made.current?.forgetRefusal(); }, [headerDirty, linesDirty]);

  function mark(part: SavePart) {
    if (!autoNow.current) return;
    queue().mark(part);
    setKick(k => k + 1);
  }

  /**
   * Записать всё; `true` — записано. Набранное, пока шла запись, дописывается тем же вызовом: уход после «записано»
   * иначе унёс бы его. Отказ называет носитель в сводке — он есть у каждой модели записи (D-12.4, D-12.5).
   */
  async function flush(): Promise<boolean> {
    for (let round = 0; round < FLUSH_ROUNDS; round += 1) {
      const outcome = await queue().flush();
      if (!outcome.ok) return false;
      const { dirty } = sides.current;
      if (!dirty.header && !dirty.lines) return true;
    }
    queue().refuse(FLUSH_UNFINISHED);
    return false;
  }

  const { status } = said;
  const saving = status.kind === 'saving';
  return {
    lines, mark, flush, status, saving,
    /** Вид из ответа последней записи `flush` — версия для перехода следом; `null` — запись не шла. */
    written: () => made.current?.written ?? null,
    retry: () => queue().retry(),
    /** Пуста ли очередь: нечего дописывать, ничего не в полёте, отказа нет, правок нет. */
    empty: !said.pending && !headerDirty && !linesDirty,
    /** Явная запись — «Сохранить» в «Сроках и оплате» и «Сохранить и перейти» охраны ухода. */
    save: flush,
  };
}

/** Что носитель записи говорит — словами (D-12.1); то же читают подсказка узкой сводки и тесты. */
export function sayStatus(status: SaveStatus, savedAt: string): string {
  switch (status.kind) {
    case 'idle': return `Сохранено · ${savedAt}`;
    case 'saving': return 'Сохраняю…';
    case 'failed': return 'Не сохранено — повторить';
    case 'refused': return `Не сохранено: ${status.text}`;
  }
}

/**
 * Как пишется форма счёта (D-12.3): `auto` — сама; `read` — разобранный читается, правка — через «Изменить»;
 * `edit` — режим «Изменить» разобранного, запись — «Разобран» (D-12.4); `terms` — оплаченный черновик, запись —
 * «Сохранить» в «Сроках и оплате» (D-12.5); `null` — никак.
 */
export type SaveModel = 'auto' | 'read' | 'edit' | 'terms' | null;

/**
 * Кому автосохранение (D-12.3): черновику и отклонённому без оплаты. Разобранный читается и правится режимом
 * «Изменить», оплаченный черновик открытого периода — явной записью: промежуточный итог получил бы отказ
 * «оплаченный обязан оставаться сведённым». Запертому, исправлению и без права — никак.
 *
 * <p>Режим «Изменить» держится, пока его не закрыли, а не пока счёт разобран: «Разобран» в режиме пишет правку,
 * сервер на миг опускает счёт в черновик, и модель не должна переключиться на автосохранение посреди шага.</p>
 *
 * @param writable форма пишет: есть право, счёт не заперт и не удалён, исправления нет.
 * @param editing открыт режим «Изменить» разобранного.
 */
export function saveModel(view: Pick<InvoiceView, 'requisites' | 'payment'>, writable: boolean, parsed: boolean,
  editing = false): SaveModel {
  if (!writable) return null;
  if (editing) return 'edit';
  if (parsed) return 'read';
  if (view.payment.paid) return 'terms';
  return 'auto';
}

/**
 * Правки реквизитов после ответа своей записи (#1656 для шапки): ушедшие снимаются, набранные во время
 * записи — остаются. Поле, правленное после отправки, держит новое значение и пишется следующей записью.
 */
export function editsAfterSave(sent: Record<string, unknown>, now: Record<string, unknown>): Record<string, unknown> {
  const left: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(now))
    if (!(key in sent) || JSON.stringify(sent[key]) !== JSON.stringify(value)) left[key] = value;
  return left;
}

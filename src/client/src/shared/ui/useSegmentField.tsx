import { Fragment, useEffect, useId, useRef, useState } from 'react';

/**
 * Движок поля из сегментов — дата (ДД.ММ.ГГГГ) и время (ЧЧ:ММ) на одном (issue #1468). Вынесен из
 * `DateInput` без изменения поведения: набор с автопереходом, Backspace назад, разделитель как
 * переход, ↑/↓, вставка целиком, Ctrl+A / C / X, Esc к значению при входе, фиксация при уходе из ПОЛЯ.
 * Каждое из этого чинили по ревью (#1460, #1463, #1465, #1542, #1553, #1556), и второй копии
 * обработчиков для времени здесь нет нарочно: починка одной копии не дошла бы до другой.
 *
 * <p>Что делает поле датой или временем — модель (`SegmentModel`): длины, разбор, проверка, шаг
 * стрелкой, вставка. Календарь, рамка и крестик — у компонента поля.</p>
 */

/** Что дают сегменты: значение, пустоту (явная очистка) или повод для ошибки под полем. */
export type SegmentReading =
  | { kind: 'value'; value: string }
  | { kind: 'empty' }
  | { kind: 'partial' }
  | { kind: 'impossible' };

export type SegmentValues<K extends string> = Record<K, string>;

export interface SegmentModel<K extends string> {
  /** Видимые сегменты в порядке показа: у en-US за месяцем идёт день. */
  order: readonly K[];
  /** Разделитель между сегментами на экране. */
  sep: string;
  /** Обозначения пустых сегментов: ДД / ЧЧ. */
  labels: Record<K, string>;
  /** Имена сегментов для чтения с экрана: «день», «час». */
  names: Record<K, string>;
  /** Сколько цифр в сегменте; набравший их переводит к следующему. */
  len: Record<K, number>;
  /**
   * Первая цифра больше этой сегмент начать не может — сразу «0X» и дальше: дня 40 и часа 30 не
   * бывает. Не задано — сегмент ждёт всех цифр.
   */
  padAbove: Partial<Record<K, number>>;
  /** Клавиши, переводящие к следующему сегменту: «18.» или «4:». */
  separators: readonly string[];
  /** Пустые сегменты — одним объектом на модель: с ним сравнивают и начинают заново. */
  empty: SegmentValues<K>;
  /** Значение → сегменты; null — значение не дата (не время): его показывают текстом. */
  of(value: string | null | undefined): SegmentValues<K> | null;
  read(s: SegmentValues<K>): SegmentReading;
  /** Дописать сегмент при уходе из него («4» → 04, «26» → 2026); null — дописывать нечего. */
  fix(part: K, typed: string): string | null;
  /** ↑/↓ — значение сегмента на шаг; null — стрелка этот сегмент не трогает. */
  step(part: K, delta: 1 | -1, s: SegmentValues<K>): string | null;
  /** Вставка целиком в любой сегмент; null — не значение, вставку разбирает сегмент. */
  paste(text: string): SegmentValues<K> | null;
  /** Всё значение текстом — для Ctrl+C поверх выделенного; null — не целое. */
  text(s: SegmentValues<K>): string | null;
  problems: { partial: string; impossible: string; foreign: string };
  /** Объявление скринридеру после Ctrl+A: «Выделена вся дата». */
  selectedAll: string;
}

interface SegmentFieldOptions {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onProblem?: (message: string | null) => void;
  onIncomplete?: (message: string | null) => void;
  onCommit?: (value: string) => void;
  disabled: boolean;
  readOnly: boolean;
  required: boolean;
  invalid: boolean;
  /**
   * Свои клавиши поля — раньше движка (Alt+↓ открывает календарь); true — обработана. Движок их не
   * знает: у времени календаря нет.
   */
  onKey?: (e: React.KeyboardEvent) => boolean;
}

export function useSegmentField<K extends string>(model: SegmentModel<K>, {
  label, value, onChange, onProblem, onIncomplete, onCommit, disabled, readOnly, required, invalid, onKey,
}: SegmentFieldOptions) {
  /**
   * Набранное человеком — вместе со значением, от которого его начали набирать (`base`). Показанные
   * сегменты ВЫЧИСЛЯЮТСЯ (issue #858): набранное, пока значение то же, иначе — прямо из значения.
   *
   * <p>Раньше сегменты были копией значения, которую эффект переливал «когда не редактируют».
   * Копия жила своей жизнью между приходом нового значения и запуском эффекта — то есть кадр поле
   * показывало прежнее значение.</p>
   *
   * <p>Недописанное и несуществующее при уходе из поля ОСТАЮТСЯ видимыми, с ошибкой под полем
   * (issue #1460, решение владельца): раньше они молча откатывались к прежнему значению, и человек
   * считал ввод принятым. Отданное значение меняет `value` — и набранное уходит само, по `base`.
   * Значение сменили снаружи (сброс формы, другая запись) — тоже: набранное относилось к прежнему.</p>
   */
  const [typed, setTyped] = useState<(SegmentValues<K> & { base: string }) | null>(null);
  const [focused, setFocused] = useState(false);
  /**
   * Ctrl+A выделил всё значение (issue #1463): у сегментов общего выделения нет, поэтому оно своё.
   * Delete или Backspace стирают значение, цифра начинает его заново, Ctrl+C копирует текстом, Ctrl+X
   * копирует и стирает, Esc, стрелка и щелчок выделение снимают.
   */
  const [allSelected, setAllSelected] = useState(false);

  const inputs = useRef<Partial<Record<K, HTMLInputElement | null>>>({});
  const blurTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  /**
   * Значение, с которым человек вошёл в поле, — к нему возвращает Esc. Не текущее: сегменты отдают
   * значение, как только оно складывается, и «31» поверх 10.2026 успевает отдать 31.10 по пути к
   * 31.02 (поймано живой пробой окна оплаты). Запоминается при входе без набранного: вернувшись к
   * полю с ошибкой, человек правит тот же ввод, и прежним остаётся прежнее.
   */
  const startValue = useRef(value ?? '');
  /**
   * Значение на сейчас — для `onCommit` из таймера ухода. Не из замыкания: уход сам дописывает
   * сегмент («26» → 2026), и значение меняется уже после того, как таймер поставлен.
   */
  const latest = useRef(value ?? '');
  useEffect(() => { latest.current = value ?? ''; });
  /**
   * Фиксация (`onCommit`) — уход из ПОЛЯ, а не из сегмента: значок календаря, крестик и сам календарь
   * — ещё поле, и шаг Tab'ом на значок не поправлял бы другую границу периода посреди правки (ревью
   * PR #1553). И только изменённого: проход Tab'ом по сохранённой паре форму не пачкает.
   */
  const groupRef = useRef<HTMLDivElement>(null);
  /** Всплывающая часть поля (календарь) — тоже поле: уход в неё не фиксирует. */
  const contentRef = useRef<HTMLDivElement>(null);
  const commitTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const inside = useRef(false);
  const entered = useRef('');
  const withinField = (to: EventTarget | null) => to instanceof Node
    && (!!groupRef.current?.contains(to) || !!contentRef.current?.contains(to));
  function onFieldFocus() {
    clearTimeout(commitTimer.current);
    if (!inside.current) { inside.current = true; entered.current = value ?? ''; }
  }
  /** Зафиксировать значение на сейчас, если оно отличается от того, с которым вошли или фиксировали. */
  function commit() {
    clearTimeout(commitTimer.current);
    if (latest.current === entered.current) return;
    entered.current = latest.current;
    onCommit?.(latest.current);
  }
  function onFieldBlur(e: React.FocusEvent) {
    if (withinField(e.relatedTarget)) return;
    const leave = () => { inside.current = false; commit(); };
    // Фокус ушёл на известный элемент (кнопка, крестик, поле рядом) — фиксация сразу, в этом же
    // событии: щелчок «Добавить» / «Применить» читает уже поправленное, а таймер окошко, закрывшись,
    // погасило бы вовсе (issue #1465). Куда — неизвестно (щелчок мимо, окно браузера) — таймером:
    // фокус может вернуться в поле, и тогда ухода не было.
    if (e.relatedTarget) leave();
    else commitTimer.current = setTimeout(leave, 120);
  }
  useEffect(() => () => clearTimeout(commitTimer.current), []);

  const fromValue = model.of(value);
  const draft = typed && typed.base === (value ?? '') ? typed : null;
  // Значение не дата («вчера», «2026/13») — показывается текстом, пока его не начали переписывать.
  const foreign = fromValue === null && draft === null;
  const shown: SegmentValues<K> = draft ?? fromValue ?? model.empty;
  // Функциональные апдейтеры: вставка пишет все сегменты разом, и сборка от отрендеренного `shown`
  // молча потеряла бы первую правку (поймано ревью PR #863).
  const setSeg = (part: K, v: string) =>
    setTyped(prev => ({ ...(prev && prev.base === (value ?? '') ? prev : { ...shown, base: value ?? '' }), [part]: v }));

  // Ошибка набранного. «Не дописано» — только после ухода из поля: посреди ввода значение и должно
  // быть недописанным. Несуществующее — сразу, как только все сегменты набраны.
  const reading = draft ? model.read(draft) : null;
  const problem = reading?.kind === 'impossible' ? model.problems.impossible
    : reading?.kind === 'partial' && !focused ? model.problems.partial
      : foreign && !readOnly && !disabled ? model.problems.foreign : null;
  useEffect(() => { onProblem?.(problem); }, [onProblem, problem]);
  const incomplete = reading?.kind === 'impossible' ? model.problems.impossible
    : reading?.kind === 'partial' ? model.problems.partial : null;
  useEffect(() => { onIncomplete?.(incomplete); }, [onIncomplete, incomplete]);

  /**
   * Есть что стирать (issue #1463): значение, «не дата» или недописанное поверх пустого. Кнопкам —
   * крестику и «Очистить» календаря — только у необязательного правимого поля: обязательное стирают
   * сегментами или Ctrl+A → Delete, и пустоту называет форма.
   */
  const hasContent = (value ?? '') !== '' || (!!draft && model.order.some(part => draft[part] !== ''));
  const clearable = hasContent && !required && !readOnly && !disabled;

  function onFocus() {
    clearTimeout(blurTimer.current);
    if (!focused && !draft) startValue.current = value ?? '';
    setFocused(true);
  }
  function onBlur() {
    // Delay so focus transitions between segments don't flicker focused=false. Набранное, которое
    // дало значение или пустоту, уходит вместе с фокусом; недописанное и несуществующее — остаётся с ошибкой.
    blurTimer.current = setTimeout(() => {
      setFocused(false);
      setAllSelected(false);
      setTyped(prev => {
        const kind = prev && model.read(prev).kind;
        return kind === 'partial' || kind === 'impossible' ? prev : null;
      });
    }, 120);
  }

  /**
   * Отдать значение. Фиксация видит его сразу, не дожидаясь отрисовки: уход и Enter дописывают
   * сегмент («26» → 2026) в том же событии, где фиксируют (issue #1465). Отданное другое значение
   * снимает набранное: иначе, вернись значение к прежнему, набранное ожило бы поверх него — список дат
   * отбора добавил дату и стёр черновик, а поле показывало добавленную.
   */
  function give(v: string) {
    latest.current = v;
    if (v !== (value ?? '')) setTyped(null);
    onChange(v);
  }

  // Отдаём значение, только если оно есть; пустой ввод — явная очистка, остальное не отдаётся вовсе.
  function emit(s: SegmentValues<K>) {
    const r = model.read(s);
    if (r.kind === 'value') give(r.value);
    else if (r.kind === 'empty') give('');
  }

  /** Выбрано во всплывающей части (день в календаре): отдать, снять набранное и зафиксировать. */
  function pick(v: string) {
    give(v);
    // Выбранное снимает набранное: совпади оно с текущим значением, значение не сменится, и ошибка
    // набранного осталась бы над выбранным (ревью PR #1542).
    setTyped(null);
    entered.current = v;
    onCommit?.(v);
  }

  /** Вставка значения целиком — в любой сегмент (issue #1460); не значение — сегмент разберёт сам. */
  function onPaste(e: React.ClipboardEvent) {
    if (readOnly || disabled) return;
    const got = model.paste(e.clipboardData.getData('text'));
    if (!got) return;
    e.preventDefault();
    setTyped({ ...got, base: value ?? '' });
    emit(got);
  }

  /**
   * Выделение — сразу за фокусом: следующая цифра заменит сегмент. Таймер только повторяет его, если в
   * сегмент ещё не набрали: на медленной машине он срабатывал после первой цифры и выделял её, и
   * «3102» поверх 19.10 давало месяц «2» (живые прогоны PR #1544).
   */
  function advance(part: K | undefined) {
    const el = part && inputs.current[part];
    if (!el) return;
    el.focus();
    el.select();
    const was = el.value;
    setTimeout(() => { if (document.activeElement === el && el.value === was) el.select(); }, 0);
  }

  /** Сосед сегмента в порядке показа: у en-US за месяцем идёт день, у ru-RU — год. */
  function go(part: K, step: 1 | -1) {
    advance(model.order[model.order.indexOf(part) + step]);
  }

  /**
   * Цифры в сегмент. Соседние сегменты — из `from`, по умолчанию показанных. Явная основа нужна цифре
   * поверх выделенного значения: она начинает новое, а показано ещё прежнее.
   */
  function handle(part: K, raw: string, from: SegmentValues<K> = shown) {
    const len = model.len[part];
    const cur = from[part];
    const v = raw.replace(/\D/g, '').slice(0, len);
    const top = model.padAbove[part];
    // Автопереход — только набравшему новые цифры (сегмент не был полон).
    if (v.length === len && cur.length < len) {
      setSeg(part, v); go(part, 1); emit({ ...from, [part]: v });
    } else if (top !== undefined && v.length === 1 && cur === '' && +v > top) {
      // Цифра, которой сегмент начаться не может («4» в дне, «3» в часе), — «0X» и дальше.
      const p = '0' + v; setSeg(part, p); go(part, 1); emit({ ...from, [part]: p });
    } else {
      setSeg(part, v); emit({ ...from, [part]: v });
    }
  }

  /**
   * ↑/↓ — значение сегмента на шаг, как у нативного поля (issue #1463); правило шага — у модели.
   * Значение выделяется снова: следующая стрелка или цифра работает со всем сегментом.
   */
  function step(part: K, delta: 1 | -1, input: HTMLInputElement) {
    const next = model.step(part, delta, shown);
    if (next === null) return;
    setSeg(part, next);
    emit({ ...shown, [part]: next });
    setTimeout(() => { if (document.activeElement === input) input.select(); }, 0);
  }

  /** Первый видимый сегмент — с него начинают переписывать значение заново. */
  const first = model.order[0];

  /**
   * Клавиша поверх выделенного Ctrl+A значения; true — обработана здесь. Стирание работает и у
   * обязательного поля: это ввод, а не кнопка, — пустоту назовёт форма. Ctrl+C и Ctrl+X уходят в
   * `onCopy` / `onCut`, клавиши-модификаторы выделения не снимают; Esc снимает только его — окно, где
   * стоит поле, не закрывается.
   *
   * <p>Цифра начинает значение заново, но прежнее не стирает: оно остаётся, пока новое не сложится, —
   * как при наборе поверх сегмента. Иначе форма получила бы пустоту посреди набора, а уход с
   * недописанным оставлял бы поле пустым вместо прежнего значения. Esc возвращает его и здесь.</p>
   */
  function onSelectedKey(e: React.KeyboardEvent<HTMLInputElement>): boolean {
    const ctrl = e.ctrlKey || e.metaKey;
    if (MODIFIERS.includes(e.key) || (ctrl && CLIPBOARD_KEYS.some(k => e.code === `Key${k}` || e.key.toUpperCase() === k))) return true;
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setAllSelected(false); return true; }
    const wipe = e.key === 'Delete' || e.key === 'Backspace';
    const digit = /^\d$/.test(e.key);
    if (!wipe && !digit) { setAllSelected(false); return false; }
    e.preventDefault();
    if (readOnly || disabled) return true;
    if (wipe) { clear(); return true; }
    setAllSelected(false);
    inputs.current[first]?.focus();
    setTyped({ ...model.empty, base: value ?? '' });
    handle(first, e.key, model.empty);
    return true;
  }

  /**
   * Разделитель переводит к следующему сегменту, Backspace в пустом — к предыдущему, ←/→ с края — к
   * соседнему (issue #1463). Цифра в полный сегмент без выделения заменяет его, как в нативном поле:
   * иначе её молча съедал `maxLength`.
   */
  const onSegmentKey = (part: K) => (e: React.KeyboardEvent<HTMLInputElement>) => {
    const cur = shown[part];
    const input = e.currentTarget;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.code === 'KeyA' || e.key.toLowerCase() === 'a')) {
      // Пустое поле выделять нечем: подсветка и «Выделена вся дата» обещали бы значение, которого нет.
      e.preventDefault();
      if (hasContent) { input.select(); setAllSelected(true); }
      return;
    }
    if (allSelected && onSelectedKey(e)) return;
    if (e.key === 'Backspace' && cur === '') go(part, -1);
    else if (model.separators.includes(e.key) && cur) { e.preventDefault(); go(part, 1); }
    else if (/^\d$/.test(e.key) && input.value.length >= input.maxLength && input.selectionStart === input.selectionEnd) input.select();
    else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.altKey && !readOnly && !disabled) {
      e.preventDefault(); step(part, e.key === 'ArrowUp' ? 1 : -1, input);
    }
    else if (e.key === 'ArrowLeft' && atEdge(input, 'start')) { e.preventDefault(); go(part, -1); }
    else if (e.key === 'ArrowRight' && atEdge(input, 'end')) { e.preventDefault(); go(part, 1); }
  };

  /**
   * Дописать сегмент при уходе — значение из DOM: обработчик ухода срабатывает синхронно, раньше
   * перерисовки, когда `advance` уводит фокус, и замыкание ещё помнит прежнее.
   */
  function fixSegment(part: K) {
    const cur = inputs.current[part]?.value ?? shown[part];
    const fixed = model.fix(part, cur);
    if (fixed !== null) { setSeg(part, fixed); emit({ ...shown, [part]: fixed }); }
  }

  const segmentOf = (t: EventTarget) => model.order.find(part => inputs.current[part] === t);

  function startOver() {
    if (readOnly || disabled) return;
    setTyped({ ...model.empty, base: value ?? '' });
    // Сегментов ещё нет — они появятся этим же рендером; фокус ставится после него.
    setTimeout(() => advance(first), 0);
  }

  /**
   * Стереть значение целиком: крестик, «Очистить» календаря, Ctrl+A → Delete. Фокус — в первый
   * сегмент, и СРАЗУ, пока крестик не исчез вместе со значением: удалённому элементу браузер не шлёт
   * blur, и фокус упал бы на body (урок `TypePickerField`, #572). Значение до очистки запоминается для
   * Esc, если человек пришёл не из сегмента; таймер ухода отменяется, иначе он вернул бы ошибку
   * прежнего набранного.
   */
  function clear() {
    if (!focused) startValue.current = value ?? '';
    const firstInput = inputs.current[first];
    firstInput?.focus();
    clearTimeout(blurTimer.current);
    setFocused(true);
    setAllSelected(false);
    setTyped({ ...model.empty, base: value ?? '' });
    if ((value ?? '') !== '') give('');
    // «Не дата» показана текстом: сегментов ещё нет — они появятся этим же рендером.
    if (!firstInput) setTimeout(() => advance(first), 0);
  }

  /**
   * Всё значение текстом — для Ctrl+C и Ctrl+X поверх выделенного. Только целое: недописанное
   * склеилось бы в «18..2026», которую не примет ни одно поле, — его копирует браузер, выделенным
   * сегментом.
   */
  function onCopy(e: React.ClipboardEvent) {
    const text = allSelected ? model.text(shown) : null;
    if (text === null) return;
    e.preventDefault();
    e.clipboardData.setData('text/plain', text);
  }
  /** Ctrl+X поверх выделенного: целое — в буфер, и поле стирается целиком, а не одним сегментом. */
  function onCut(e: React.ClipboardEvent) {
    if (!allSelected) return;
    e.preventDefault();
    const text = model.text(shown);
    if (text !== null) e.clipboardData.setData('text/plain', text);
    if (!readOnly && !disabled) clear();
  }

  // Esc при правке возвращает значение, с которым вошли в поле (issue #1460), — и только его: окно,
  // где стоит поле, не закрывается (`Modal` решает о своём Esc после полей и не видит погашенного
  // здесь). Правки нет — Esc принадлежит окну. Только из сегмента: на значке календаря `startValue`
  // не снят (фокус туда приходит мимо `onFocus` сегментов) и мог устареть с монтирования — откат к нему
  // стёр бы значение, пришедшее с данными формы (ревью PR #1542).
  function onGroupKey(e: React.KeyboardEvent) {
    if (onKey?.(e)) return;
    const part = segmentOf(e.target);
    if (e.key === 'Escape' && !e.nativeEvent.isComposing && part !== undefined
      && (draft || (value ?? '') !== startValue.current)) {
      e.preventDefault(); e.stopPropagation();
      setTyped(null);
      if ((value ?? '') !== startValue.current) give(startValue.current);
    } else if (e.key === 'Enter' && part !== undefined) {
      // Enter — «готово» (issue #1465): дописать сегмент, как при уходе, и зафиксировать. Действие
      // формы по Enter (отправка) идёт следом и видит поправленное, а не прежнее.
      fixSegment(part);
      commit();
    }
  }

  const markInvalid = problem || invalid ? true : undefined;
  // Ошибка для скринридера — описанием сегментов (`aria-describedby`), и у `DateField` тоже: его
  // подпись под рамкой с полем не связана. Живая область объявляет только СМЕНУ ошибки: `role="alert"`
  // объявлял бы каждую ошибку при отрисовке, и таблица с двадцатью ячейками «не дата» зачитывалась бы
  // подряд (ревью PR #1542).
  const problemId = useId();
  const describedBy = problem ? problemId : undefined;

  const segClass = [
    'border-0 outline-none bg-transparent text-center min-w-0 p-0',
    disabled ? 'cursor-not-allowed' : 'focus:bg-brand-subtle',
    'rounded-sm tabular-nums',
    problem ? 'text-danger' : '',
    // Выделенное Ctrl+A значение подсвечено целиком, как один текст.
    allSelected ? 'bg-brand-subtle' : '',
  ].join(' ');

  /** Сегменты с разделителями и объявление выделения — внутрь группы поля. */
  const segments = (
    <>
      {model.order.map((part, i) => {
        const len = model.len[part];
        return (
          <Fragment key={part}>
            {i > 0 && <span className="text-fg4 select-none">{model.sep}</span>}
            {/* Ширина — по образцам (см. `SIZER`): обозначение и цифры. */}
            <span className="inline-grid">
              <span data-sizer={model.labels[part]} aria-hidden className={SIZER} />
              <span data-sizer={'0'.repeat(len)} aria-hidden className={SIZER} />
              <input
                ref={el => { inputs.current[part] = el; }} type="text" inputMode="numeric"
                placeholder={model.labels[part]} aria-label={model.names[part]} maxLength={len}
                value={shown[part]}
                disabled={disabled} readOnly={readOnly} aria-required={required || undefined} aria-invalid={markInvalid} aria-describedby={describedBy}
                onChange={e => handle(part, e.target.value)}
                onKeyDown={onSegmentKey(part)}
                onFocus={onFocus}
                onBlur={() => { fixSegment(part); onBlur(); }}
                className={`${segClass} col-start-1 row-start-1 w-0 min-w-full`}
              />
            </span>
          </Fragment>
        );
      })}
    </>
  );
  const selectedAnnouncement = <span className="sr-only" aria-live="polite">{allSelected ? model.selectedAll : ''}</span>;

  return {
    foreign, problem, problemId, describedBy, markInvalid, clearable, contentRef, segments, selectedAnnouncement,
    /** Обработчики группы сегментов. */
    groupProps: {
      ref: groupRef, role: 'group' as const, 'aria-label': label, onKeyDown: onGroupKey, onPaste, onCopy, onCut,
      onMouseDown: () => setAllSelected(false), onFocus: onFieldFocus, onBlur: onFieldBlur,
    },
    /** Всплывающая часть поля — ещё поле: вход и уход в неё считаются как в группе. */
    fieldFocus: { onFocus: onFieldFocus, onBlur: onFieldBlur },
    pick, clear, startOver,
  };
}

/**
 * Невидимый образец, растягивающий ячейку сегмента: ширина по содержимому, а не в `ch` — «MM» и «YYYY»
 * шире цифр, и у en-US подсказка обрезалась (проба стенда, PR #1544). Текст образца — в псевдоэлементе,
 * мимо `textContent` и скринридера. Поле в ячейке — `w-0 min-w-full`: своей ширины по умолчанию
 * (около 20 знаков) оно в ячейку не вносит.
 */
const SIZER = 'invisible col-start-1 row-start-1 px-0.5 tabular-nums after:content-[attr(data-sizer)]';
/** Клавиши-модификаторы: нажатые сами по себе, выделение всего значения не снимают (путь к Ctrl+C). */
const MODIFIERS = ['Control', 'Meta', 'Shift', 'Alt'];
/** Ctrl+C и Ctrl+X поверх выделенного значения — к `onCopy` / `onCut`, выделение не снимают. */
const CLIPBOARD_KEYS = ['C', 'X'];

/** Курсор у края сегмента или выделен весь сегмент — стрелка уводит к соседнему. */
function atEdge(input: HTMLInputElement, side: 'start' | 'end'): boolean {
  const { selectionStart: from, selectionEnd: to, value } = input;
  if (from === 0 && to === value.length) return true;
  return side === 'start' ? to === 0 : from === value.length;
}

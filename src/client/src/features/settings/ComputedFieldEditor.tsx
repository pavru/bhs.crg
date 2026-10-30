import { useEffect, useMemo, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import type { SchemaField } from '@/shared/api/schema';
import { nextTabStop, tabStops } from '@/shared/ui/tabStops';
import { evalComputed, validateComputed, findComputedCycles, referencedKeys } from '@/shared/utils/computedExpression';
import { Tooltip } from '@/shared/ui/Tooltip';

/** Редактор формулы расчётного поля (issue #368): текст выражения (JS) + пикер-typeahead «Вставить поле»,
 *  инлайн-валидация (синтаксис/неизвестное поле/цикл, #309/#311) и «Проверить на образце». */
export function ComputedFieldEditor({ field, siblingFields, onChange }: {
  field: SchemaField;
  siblingFields: { key: string; title: string; type: string; computed?: boolean; expression?: string }[];
  onChange: (patch: Partial<SchemaField>) => void;
}) {
  const taRef = useRef<HTMLTextAreaElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  /** Фокус уже перенесён (в формулу, Tab'ом дальше, на кнопку без прокрутки) — Radix его не возвращает. */
  const focusMoved = useRef(false);
  const [pickOpen, setPickOpen] = useState(false);
  const [pickFilter, setPickFilter] = useState('');
  const [testOpen, setTestOpen] = useState(false);
  const [sample, setSample] = useState<Record<string, string>>({});
  const expr = field.expression ?? '';
  const refs = siblingFields.filter(f => f.key.trim());

  // Инлайн-валидация: синтаксис + ссылки на несуществующие поля + цикл зависимостей.
  const knownKeys = useMemo(() => new Set([field.key, ...refs.map(f => f.key)]), [field.key, refs]);
  const { syntaxError, unknownRefs } = validateComputed(expr, knownKeys);
  const inCycle = useMemo(() => {
    const map: Record<string, string> = { [field.key]: expr };
    for (const f of refs) if (f.computed && f.expression) map[f.key] = f.expression;
    return findComputedCycles(map).has(field.key);
  }, [field.key, expr, refs]);

  const filtered = refs.filter(f =>
    !pickFilter || `${f.key} ${f.title}`.toLowerCase().includes(pickFilter.toLowerCase()));

  function insertRef(key: string) {
    const snippet = `get("${key}")`;
    const cur = field.expression ?? '';
    const ta = taRef.current;
    if (!ta) { onChange({ expression: cur + snippet }); setPickOpen(false); return; }
    const start = ta.selectionStart ?? cur.length;
    const end = ta.selectionEnd ?? cur.length;
    onChange({ expression: cur.slice(0, start) + snippet + cur.slice(end) });
    // Фокус уходит в формулу: возврат Radix на кнопку списка отменяется, иначе он перебил бы курсор.
    focusMoved.current = true;
    setPickOpen(false); setPickFilter('');
    requestAnimationFrame(() => { ta.focus(); const p = start + snippet.length; ta.setSelectionRange(p, p); });
  }

  /**
   * Tab с края списка — дальше по форме, как у календаря даты (docs/UI.md, «Слои над окном»): список
   * лежит в конце body, вне порядка Tab формы. С последней остановки вперёд — следующее поле после
   * кнопки (формула), с фильтра назад — сама кнопка; список закрывается. Фокус переносится сразу, в
   * обработчике: возврат Radix идёт таймером, и до него фокус лежал бы на body.
   */
  function onListTab(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'Tab') return;
    const stops = tabStops(e.currentTarget);
    if (document.activeElement !== (e.shiftKey ? stops[0] : stops.at(-1))) return;
    e.preventDefault();
    const trigger = triggerRef.current;
    focusMoved.current = true;
    ((!e.shiftKey && trigger && nextTabStop(trigger)) || trigger)?.focus();
    setPickOpen(false);
  }

  // Кнопку прокрутили из видимой части тела окна — список закрывается. `hideWhenDetached` только прятал
  // бы его: фокус и набор оставались бы в невидимом фильтре. Фокус — на кнопку, без прокрутки к ней.
  useEffect(() => {
    const trigger = triggerRef.current;
    if (!pickOpen || !trigger || typeof IntersectionObserver === 'undefined') return;
    const watch = new IntersectionObserver(([entry]) => {
      if (entry?.isIntersecting) return;
      focusMoved.current = true;
      trigger.focus({ preventScroll: true });
      setPickOpen(false);
    });
    watch.observe(trigger);
    return () => watch.disconnect();
  }, [pickOpen]);

  // «Проверить на образце»: образцовые значения ссылок → результат/ошибка (число если парсится).
  const testRefs = referencedKeys(expr);
  const testResult = useMemo(() => {
    if (!testOpen) return null;
    const values: Record<string, unknown> = {};
    for (const k of testRefs) {
      const raw = sample[k] ?? '';
      values[k] = raw !== '' && !Number.isNaN(Number(raw)) ? Number(raw) : raw;
    }
    return evalComputed(expr, values);
  }, [testOpen, sample, expr, testRefs]);

  return (
    <div className="rounded-sm border border-primary/30 bg-primary-container/40 p-2 space-y-1.5">
      <div className="flex items-center justify-between gap-2">
        <span className="text-label-small text-on-surface-variant font-medium">Формула (JavaScript)</span>
        <div className="flex items-center gap-1.5">
          <button type="button" onClick={() => setTestOpen(o => !o)}
            className="text-label-small px-2 py-0.5 rounded-xs border border-outline text-on-surface-variant state-layer">
            Проверить на образце
          </button>
          {/* Слой над окном (#1537, docs/UI.md, «Слои над окном»): портал в body — окно нового типа его
              не режет; Esc и щелчок мимо закрывают список, а не окно. Поповер, а не меню: у DropdownMenu
              набор текста ушёл бы в поиск по первой букве пунктов, и фильтр не напечатать. Фокус при
              открытии — в фильтр (первое поле слоя), при закрытии — на кнопку, после выбора — в формулу. */}
          <Popover.Root open={pickOpen} onOpenChange={o => { setPickOpen(o); setPickFilter(''); }}>
            <Popover.Trigger asChild>
              <button ref={triggerRef} type="button"
                className="text-label-small px-2 py-0.5 rounded-xs border border-outline text-primary hover:bg-primary/10">
                + Вставить поле
              </button>
            </Popover.Trigger>
            <Popover.Portal>
              <Popover.Content align="end" sideOffset={4} collisionPadding={8} hideWhenDetached aria-label="Вставить поле"
                onKeyDown={onListTab}
                onOpenAutoFocus={() => { focusMoved.current = false; }}
                onCloseAutoFocus={e => { if (focusMoved.current) { focusMoved.current = false; e.preventDefault(); } }}
                className="z-50 w-64 max-w-(--radix-popover-content-available-width) bg-surface border border-outline-variant rounded-md shadow-elevation-2 py-1 focus:outline-none">
                <input value={pickFilter} onChange={e => setPickFilter(e.target.value)}
                  placeholder="Фильтр…" aria-label="Фильтр полей" spellCheck={false}
                  className="mx-2 mb-1 w-[calc(100%-1rem)] border border-outline-variant rounded-xs px-2 py-1 text-body-small bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary" />
                <div className="max-h-52 overflow-auto">
                  {filtered.length === 0
                    ? <p className="px-3 py-2 text-body-small text-on-surface-variant">Нет полей</p>
                    : filtered.map(f => (
                      <button key={f.key} type="button" onClick={() => insertRef(f.key)}
                        className="w-full flex items-center gap-2 px-3 py-1.5 text-left state-layer">
                        <code className="text-label-small font-mono text-primary shrink-0 max-w-[45%] truncate">{f.key}</code>
                        <span className="text-body-small text-on-surface-variant truncate flex-1">{f.title || '—'}</span>
                        <span className="text-label-small text-on-surface-variant shrink-0">{f.type}</span>
                      </button>
                    ))}
                </div>
              </Popover.Content>
            </Popover.Portal>
          </Popover.Root>
        </div>
      </div>
      <textarea ref={taRef} value={expr} spellCheck={false} rows={2}
        onChange={e => onChange({ expression: e.target.value })}
        placeholder={'get("Количество") * get("Цена")'}
        className="w-full font-mono text-body-small border border-outline rounded-xs px-2 py-1.5 bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary resize-y" />

      {/* Инлайн-статус валидации (#309/#311) */}
      {inCycle ? (
        <p className="text-label-small text-error">⚠ Циклическая зависимость — поле ссылается само на себя (прямо или через другие расчётные).</p>
      ) : syntaxError ? (
        <Tooltip content={syntaxError}><p className="text-label-small text-error">✗ Синтаксическая ошибка: {syntaxError}</p></Tooltip>
      ) : unknownRefs.length > 0 ? (
        <p className="text-label-small text-warning">⚠ Неизвестные поля: {unknownRefs.map(k => `«${k}»`).join(', ')}</p>
      ) : expr.trim() ? (
        <p className="text-label-small text-success">✓ Синтаксис в порядке</p>
      ) : null}

      {testOpen && (
        <div className="rounded-xs border border-outline-variant bg-surface p-2 space-y-1.5">
          {testRefs.length === 0
            ? <p className="text-label-small text-on-surface-variant">Формула не ссылается на поля.</p>
            : testRefs.map(k => (
              <label key={k} className="flex items-center gap-2 text-label-small">
                <code className="font-mono text-on-surface-variant w-28 truncate shrink-0">{k}</code>
                <input value={sample[k] ?? ''} onChange={e => setSample(s => ({ ...s, [k]: e.target.value }))}
                  placeholder="образцовое значение"
                  className="flex-1 border border-outline-variant rounded-xs px-2 py-1 text-body-small bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary" />
              </label>
            ))}
          <div className="text-label-small pt-0.5">
            {testResult?.error
              ? <span className="text-error">Ошибка: {testResult.error}</span>
              : <span className="text-on-surface-variant">Результат: <strong className="font-mono">{String(testResult?.value ?? '—')}</strong></span>}
          </div>
        </div>
      )}

      <p className="text-label-small text-on-surface-variant">
        Читайте поля через <code className="font-mono">get("Ключ")</code>. Значения перечислений видны как отображаемое имя.
      </p>
    </div>
  );
}

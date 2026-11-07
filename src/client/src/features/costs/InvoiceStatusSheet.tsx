import { useEffect, useRef } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { ListItem } from '@/shared/ui/ListItem';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { ErrorIcon, Info, Warning, type IconDef } from '@/shared/ui/icons';
import { ruPlural } from '@/shared/utils/pluralize';
import { useInvoiceDelete } from './invoiceDelete';
import {
  LEVELS, LEVEL_TITLE, bannerOf, blocksText, type InvoiceStatus, type StatusAction, type StatusItem, type StatusLevel,
} from './invoiceStatus';

/**
 * Перечень «Состояние счёта» и banner над частями (costs-invoice-screen-11, D-11.3, ТЗ COST-6.4).
 *
 * <p>Перечень — `SideSheet variant="modal"` шириной 400; уже 768 он сам становится нижним листом. Пункт —
 * `ListItem`: знак уровня, текст, пояснение и пометки «мешает: …», одно text-действие справа. Пункт без права
 * виден и не ведёт.</p>
 *
 * <p>Banner — один, только при «Мешает работе»: худший такой пункт, до двух его действий, остальные такие же
 * — текстом «и ещё N — в перечне». Перечень открывает индикатор, действия «Весь перечень» у banner нет
 * (Э13).</p>
 */

const LEVEL_ICON: Record<StatusLevel, { icon: IconDef; tone: string }> = {
  blocking: { icon: ErrorIcon, tone: 'text-error' },
  gate: { icon: Warning, tone: 'text-warning' },
  check: { icon: Warning, tone: 'text-warning' },
  info: { icon: Info, tone: 'text-on-surface-variant' },
};

/**
 * Действие пункта — ПОСЛЕ того, как лист снят и вернул фокус открывшему. Действие, сделанное раньше, теряло
 * своё: фокус места уходил обратно на индикатор (живой прогон 3.3 — «К строкам» ставил фокус в строку, а лист
 * тут же уводил его). Ждём не срок, а само снятие: содержимое листа ушло из документа. Возврат фокуса слой
 * делает таймером, заведённым при снятии, — своё действие ставим таймером после него. Так от длительности
 * ухода листа ничего не зависит (ревью PR #1792), а отказаться от возврата у листа нельзя: это правка
 * shared/ui. Потолок ожидания — чтобы зависший уход не держал действие вечно.
 */
const CLOSE_LIMIT_MS = 2000;
function afterClose(node: HTMLElement | null, act: () => void, alive: () => boolean) {
  const since = performance.now();
  const wait = () => {
    if (node?.isConnected && performance.now() - since < CLOSE_LIMIT_MS) { requestAnimationFrame(wait); return; }
    setTimeout(() => { if (alive()) act(); }, 0);
  };
  requestAnimationFrame(wait);
}

/** Исполнитель действий пункта: удаление — через диалог удаления формы, остальное — форма. */
function useStatusRun(onAction: (action: StatusAction) => void): (action: StatusAction) => void {
  const askDelete = useInvoiceDelete();
  return action => (action.kind === 'delete' ? askDelete?.(action.reason) : onAction(action));
}

export function InvoiceStatusSheet({ open, onClose, status, saving, savedAt, onAction }: {
  open: boolean;
  onClose: () => void;
  status: InvoiceStatus;
  /** Запись в пути — перечень о том, что было до неё. */
  saving: boolean;
  /** Когда сохранён счёт, о котором перечень. */
  savedAt: string;
  onAction: (action: StatusAction) => void;
}) {
  const run = useStatusRun(onAction);
  // Форму закрыли, пока лист уходил, — действие о счёте, которого на экране уже нет, не выполняется.
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  // По нему видно, что лист снят: содержимое уходит из документа вместе с ним.
  const body = useRef<HTMLParagraphElement>(null);
  const count = status.count > 0 ? `${status.count} ${ruPlural(status.count, 'замечание', 'замечания', 'замечаний')} · ` : '';
  return (
    <SideSheet variant="modal" open={open} onClose={onClose} title="Состояние счёта">
      {/* Слота подзаголовка у листа нет — первой строкой тела (D-11.3). */}
      <p ref={body} className="text-body-medium text-on-surface-variant pb-2" data-testid="invoice-status-subtitle">
        {count}{saving ? 'Сохраняю…' : `по сохранённому · ${savedAt}`}
      </p>
      {status.items.length === 0 && (
        <p className="text-body-medium text-on-surface-variant py-4">
          {status.mood === 'ready' ? 'Всё готово к «Разобран».' : 'Сказать о счёте нечего.'}
        </p>
      )}
      {LEVELS.map(level => {
        const items = status.items.filter(item => item.level === level);
        if (items.length === 0) return null;
        const id = `invoice-status-${level}`;
        return (
          <section key={level} aria-labelledby={id} className="-mx-6" data-testid={id}>
            <h3 id={id} className="px-4 pt-3 pb-1 text-title-small text-on-surface-variant">
              {LEVEL_TITLE[level]}{level === 'info' && ' · в число не идёт'}
            </h3>
            <ul>
              {items.map(item => (
                <li key={item.key}><StatusRow item={item} run={action => { const node = body.current; onClose(); afterClose(node, () => run(action), () => mounted.current); }} /></li>
              ))}
            </ul>
          </section>
        );
      })}
    </SideSheet>
  );
}

function StatusRow({ item, run }: { item: StatusItem; run: (action: StatusAction) => void }) {
  const icon = LEVEL_ICON[item.level];
  const [action, ...more] = item.actions;
  // Одно действие справа; «Открыть» двойника при удалении справа и «Показать на скане» у меток при «Всё верно»
  // справа — сама строка (её подсказка — подпись действия).
  const open = more.find(a => a.kind === 'open' || a.kind === 'show-scan');
  const supporting = item.detail || item.blocks ? (
    <span className="flex flex-col gap-0.5 whitespace-normal">
      {item.detail && <span>{item.detail}</span>}
      {item.blocks && <StatusText tone="warning">{blocksText(item.blocks)}</StatusText>}
    </span>
  ) : undefined;
  const content = {
    icon: icon.icon, iconTone: icon.tone, 'data-testid': `status-item-${item.key}`, title: item.text, supporting,
    headline: <span className="whitespace-normal break-words">{item.text}</span>,
    action: action
      ? <Button variant="text" title={action.short ? action.label : undefined} onClick={() => run(action)}>{action.short ?? action.label}</Button>
      : item.why ? <span className="sr-only">{item.why}</span> : undefined,
  };
  return open
    ? <ListItem {...content} onClick={() => run(open)} hint={open.label} />
    : <ListItem {...content} passive />;
}

/** Banner над частями: худший пункт «Мешает работе». Других плашек в форме нет. */
export function InvoiceStatusBanner({ status, onAction, busy = [] }: {
  status: InvoiceStatus;
  onAction: (action: StatusAction) => void;
  /** Действия, ждущие ответа, — по виду. */
  busy?: readonly StatusAction['kind'][];
}) {
  const run = useStatusRun(onAction);
  const banner = bannerOf(status);
  if (!banner) return null;
  const { item, rest } = banner;
  return (
    <Banner tone={item.bannerTone ?? 'error'} urgent data-testid="invoice-status-banner"
      actions={item.actions.slice(0, 2).map(action => ({ label: action.label, loading: busy.includes(action.kind), onClick: () => run(action) }))}>
      {item.text}.{item.detail && ` ${item.detail}`}
      {rest > 0 && ` И ещё ${rest} — в перечне.`}
    </Banner>
  );
}

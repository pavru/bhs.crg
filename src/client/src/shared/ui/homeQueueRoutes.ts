import { Archive, Checklist, DocumentScanner, Help, Warning } from './icons';
import type { IconDef } from './icons/types';

/**
 * Куда ведёт карточка очереди Главной и каким знаком она говорит (shell-08, ТЗ UI-34) — по паре «модуль,
 * очередь». Сервер адресов клиента не знает (как и у `recordRoutes.ts`): модуль объявляет очередь кодом,
 * экран и его параметр знает только клиент.
 *
 * ⚠️ Перепись: `homeQueues.json` — все очереди сборки, сверенные с сервером (`HomeQueueDeclarationTests`);
 * `homeQueues.test.ts` требует, чтобы у каждой здесь был адрес и экран принимал её параметр. Карточку
 * без адреса Главная не показывает: она звала бы туда, куда не попасть.
 */
export interface HomeQueueRoute {
  href: string;
  /** Знак — тот же, что у чипа очереди на её экране: одна очередь, один знак. */
  icon: IconDef;
  tone?: 'error' | 'warning';
  /** Имя знака для читалки, когда знак говорит о состоянии. */
  iconLabel?: string;
  /** Знак при «проверено не всё»: сомнение отличает знак, а не цвет. */
  doubt?: { icon: IconDef; tone: 'warning'; iconLabel: string };
}

/** Ключ — `модуль/очередь`, как в переписи. Знаки и тона — как у `InvoiceQueueChips`. */
export const HOME_QUEUE_ROUTES: Readonly<Record<string, HomeQueueRoute>> = {
  'costs/parsing': { href: '/invoices?queue=parsing', icon: Checklist },
  'costs/unrecognized': { href: '/invoices?queue=unrecognized', icon: DocumentScanner, tone: 'warning', iconLabel: 'внимание' },
  'costs/lost': {
    href: '/invoices?queue=lost', icon: Warning, tone: 'error', iconLabel: 'ошибка',
    doubt: { icon: Help, tone: 'warning', iconLabel: 'проверено не всё' },
  },
  'costs/archived': { href: '/invoices?queue=archived', icon: Archive },
};

/** Адрес и знак экрана очереди или `null` — тогда карточки нет. */
export function homeQueueRoute(module: string, id: string): HomeQueueRoute | null {
  return HOME_QUEUE_ROUTES[`${module}/${id}`] ?? null;
}

import { LOST_DOUBT_MARK, QUEUE_MARK, type QueueMark } from '@/features/costs/invoiceQueues';

/**
 * Куда ведёт карточка очереди Главной и каким знаком она говорит (shell-08, ТЗ UI-34) — по паре «модуль,
 * очередь». Сервер адресов клиента не знает (как и у `recordRoutes.ts`): модуль объявляет очередь кодом,
 * экран и его параметр знает только клиент.
 *
 * ⚠️ Перепись: `homeQueues.json` — все очереди сборки, сверенные с сервером (`HomeQueueDeclarationTests`);
 * `homeQueues.test.ts` требует, чтобы у каждой здесь был адрес и экран принимал её параметр. Карточку
 * без адреса Главная не показывает: она звала бы туда, куда не попасть.
 */
/** Знак — из таблицы знаков экрана очереди (у счетов — `QUEUE_MARK`): одна очередь, один знак. */
export interface HomeQueueRoute extends QueueMark {
  href: string;
  /** Знак при «проверено не всё»: сомнение отличает знак, а не цвет. */
  doubt?: QueueMark;
}

/** Ключ — `модуль/очередь`, как в переписи. */
export const HOME_QUEUE_ROUTES: Readonly<Record<string, HomeQueueRoute>> = {
  'costs/parsing': { href: '/invoices?queue=parsing', ...QUEUE_MARK.parsing },
  'costs/unrecognized': { href: '/invoices?queue=unrecognized', ...QUEUE_MARK.unrecognized },
  'costs/lost': { href: '/invoices?queue=lost', ...QUEUE_MARK.lost, doubt: LOST_DOUBT_MARK },
  'costs/archived': { href: '/invoices?queue=archived', ...QUEUE_MARK.archived },
};

/** Адрес и знак экрана очереди или `null` — тогда карточки нет. */
export function homeQueueRoute(module: string, id: string): HomeQueueRoute | null {
  return HOME_QUEUE_ROUTES[`${module}/${id}`] ?? null;
}

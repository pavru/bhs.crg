import { Apartment, DocumentScanner, LocalShipping, NoteAdd, type IconDef } from './icons';
import type { AccessInfo } from '@/shared/api/access';
import { hasPermission } from '@/shared/api/access';
import { workNav } from './navConfig';
import { allowed } from './navAccess';

/**
 * Действия командной палитры (issue #1433): «Новый счёт», «Счёт из файла» и т. п. — то же, что кнопки
 * экранов, но из любого места приложения.
 *
 * <p>Палитра действия НЕ исполняет: она ведёт на экран адресом с параметром `do`, и экран исполняет
 * его СВОЕЙ кнопкой (`usePaletteIntent`). Так у действия один исполнитель — тот, что уже проверен
 * экраном: черновик заводится той же функцией, вопрос о несохранённых правках задаётся тем же путём,
 * а выбор файла открывается тем же полем, что знает виды файлов.</p>
 *
 * <p>⚠️ Действие без экрана, который его принимает, — дверь в никуда: палитра привела бы на список и
 * ничего не сделала. Поэтому у каждого действия здесь есть принимающий экран, и это сверяет перепись
 * `paletteActions.test.ts`.</p>
 */
export interface PaletteAction {
  /** Значение параметра `do`; стабильно — это часть адреса. */
  id: string;
  label: string;
  /** Слова, по которым действие ещё ищется, кроме подписи: «создать», «загрузить», «скан». */
  words: string;
  icon: IconDef;
  /** Экран, который действие исполняет: адрес пункта навигации. */
  to: string;
  /** Право кнопки на экране — то самое, которым экран её показывает. */
  permission: string;
  /**
   * Основное действие экрана — его filled-кнопка (правило `Button`: такая на экране одна). На
   * телефоне оно встаёт плавающей кнопкой (`PrimaryFab`, issue #1434), а кнопка в шапке экрана там
   * прячется. У экрана основное действие одно — сверяет `paletteActions.test.ts`.
   */
  primary?: true;
}

export const INTENT_PARAM = 'do';

export const PALETTE_ACTIONS = {
  invoiceNew: {
    id: 'invoice-new', label: 'Новый счёт', words: 'создать завести черновик счета на оплату',
    icon: NoteAdd, to: '/invoices', permission: 'costs.invoice.edit',
  },
  invoiceScan: {
    id: 'invoice-scan', label: 'Счёт из файла', words: 'загрузить скан pdf распознать файлы счета',
    // Основное — «из скана» (решение владельца от 08.10.2026): счёт приезжает бумагой.
    icon: DocumentScanner, to: '/invoices', permission: 'costs.invoice.edit', primary: true,
  },
  waybillNew: {
    id: 'waybill-new', label: 'Новая накладная', words: 'создать завести расходную накладные',
    icon: LocalShipping, to: '/waybills', permission: 'costs.waybill.edit', primary: true,
  },
  constructionNew: {
    id: 'construction-new', label: 'Новая стройка', words: 'создать завести объект стройки',
    icon: Apartment, to: '/document-sets', permission: 'core.constructions.edit', primary: true,
  },
} as const satisfies Record<string, PaletteAction>;

/** Адрес, по которому экран исполнит действие. */
export function intentHref(action: PaletteAction): string {
  return `${action.to}?${INTENT_PARAM}=${action.id}`;
}

/**
 * Действия, доступные пользователю: открыт экран действия (модуль и право чтения — тем же правилом,
 * что у навигации) И есть право его кнопки. Одного права кнопки мало: экран, закрытый навигацией,
 * отказал бы, и палитра привела бы к отказу вместо действия.
 */
export function availableActions(access: AccessInfo): PaletteAction[] {
  return Object.values(PALETTE_ACTIONS).filter(a => {
    const screen = workNav.find(n => n.to === a.to);
    return screen !== undefined && allowed(screen, access) && hasPermission(access, a.permission);
  });
}

/**
 * Основное действие экрана по адресу — для плавающей кнопки телефона (issue #1434). Только на самом
 * экране действия: в карточке под ним (`/document-sets/<id>`) у экрана свои кнопки.
 */
export function primaryActionAt(pathname: string, access: AccessInfo): PaletteAction | undefined {
  return availableActions(access).find(a => a.primary && a.to === pathname);
}

import type { AccessInfo } from '@/shared/api/access';
import type { RecordSearchGroup, RecordSearchHit } from '@/shared/api/recordSearch';
import { formatMoney } from '@/shared/format/format';
import { Apartment, Layers, ReceiptLong, type IconDef } from './icons';
import { recordScreen, type RecordRoute } from './recordRoutes';

/**
 * Группы данных командной палитры (shell-05, [UI-30], [UI-31]): «Счета», «Стройки», «Наборы данных».
 *
 * <p>Группу ищет сервер — в правах спрашивающего, и закрытой правами группы в ответе нет. Здесь — что
 * палитре нужно от группы сверх ответа: чип области, раздел для Tab и экран записи. Группа, у которой
 * экрана записи нет или он закрыт (`recordScreen` — тем же правом раздела, что закрывает маршрут), не
 * показывается вовсе — ни чипом, ни найденным: строка вела бы в отказ (второй пояс, а не единственный).</p>
 */
export interface RecordGroupDef {
  /** Код группы на сервере (`RecordSearchGroup.Code`) — значение параметра `group`. */
  code: string;
  /** Подпись чипа и заголовок группы. */
  label: string;
  /** Код типа записи — ключ `RECORD_ROUTES`. */
  recordType: string;
  /** Подвал: «Tab искать только в счетах». */
  within: string;
  /** Знак записи группы — в палитре и в «Недавних» Главной: что это за запись, а не где она лежит. */
  icon: IconDef;
}

/** Порядок — как у групп в палитре: «Действия» → эти → разделы. */
export const RECORD_GROUPS: readonly RecordGroupDef[] = [
  { code: 'invoices', label: 'Счета', recordType: 'СчётНаОплату', within: 'счетах', icon: ReceiptLong },
  { code: 'constructions', label: 'Стройки', recordType: 'Стройка', within: 'стройках', icon: Apartment },
  { code: 'datasets', label: 'Наборы данных', recordType: 'НаборДанных', within: 'наборах данных', icon: Layers },
];

export interface OpenGroup {
  def: RecordGroupDef;
  /** Экран записи этой группы — открытый этому человеку. */
  route: RecordRoute;
}

/** Группы, открытые человеку: экран записи есть и открыт. */
export function openGroups(access: AccessInfo): OpenGroup[] {
  return RECORD_GROUPS.flatMap(def => {
    const route = recordScreen(def.recordType, access);
    return route ? [{ def, route }] : [];
  });
}

/**
 * Группа раздела, где стоит человек, — для Tab ([UI-31]): раздел — экран записей группы (`/invoices`
 * и всё под ним). На экране без своей группы данных (`/reconciliations`) — `undefined`: Tab поиск не сужает.
 */
export function tabGroup(pathname: string, groups: OpenGroup[]): OpenGroup | undefined {
  return groups.find(g => pathname === g.route.screen || pathname.startsWith(`${g.route.screen}/`));
}

/** Найденная группа ответа вместе с тем, что о ней знает клиент. */
export interface FoundGroup extends OpenGroup {
  answer: RecordSearchGroup;
}

/** Ответ сервера по открытым группам — в порядке групп палитры; группы без экрана отброшены. */
export function foundGroups(answer: RecordSearchGroup[] | undefined, groups: OpenGroup[]): FoundGroup[] {
  if (!answer) return [];
  return groups.flatMap(g => {
    const found = answer.find(a => a.code === g.def.code && a.recordType === g.def.recordType);
    return found ? [{ ...g, answer: found }] : [];
  });
}

/**
 * Что стоит у замыкающего края строки записи (М-4): сумма счёта, место набора данных, внешний код стройки.
 * Пояснение, которому места у края не досталось (назначение счёта), — второй строкой.
 */
export function hitText(hit: RecordSearchHit): { trailing?: string; supporting?: string } {
  if (hit.amount != null) return { trailing: formatMoney(hit.amount), supporting: hit.detail ?? undefined };
  if (hit.place) return { trailing: hit.place, supporting: hit.detail ?? undefined };
  return { trailing: hit.detail ?? undefined };
}

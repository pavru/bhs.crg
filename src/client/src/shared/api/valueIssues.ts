import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { AuditFinding } from './documentTypes';

/**
 * Расхождения значения с объявленным типом — для показа в ФОРМЕ, у поля (issue #644).
 *
 * Ввод руками форма проверяет и сама (`validateConstraint` на изменение, `collectConstraintViolations`
 * при сохранении, #463). Непроверенным до сих пор оставалось всё остальное: распознавание, вставка из
 * буфера, авто-маппер, привязка набора данных и запись по API кладут значение как прочитали, и оно
 * молчало до самого выпуска — а у записей общих данных не всплывало вообще никогда.
 *
 * Источник — аудит объекта, тот же, что у модалок исправления. Своих правил на клиенте нет намеренно:
 * разойдись они с серверными, форма показывала бы одно, а выпуск — другое.
 *
 * Проверяется СОХРАНЁННОЕ состояние: пока правку не сохранили, находки говорят о том, что лежит в
 * базе. Это то же поведение, что у индикаторов битых ссылок (#332), и оно верное — вопрос «что не так
 * с хранимыми данными» иначе не задать.
 */

/** Коды находок, относящихся к значению поля (осиротевшие ключи форме показывать нечем — их нет в схеме). */
const VALUE_CODES = new Set(['value-type', 'type-mismatch']);

export type ValueIssues = Map<string, string[]>;

const EMPTY_ISSUES: ValueIssues = new Map();

/** Находки по пути: `Работы[0].Порядок` → сообщения. У одного значения их бывает несколько. */
export function valueIssuesByPath(findings: AuditFinding[] | undefined): ValueIssues {
  if (!findings?.length) return EMPTY_ISSUES;
  const map: ValueIssues = new Map();
  for (const f of findings) {
    if (!VALUE_CODES.has(f.code)) continue;
    const list = map.get(f.path);
    if (list) list.push(f.message);
    else map.set(f.path, [f.message]);
  }
  return map;
}

/** Число расхождений СТРОГО глубже поля (внутри составного/строк таблицы) — для свод-бейджа. */
export function deepIssueCount(issues: ValueIssues, key: string): number {
  let n = 0;
  for (const [path, messages] of issues)
    if (path !== key && path.split(/[.[]/)[0] === key) n += messages.length;
  return n;
}

/** Сумма расхождений по полям раздела (и своих, и вложенных) — для рейла разделов. */
export function issueCountInFields(issues: ValueIssues, keys: string[]): number {
  let n = 0;
  for (const key of keys) n += (issues.get(key)?.length ?? 0) + deepIssueCount(issues, key);
  return n;
}

// Документ читает аудит через существующий useAuditInstance (documentSets.ts) — второй хук на тот же
// ключ кэша только развёл бы условия перечитывания.

/** Аудит записи общих данных — тот же серверный обход, объект тот же (DomainObject). */
export function useCommonDataValueIssues(entryId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: ['common-data-audit', entryId],
    queryFn: () => apiClient.get<AuditFinding[]>(`/common-data/${entryId}/audit`).then(r => r.data),
    enabled: !!entryId && enabled,
    staleTime: 30_000,
  });
}

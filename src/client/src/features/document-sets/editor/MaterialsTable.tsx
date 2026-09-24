import type { ReactNode } from 'react';
import { AlertTriangle, Check, Link2, Replace, ShieldCheck, Unlink } from 'lucide-react';
import { ScopeIcon } from '@/shared/ui/ScopeIcon';
import type { MaterialQualityLink, QualityDocument } from '@/shared/api/qualityDocs';
import { SCOPE_LABELS, type CatalogScope } from '@/shared/api/types';
import type { MaterialRow } from './materialRow';

/**
 * Действие в строке материала (issue #680).
 *
 * Своя пилюля, а не `Button size="sm"`: у кнопки высота 32 px, а строк на живом реестре 130 — это
 * лишний экран прокрутки в таблице, которая и так скроллится внутри себя. Размер взят у пилюли
 * «привязать», которая в строке уже стояла, поэтому вертикальная цена нулевая.
 *
 * Тон говорит о роли: `brand` — согласие с догадкой машины (главное действие подсказки),
 * `tonal` — «сделаю выбор сам».
 */
function RowPill({ onClick, title, tone = 'tonal', children }: {
  onClick: () => void; title: string; tone?: 'brand' | 'tonal'; children: ReactNode;
}) {
  return (
<button type="button" onClick={onClick} title={title}
  className={'flex items-center gap-1 px-1.5 py-0.5 text-xs rounded-full shrink-0 '
    + 'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand '
    + (tone === 'brand'
      ? 'bg-brand hover:bg-brand-hover text-on-brand'
      : 'bg-tonal text-on-tonal hover:brightness-[.97]')}>
  {children}
</button>
  );
}

// ─── Таблица материалов ─────────────────────────────────────────────

/**
 * Список материалов со состоянием связки в каждой строке (issue #1032 — вынесено из вкладки).
 *
 * Пропы названы теми же именами, что несли значения внутри вкладки: перенос JSX дословный, и
 * переименование здесь означало бы правку каждой строки разметки заодно с переносом.
 */
export function MaterialsTable({
  materials, selected, toggle, colliding, findLink, suggestionByKey, scope, scopeId,
  docById, docName, setViewDoc, openPickerFor, setBreaking, acceptSuggestion,
}: {
  materials: MaterialRow[];
  selected: Set<string>;
  toggle: (key: string) => void;
  colliding: Map<string, string[]>;
  findLink: (m: MaterialRow) => MaterialQualityLink | undefined;
  suggestionByKey: Map<string, { doc: QualityDocument; score: number }>;
  scope: CatalogScope;
  scopeId: string | null;
  docById: Map<string, QualityDocument>;
  docName: Map<string, string>;
  setViewDoc: (doc: QualityDocument) => void;
  openPickerFor: (m: MaterialRow) => void;
  setBreaking: (v: { link: MaterialQualityLink; label: string }) => void;
  acceptSuggestion: (m: MaterialRow, docId: string) => Promise<void>;
}) {
  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="max-h-[50vh] overflow-y-auto">
        {/* table-fixed — ради ВИДИМОСТИ действий, а не ради вида. Ячейка таблицы с обычной
            раскладкой берёт ширину по содержимому и `truncate` в ней не работает: на живом
            реестре строка выходила 1776 px в контейнере 1550, и правая колонка с «Разорвать»
            уезжала за край — добраться до неё можно было только горизонтальной прокруткой
            внутри таблицы. Фиксированная раскладка берёт ширины из шапки, и обрезка начинает
            действовать (issue #680). */}
        <table className="w-full table-fixed text-sm">
          <thead className="bg-base sticky top-0">
            <tr>
              <th className="w-8 px-2 py-2"></th>
              <th className="px-2 py-2 text-left font-medium text-fg3">Материал</th>
              <th className="px-2 py-2 text-left font-medium text-fg3 w-2/5">Документ качества</th>
            </tr>
          </thead>
          <tbody>
            {materials.map(m => {
              const link = findLink(m);
              const suggestion = !link ? suggestionByKey.get(m.key) : undefined;
              return (
                <tr key={m.key} className="border-t border-muted hover:bg-base">
                  <td className="px-2 py-1.5 text-center">
                    <input type="checkbox" checked={selected.has(m.key)} onChange={() => toggle(m.key)}
                      className="w-4 h-4 rounded border-stroke-strong text-brand" />
                  </td>
                  <td className="px-2 py-1.5 text-fg1">
                    <span className="flex items-center gap-1.5">
                      {/* min-w-0 — не косметика: без него `truncate` не ужимает имя, минимальная
                          ширина ячейки равна всей строке, и таблица разъезжается шире
                          контейнера. Действия правой колонки при этом уезжают за край экрана —
                          то есть аффорданс, ради видимости которого затевался issue #680,
                          достаётся только тому, кто догадался прокрутить таблицу вбок. */}
                      <span className="truncate min-w-0">{m.label}</span>
                      {/* Строка спорит с другой за связку (issue #585): совпало значение, но не
                          ключ целиком. Различить их система не может — решение за человеком. */}
                      {colliding.has(m.key) && (
                        <span
                          title={`Совпадает с другой строкой по «${colliding.get(m.key)!.join('», «')}», но ключ целиком разный. `
                            + 'Либо это одна позиция, записанная по-разному — тогда исправьте материал, '
                            + 'либо разные товары — тогда заведите две связки.'}
                          className="shrink-0 text-warning">
                          <AlertTriangle size={13} />
                        </span>
                      )}
                    </span>
                  </td>
                  <td className="px-2 py-1.5">
                    {link ? (
                      <span className="flex items-center gap-1.5">
                        <ShieldCheck size={13} className="text-success shrink-0" />
                        {/* Уровень показываем, ТОЛЬКО когда он расходится с селектором (issue
                            #681): в обычном случае он у всех строк один, и повторённый 130 раз
                            значок стал бы фоном. А расхождение — ровно то место, где человек
                            думает, что правит связку комплекта, а правит общесистемную. */}
                        {(link.scope !== scope || (link.scopeId ?? null) !== scopeId) && (
                          <ScopeIcon scope={link.scope}
                            title={`Связка заведена на уровне «${SCOPE_LABELS[link.scope]}», а в селекторе выбрано `
                              + `«${SCOPE_LABELS[scope]}». Перепривязка изменит её на своём уровне — то есть всюду, `
                              + 'куда этот уровень достаёт.'} />
                        )}
                        {/* Имя документа — единственный вход в просмотр: рядом стояла иконка
                            Eye с тем же обработчиком и той же подсказкой (issue #682). Место,
                            которое она занимала, ушло под «Перепривязать». */}
                        <button onClick={() => { const d = docById.get(link.qualityDocumentId); if (d) setViewDoc(d); }}
                          title="Просмотреть документ"
                          className="flex-1 min-w-0 text-left text-brand-hover hover:underline truncate">
                          {docName.get(link.qualityDocumentId) ?? '(документ)'}
                        </button>
                        {/* Починка неверной связки — перепривязкой, а не разрывом: разрыв меняет
                            одну ошибку («не тот документ») на другую («документа нет»). До этого
                            единственный путь починки шёл через destructive-действие. */}
                        <button onClick={() => openPickerFor(m)} title="Перепривязать к другому документу"
                          className="p-0.5 text-fg4 hover:text-brand"><Replace size={13} /></button>
                        <button onClick={() => setBreaking({ link, label: m.label })} title="Снять связь"
                          className="p-0.5 text-fg4 hover:text-danger"><Unlink size={13} /></button>
                      </span>
                    ) : suggestion ? (
                      <span className="flex items-center gap-1.5">
                        <span className="text-[10px] px-1 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{Math.round(suggestion.score * 100)}%</span>
                        <button onClick={() => setViewDoc(suggestion.doc)} title="Просмотреть предложенный документ"
                          className="flex-1 min-w-0 text-left text-fg3 italic hover:underline truncate">
                          {suggestion.doc.displayName}
                        </button>
                        <RowPill tone="brand" onClick={() => void acceptSuggestion(m, suggestion.doc.id)}
                          title="Привязать предложенный документ">
                          <Check size={12} /> привязать
                        </RowPill>
                        {/* Подсказка — это согласие с догадкой машины, а не вход в выбор (issue
                            #680). Промахнулась догадка — до этой кнопки уйти из строки было
                            некуда, кроме как через чекбокс и кнопку под таблицей. */}
                        <RowPill onClick={() => openPickerFor(m)} title="Выбрать другой документ качества">
                          другой
                        </RowPill>
                      </span>
                    ) : (
                      <span className="flex items-center gap-1.5">
                        <span className="flex-1 min-w-0 text-fg4">—</span>
                        {/* Кнопка ВИДИМАЯ, не по hover: необнаруживаемость одиночного пути и есть
                            предмет жалобы, а мерцающая колонка на 130 строках недоступна ни с
                            клавиатуры, ни с тача. */}
                        <RowPill onClick={() => openPickerFor(m)} title="Выбрать документ качества для этого материала">
                          <Link2 size={12} /> Связать
                        </RowPill>
                      </span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

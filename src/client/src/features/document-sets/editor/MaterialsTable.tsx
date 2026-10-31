import { Icon } from '@/shared/ui/Icon';
import { ChangeCircle, Check, LinkIcon, LinkOff, Search, VerifiedUser, Warning } from '@/shared/ui/icons';
import { ScopeIcon } from '@/shared/ui/ScopeIcon';
import { Checkbox } from '@/shared/ui/Checkbox';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import type { MaterialQualityLink, QualityDocument } from '@/shared/api/qualityDocs';
import { SCOPE_LABELS, type CatalogScope } from '@/shared/api/types';
import type { MaterialRow } from './materialRow';
import { Tooltip } from '@/shared/ui/Tooltip';
import { IconButton } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';

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
  // Фиксированная раскладка (`fixed`) — ради ВИДИМОСТИ действий, а не ради вида. Клетка таблицы с
  // обычной раскладкой берёт ширину по содержимому и `truncate` в ней не работает: на живом реестре
  // строка выходила 1776 px в контейнере 1550, и правая колонка с «Разорвать» уезжала за край —
  // добраться до неё можно было только горизонтальной прокруткой внутри таблицы (issue #680).
  const columns: DataGridColumn<MaterialRow>[] = [
    { key: 'selected', label: '', width: 'w-10',
      cell: m => <Checkbox size="sm" checked={selected.has(m.key)} onChange={() => toggle(m.key)} aria-label={`Выбрать «${m.label}»`} /> },
    { key: 'label', label: 'Материал',
      cell: m => (
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
            <Tooltip content={`Совпадает с другой строкой по «${colliding.get(m.key)!.join('», «')}», но ключ целиком разный. `
                + 'Либо это одна позиция, записанная по-разному — тогда исправьте материал, '
                + 'либо разные товары — тогда заведите две связки.'}>
              <span
                className="shrink-0 text-warning">
                <Icon icon={Warning} size={18} />
              </span>
            </Tooltip>
          )}
        </span>
      ) },
    { key: 'link', label: 'Документ качества', width: 'w-2/5',
      cell: m => {
        const link = findLink(m);
        const suggestion = !link ? suggestionByKey.get(m.key) : undefined;
        return (
          <>
            {link ? (
              <span className="flex items-center gap-1.5">
                <Icon icon={VerifiedUser} size={18} className="text-success shrink-0" />
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
                <Tooltip content="Просмотреть документ">
                  <button onClick={() => { const d = docById.get(link.qualityDocumentId); if (d) setViewDoc(d); }}
                    className="flex-1 min-w-0 text-left text-primary hover:underline truncate">
                    {docName.get(link.qualityDocumentId) ?? '(документ)'}
                  </button>
                </Tooltip>
                {/* Починка неверной связки — перепривязкой, а не разрывом: разрыв меняет
                    одну ошибку («не тот документ») на другую («документа нет»). До этого
                    единственный путь починки шёл через destructive-действие. */}
                <IconButton label="Перепривязать к другому документу" onClick={() => openPickerFor(m)} icon={ChangeCircle} />
                <IconButton label="Снять связь"
                  danger
                  onClick={() => setBreaking({ link, label: m.label })} icon={LinkOff} />
              </span>
            ) : suggestion ? (
              <span className="flex items-center gap-1.5">
                <span className="text-label-small px-1 py-0.5 rounded-xs bg-primary-container text-on-primary-container shrink-0">{Math.round(suggestion.score * 100)}%</span>
                <Tooltip content="Просмотреть предложенный документ">
                  <button onClick={() => setViewDoc(suggestion.doc)}
                    className="flex-1 min-w-0 text-left text-on-surface-variant italic hover:underline truncate">
                    {suggestion.doc.displayName}
                  </button>
                </Tooltip>
                {/* Действие в строке (issue #680) — `Chip` без нажатия, assist chip MD3 ([UI-42]). Не
                    `Button size="sm"`: у кнопки 32 px, а строк на живом реестре 130 — лишний экран
                    прокрутки. Согласие с догадкой отличает галочка и место первым в паре. */}
                <Chip icon={Check} className="shrink-0" title="Привязать предложенный документ"
                  onClick={() => void acceptSuggestion(m, suggestion.doc.id)}>
                  привязать
                </Chip>
                {/* Подсказка — это согласие с догадкой машины, а не вход в выбор (issue
                    #680). Промахнулась догадка — до этой кнопки уйти из строки было
                    некуда, кроме как через чекбокс и кнопку под таблицей. */}
                <Chip icon={Search} className="shrink-0" title="Выбрать другой документ качества" onClick={() => openPickerFor(m)}>
                  другой
                </Chip>
              </span>
            ) : (
              <span className="flex items-center gap-1.5">
                <span className="flex-1 min-w-0 text-on-surface-variant">—</span>
                {/* Кнопка ВИДИМАЯ, не по hover: необнаруживаемость одиночного пути и есть
                    предмет жалобы, а мерцающая колонка на 130 строках недоступна ни с
                    клавиатуры, ни с тача. */}
                <Chip icon={LinkIcon} className="shrink-0" title="Выбрать документ качества для этого материала"
                  onClick={() => openPickerFor(m)}>
                  Связать
                </Chip>
              </span>
            )}
          </>
        );
      } },
  ];

  return (
    <DataGrid fixed className="max-h-[50vh]" columns={columns} rows={materials} rowKey={m => m.key} />
  );
}

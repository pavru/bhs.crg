import { useState, useRef, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { Plus, ChevronRight, Folder, FileText, Boxes } from 'lucide-react';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useAccess } from '@/shared/api/access';
import { offeredTypes } from '@/shared/api/typeOwners';
import { useListDocumentTypes, useCreateDocumentType } from '@/shared/api/documentTypes';
import type { DocumentType, DocumentTypeKind } from '@/shared/api/types';
import { LeaveGuardDialog } from './typeEditorShell';
import { TypeEditorProvider, useTypeEditorRegistry } from './typeEditorRegistry';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { useDirtyGuard } from '@/shared/ui/useDirtyGuard';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { uniqueCode } from './uniqueCode';
import { TypeDetail } from './TypeDetail';
import { CreateForm } from './CreateForm';
import { fieldCount } from './documentTypeHelpers';

// ─── Page (parameterised by kind) ──────────────────────────────────────────────

interface TypesPageProps {
  kind: DocumentTypeKind;
}

// Выбор в URL + память последнего открытого — общий хелпер (issue #787). Обе страницы («Типы
// документов» и «Составные типы») — один компонент, поэтому память раздельная по kind.
const lastTypeKey = (kind: DocumentTypeKind) => `types-last:${kind}`;
const SELECTION_KEYS = ['type'] as const;

export function DocumentTypesPage({ kind }: TypesPageProps) {
  const [createOpen, setCreateOpen] = useState(false);
  const [query, setQuery] = useState('');

  // Порядок разрешения при входе: `?type=` → localStorage → первый в списке (страхует `?? filtered[0]`
  // ниже, поэтому удалённый или не проходящий kind-фильтр id просто игнорируется).
  const navigate = useNavigate();
  const { values, remember } = useRememberedSelection(lastTypeKey(kind), SELECTION_KEYS);
  const selectedId = values.type || null;
  const setSelectedId = (id: string | null) => remember({ type: id ?? '' });
  const { data: allDocTypes = [], isLoading } = useListDocumentTypes();
  const { data: access } = useAccess();

  // Реестр незасохранённых форм текущего типа (явное сохранение, issue #197 / #210 — общий).
  const { registry, anyDirty, saving, saveAll, resetAll } = useTypeEditorRegistry();

  // Гард при уходе с типа с несохранёнными правками (общий useDirtyGuard, issue #210 Этап 1b).
  // Всё, что делает переход, живёт в onCommit — то есть ПОСЛЕ подтверждения: отменив диалог,
  // пользователь должен остаться ровно там же, с тем же поиском.
  const { request, dialogProps } = useDirtyGuard<{ id: string | null; jump?: boolean }>({
    isDirty: anyDirty, saving, saveAll,
    onCommit: ({ id, jump }) => {
      // Переход из панели проверки Typst-блоков может указать на тип другого kind (граф блоков
      // строится по всем типам): такой id страница не покажет — `filtered` его не содержит, и
      // выбор молча съехал бы на первый тип, а память страницы осталась бы отравленной. Уводим
      // на соседнюю страницу — гард уже отработал, правки сохранены или отброшены осознанно.
      const target = id ? allDocTypes.find(t => t.id === id) : null;
      if (target && target.kind !== kind) {
        navigate(`/${target.kind === 'Composite' ? 'composite-types' : 'document-types'}?type=${target.id}`);
        return;
      }
      setSelectedId(id);
      // Программный переход обязан сделать цель видимой: при активном поиске тип открывается в
      // детали, но в рейле его нет — ни строки, ни подсветки, и непонятно, где ты оказался.
      if (jump) setQuery('');
    },
  });
  const requestSelect = (id: string) => { if (id !== selectedId) request({ id }); };
  // Переход по иерархии и из панели блоков (issue #784).
  const jumpToType = (id: string) => { if (id !== selectedId) request({ id, jump: true }); };

  // Гард ухода со страницы по маршруту (issue #307): сайдбар-навигация перехватывается, показываем
  // тот же диалог. `routeLeave` хранит отложенный переход (proceed).
  const [routeLeave, setRouteLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(anyDirty, (proceed) => setRouteLeave(() => proceed));

  // ⚠️ Фильтруется СПИСОК ВЫБОРА, а не источник разрешения: `allDocTypes` ниже уходит в детали
  // как есть. Убери мы типы выключенного модуля из общего массива — селектор поля, чей `typeId`
  // указывает на такой тип, не нашёл бы своего значения, показался бы пустым, и следующее
  // сохранение схемы записало бы `typeId: null`. На экране это неотличимо от «сохранилось как
  // было», то есть потеря вышла бы тихой.
  const filtered = offeredTypes(allDocTypes, access)
    .filter(dt => dt.kind === kind)
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const allGroups = [...new Set(filtered.map(dt => dt.group).filter((g): g is string => !!g))]
    .sort((a, b) => a.localeCompare(b, 'ru'));

  const title = kind === 'Document' ? 'Типы документов' : 'Составные типы';
  const addLabel = kind === 'Document' ? 'Добавить тип документа' : 'Добавить составной тип';

  // Поиск по левому списку + группировка (пустая группа — первой).
  const q = query.trim().toLowerCase();
  const listed = q ? filtered.filter(t => `${t.name} ${t.code}`.toLowerCase().includes(q)) : filtered;
  const groupOrder: string[] = [];
  const byGroup = new Map<string, DocumentType[]>();
  for (const t of listed) {
    const g = t.group ?? '';
    if (!byGroup.has(g)) { byGroup.set(g, []); groupOrder.push(g); }
    byGroup.get(g)!.push(t);
  }
  groupOrder.sort((a, b) => a === '' ? -1 : b === '' ? 1 : a.localeCompare(b, 'ru'));

  // Выбранный тип: из выбора (если ещё в отфильтрованных) иначе первый.
  const selected = filtered.find(t => t.id === selectedId) ?? filtered[0];

  // Заголовок вкладки: показанный тип замещает раздел. Именно показанный, а не `selectedId`:
  // тот приходит из URL/памяти и может называть удалённый тип или тип другого kind.
  useDocumentTitle(selected
    ? `${kind === 'Composite' ? 'Составной тип' : 'Тип'} «${selected.name}»`
    : null);

  // Дублирование типа со схемой (клиентский клон, issue #210 Этап 2).
  const createDoc = useCreateDocumentType();
  const duplicateType = (dt: DocumentType) => createDoc.mutate({
    name: `Копия ${dt.name}`, code: uniqueCode(dt.code, new Set(allDocTypes.map(x => x.code))),
    // Копия достаётся тому же владельцу: дублируют тип, чтобы получить такой же, а не такой же,
    // но у другого хозяина.
    kind: dt.kind, parentId: dt.parentId ?? null, module: dt.module,
    schema: JSON.stringify(dt.schema), isAbstract: dt.kind === 'Document' ? dt.isAbstract : false,
  });

  const overlay = isLoading
    ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>
    : filtered.length === 0
      ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">
          {kind === 'Document' ? 'Типов документов не создано' : 'Составных типов не создано'}
        </div>
      : null;

  return (
    <>
      <TypeEditorProvider value={registry}>
        <ListDetailShell
          title={title}
          subtitle={kind === 'Composite' ? 'Переиспользуемые структуры полей для использования внутри типов документов' : undefined}
          headerAction={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>{addLabel}</Button>}
          overlay={overlay}
          nav={<TypeListPanel
            groupOrder={groupOrder} byGroup={byGroup} allDocTypes={allDocTypes}
            selectedId={selected?.id ?? null} onSelect={requestSelect}
            query={query} onQuery={setQuery} />}
          detail={selected ? (
            <TypeDetail key={selected.id} docType={selected} allDocTypes={allDocTypes}
              allGroups={allGroups} onDeleted={() => setSelectedId(null)}
              dirty={anyDirty} saving={saving} onSaveAll={saveAll} onRevert={resetAll}
              onDuplicate={() => duplicateType(selected)} onSelectType={jumpToType} />
          ) : (
            <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Ничего не найдено</div>
          )} />
      </TypeEditorProvider>

      <LeaveGuardDialog {...dialogProps} />

      {/* Гард ухода по маршруту (сайдбар/перезагрузка), issue #307 */}
      <LeaveGuardDialog
        open={routeLeave !== null} saving={saving}
        onCancel={() => setRouteLeave(null)}
        onDiscard={() => { const p = routeLeave; setRouteLeave(null); p?.(); }}
        onSave={async () => {
          try { await saveAll(); const p = routeLeave; setRouteLeave(null); p?.(); }
          catch { setRouteLeave(null); /* ошибка показана в форме */ }
        }} />

      <Modal open={createOpen} onOpenChange={setCreateOpen}
        title={kind === 'Document' ? 'Новый тип документа' : 'Новый составной тип'}
        wide flushBody>
        {createOpen && (
          <CreateForm kind={kind} onClose={() => setCreateOpen(false)}
            onCreated={created => setSelectedId(created.id)} allDocTypes={allDocTypes} />
        )}
      </Modal>
    </>
  );
}

/** Левая панель list-detail (issue #197): поиск + группы + пилюли-типы со счётчиком полей. */
function TypeListPanel({ groupOrder, byGroup, allDocTypes, selectedId, onSelect, query, onQuery }: {
  groupOrder: string[];
  byGroup: Map<string, DocumentType[]>;
  allDocTypes: DocumentType[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  query: string;
  onQuery: (q: string) => void;
}) {
  // Группы навигации: храним только то, что пользователь переключил руками (true — раскрыл,
  // false — свернул). Нетронутая группа раскрыта, если в ней лежит выбранный тип — иначе
  // восстановленный при входе выбор (#778) виден только в детали, а рейл стоит свёрнутым и без
  // единой подсветки. При активном поиске все группы раскрыты, чтобы результаты были видны.
  //
  // Сворачивание группы с выбранным типом помним вместе с самим выбором (ключ «тип:группа»):
  // иначе оно пережило бы и смену группы у открытого типа, и переход выбора в свёрнутую ранее
  // группу — тип пропал бы из рейла, а подсветки не было бы вовсе.
  const [toggled, setToggled] = useState<Map<string, boolean>>(new Map());
  const searching = query.trim().length > 0;
  // Строку выбранного типа подкручиваем в видимую часть: раскрыть группу мало — на длинном списке
  // цель программного перехода (#784) осталась бы ниже сгиба, то есть снова «не видно, где ты».
  const activeRow = useRef<HTMLButtonElement | null>(null);
  useEffect(() => { activeRow.current?.scrollIntoView({ block: 'nearest' }); }, [selectedId]);
  const selectedGroup = [...byGroup].find(([, items]) => items.some(t => t.id === selectedId))?.[0];
  const groupKey = (g: string) => g === selectedGroup ? `${selectedId}:${g}` : g;
  const groupOpen = (g: string) => toggled.get(groupKey(g)) ?? g === selectedGroup;
  // Считаем от того, что нарисовано (`searching` форсирует раскрытие): клик по шапке во время
  // поиска иначе записывал бы обратное тому, о чём просил пользователь. Предыдущее значение
  // читаем из аргумента, а не из замыкания, — два клика в одном такте не схлопываются в один.
  const toggleGroup = (g: string) => {
    const key = groupKey(g);
    setToggled(m => new Map(m).set(key, !(searching || (m.get(key) ?? g === selectedGroup))));
  };

  const typeRow = (t: DocumentType) => {
    const active = t.id === selectedId;
    const Icon = t.kind === 'Composite' ? Boxes : FileText;
    return (
      <button key={t.id} type="button" onClick={() => onSelect(t.id)}
        ref={active ? activeRow : undefined}
        aria-current={active ? 'true' : undefined}
        className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
          active ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
        <Icon size={17} className="shrink-0" />
        <span className="flex-1 truncate text-sm">{t.name}</span>
        <span className="text-xs text-fg4 shrink-0">{fieldCount(t, allDocTypes)}</span>
      </button>
    );
  };

  // Открытый тип, не прошедший поиск, остаётся в рейле отдельной строкой (issue #792). Программный
  // переход (#784) снимает поиск сам, а вот набранный руками запрос иначе прятал бы открытый тип:
  // деталь показывает его, а в списке ни строки, ни подсветки.
  const listedIds = new Set([...byGroup.values()].flat().map(t => t.id));
  const outside = selectedId && !listedIds.has(selectedId)
    ? allDocTypes.find(t => t.id === selectedId) ?? null : null;

  return (
    <>
      <NavSearchInput value={query} onChange={onQuery} placeholder="Поиск типа…" />
      <div className="flex-1 overflow-y-auto px-2 pb-3">
        {outside && (
          <>
            <div className="px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4">Открыт, вне поиска</div>
            {typeRow(outside)}
          </>
        )}
        {groupOrder.length === 0 && (
          <p className="px-3 py-6 text-center text-sm text-fg4">
            {outside ? 'Больше ничего не найдено' : 'Ничего не найдено'}
          </p>
        )}
        {groupOrder.map(g => {
          const items = byGroup.get(g)!;
          const open = searching || groupOpen(g);
          return (
            <div key={g || '__ungrouped__'}>
              <button type="button" onClick={() => toggleGroup(g)}
                aria-expanded={open}
                className="w-full flex items-center gap-1.5 px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4 hover:text-fg2 transition-colors">
                <ChevronRight size={12} className={`shrink-0 transition-transform ${open ? 'rotate-90' : ''}`} />
                <Folder size={12} className="shrink-0" />
                <span className="truncate flex-1 text-left">{g || 'Без группы'}</span>
                <span className="opacity-70">{items.length}</span>
              </button>
              {open && items.map(typeRow)}
            </div>
          );
        })}
      </div>
    </>
  );
}

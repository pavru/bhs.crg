import { useState } from 'react';
import { ModuleFieldBadge } from './ModuleFieldBadge';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, DragIndicator, Layers, LockIcon } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Button, IconButton } from '@/shared/ui/Button';
import { FIELD_UID, withFieldUid, type SchemaField, type FieldGroup } from '@/shared/api/schema';
import { FieldCard } from './FieldBuilder';
import { fieldTypeSummary, type FieldRegistries } from './fieldTypeOptions';
import { StatusBadge } from '@/shared/ui/StatusBadge';

/**
 * Группированный редактор собственных полей (issue #197 Фаза C): группы = карточки-контейнеры,
 * поля-карточки внутри, «Без группы» — катч-олл снизу. Единственное членство (поле ровно в одной
 * группе). Унаследованные поля показываются компактной read-only строкой (редактируются в панели
 * «Унаследовано»), но участвуют в раскладке и перетаскиваются между группами.
 * Drag&drop: тащим поле за ручку → бросаем на другое поле (вставка перед ним) или на пустую зону
 * группы (в конец) / «Без группы» (разгруппировать). Порядок групп — стрелками на шапке группы.
 */
export function GroupedFieldsEditor({
  fields, onFieldsChange, groups, onGroupsChange, ungroupedOrder = [], onUngroupedOrderChange, parentEffectiveFields, disabledKeys, persistedKeys, onKeyRename, reg,
}: {
  fields: SchemaField[];
  onFieldsChange: (f: SchemaField[]) => void;
  groups: FieldGroup[];
  onGroupsChange: (g: FieldGroup[]) => void;
  /** Явный порядок «Без группы» (свои + унаследованные) — задаётся DnD/стрелками (issue: порядок унасл.). */
  ungroupedOrder?: string[];
  onUngroupedOrderChange?: (o: string[]) => void;
  parentEffectiveFields: SchemaField[];
  disabledKeys?: Set<string>;
  /** Ключи полей из СОХРАНЁННОЙ схемы (issue #355) — их ключи заморожены. */
  persistedKeys?: Set<string>;
  /** Смена ключа сохранённого поля (issue #357) — для миграции данных на сохранении. */
  onKeyRename?: (from: string, to: string) => void;
  reg: FieldRegistries;
}) {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const [justAdded, setJustAdded] = useState<string | null>(null);   // uid только что добавленного поля (issue #526)
  const [dragKey, setDragKey] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null); // ключ группы (или '__ungrouped__') под курсором
  const [dropBeforeKey, setDropBeforeKey] = useState<string | null>(null); // поле, ПЕРЕД которым ляжет; null = в конец контейнера
  const [newGroupTitle, setNewGroupTitle] = useState('');
  // DnD упорядочивания КАРТОЧЕК групп — отдельно от полевого DnD (dragKey). Взаимоисключающие
  // (нативно тащим одно), поэтому обработчики групп/полей друг друга не перебивают (guard по своему state).
  const [dragGroupKey, setDragGroupKey] = useState<string | null>(null);
  const [groupDropBefore, setGroupDropBefore] = useState<string | null>(null); // группа, ПЕРЕД которой ляжет; '__end__' = в конец

  // Показать линию-индикатор вставки перед членом (кроме самого перетаскиваемого).
  const lineBefore = (key: string) => !!dragKey && dropBeforeKey === key && dragKey !== key;

  const ownByKey = new Map(fields.map(f => [f.key, f]));
  const inhByKey = new Map(parentEffectiveFields.map(f => [f.key, f]));
  const groupedKeys = new Set(groups.flatMap(g => g.fieldKeys));

  const ownUngrouped = fields.filter(f => !f.key || !groupedKeys.has(f.key));
  const inhUngrouped = parentEffectiveFields.filter(f => !groupedKeys.has(f.key));

  // Единый упорядоченный список членов «Без группы» (свои + унаследованные) по ungroupedOrder.
  // Стабильно: ключи вне порядка — в конце (свои по массиву fields, затем унаследованные по родителю).
  const ungroupedMembers = (() => {
    const pool: { key: string; own?: SchemaField; inh?: SchemaField }[] = [
      ...ownUngrouped.map(f => ({ key: f.key, own: f })),
      ...inhUngrouped.map(f => ({ key: f.key, inh: f })),
    ];
    const posMap = new Map(ungroupedOrder.map((k, i) => [k, i] as const));
    const rank = (k: string) => (k && posMap.has(k) ? posMap.get(k)! : Number.POSITIVE_INFINITY);
    return [...pool].sort((a, b) => rank(a.key) - rank(b.key));
  })();
  const ungroupedMemberKeys = ungroupedMembers.map(m => m.key);

  // ── Операции над данными ──────────────────────────────────────────────────
  const removeKeyFromGroups = (gs: FieldGroup[], key: string) =>
    gs.map(g => ({ ...g, fieldKeys: g.fieldKeys.filter(k => k !== key) }));

  /** Переместить key в контейнер (gKey=null → «Без группы») перед beforeKey (или в конец). */
  function moveKey(key: string, gKey: string | null, beforeKey?: string) {
    if (!key || key === beforeKey) return;
    if (gKey) {
      const gs = removeKeyFromGroups(groups, key).map(g => {
        if (g.key !== gKey) return g;
        const arr = g.fieldKeys.filter(k => k !== key);
        const idx = beforeKey ? arr.indexOf(beforeKey) : -1;
        if (idx >= 0) arr.splice(idx, 0, key); else arr.push(key);
        return { ...g, fieldKeys: arr };
      });
      onGroupsChange(gs);
    } else {
      // «Без группы»: единый явный порядок ungroupedOrder (свои + унаследованные).
      if (groupedKeys.has(key)) onGroupsChange(removeKeyFromGroups(groups, key));
      // текущий видимый порядок ungrouped-ключей + гарантируем присутствие key (мог прийти из группы)
      const base = ungroupedMemberKeys.filter(k => !!k);
      const arr = (base.includes(key) ? base : [...base, key]).filter(k => k !== key);
      const to = beforeKey ? arr.indexOf(beforeKey) : -1;
      arr.splice(to < 0 ? arr.length : to, 0, key);
      onUngroupedOrderChange?.(arr);
    }
  }

  /** Патч своего поля по ссылке (устойчиво к пустым ключам новых полей). */
  function patchOwn(f: SchemaField, patch: Partial<SchemaField>) {
    const idx = fields.indexOf(f);
    if (idx < 0) return;
    const oldKey = f.key;
    onFieldsChange(fields.map((x, i) => i === idx ? { ...x, ...patch } : x));
    // ключ поменялся — мигрируем членство в группах и порядок «Без группы» на новый ключ
    if (patch.key !== undefined && patch.key !== oldKey && oldKey) {
      if (groupedKeys.has(oldKey)) {
        onGroupsChange(groups.map(g => ({ ...g, fieldKeys: g.fieldKeys.map(k => k === oldKey ? patch.key! : k) })));
      }
      if (ungroupedOrder.includes(oldKey)) {
        onUngroupedOrderChange?.(ungroupedOrder.map(k => k === oldKey ? patch.key! : k));
      }
    }
  }

  function removeOwn(f: SchemaField) {
    const idx = fields.indexOf(f);
    onFieldsChange(fields.filter((_, i) => i !== idx));
    if (f.key && groupedKeys.has(f.key)) onGroupsChange(removeKeyFromGroups(groups, f.key));
    if (f.key && ungroupedOrder.includes(f.key)) onUngroupedOrderChange?.(ungroupedOrder.filter(k => k !== f.key));
    setOpenIndex(null);
  }

  function addField() {
    const field = withFieldUid({ key: '', title: '', type: 'string', required: false });
    onFieldsChange([...fields, field]);
    setOpenIndex(fields.length);
    setJustAdded(field[FIELD_UID]);   // курсор в «Название» (issue #526)
  }

  // ── Операции над группами ─────────────────────────────────────────────────
  function addGroup() {
    if (!newGroupTitle.trim()) return;
    onGroupsChange([...groups, { key: `group_${Date.now()}`, title: newGroupTitle.trim(), fieldKeys: [] }]);
    setNewGroupTitle('');
  }
  function renameGroup(gKey: string, title: string) {
    onGroupsChange(groups.map(g => g.key === gKey ? { ...g, title } : g));
  }
  function deleteGroup(gKey: string) {
    // поля из группы уходят в «Без группы» (членство просто снимается)
    onGroupsChange(groups.filter(g => g.key !== gKey));
  }
  function moveGroup(gKey: string, dir: -1 | 1) {
    const idx = groups.findIndex(g => g.key === gKey);
    const swap = idx + dir;
    if (idx < 0 || swap < 0 || swap >= groups.length) return;
    const next = [...groups];
    [next[idx], next[swap]] = [next[swap], next[idx]];
    onGroupsChange(next);
  }
  // DnD-перестановка группы: убрать перетаскиваемую, вставить перед beforeKey ('__end__' → в конец).
  function moveGroupTo(gKey: string, beforeKey: string) {
    const from = groups.findIndex(g => g.key === gKey);
    if (from < 0 || gKey === beforeKey) return;
    const next = [...groups];
    const [moved] = next.splice(from, 1);
    const to = beforeKey === '__end__' ? next.length : next.findIndex(g => g.key === beforeKey);
    next.splice(to < 0 ? next.length : to, 0, moved);
    onGroupsChange(next);
  }
  const endGroupDrag = () => { setDragGroupKey(null); setGroupDropBefore(null); };

  // ── Рендер членов контейнера (своё поле = карточка, унаслед. = строка) ────────
  // Обработчики drag всегда навешены (guard внутри) — не зависим от ре-рендера между dragstart и
  // первым dragover, чтобы нативный DnD надёжно срабатывал.
  const memberDropProps = (targetKey: string, containerKey: string | null) => ({
    dragging: !!dragKey && dragKey === targetKey,
    onDragStart: () => { if (targetKey) setDragKey(targetKey); },
    onDragEnd: () => { setDragKey(null); setDropTarget(null); setDropBeforeKey(null); },
    // Над конкретным полем: линия ПЕРЕД ним. stopPropagation — чтобы обработчик тела контейнера
    // (append) не перебивал точную позицию.
    onDragOver: (e: React.DragEvent) => {
      if (dragKey && targetKey && dragKey !== targetKey) {
        e.preventDefault(); e.stopPropagation();
        setDropTarget(containerKey ?? '__ungrouped__');
        setDropBeforeKey(targetKey);
      }
    },
    onDrop: (e: React.DragEvent) => {
      e.stopPropagation();
      if (dragKey && targetKey && dragKey !== targetKey) moveKey(dragKey, containerKey, targetKey);
      setDragKey(null); setDropTarget(null); setDropBeforeKey(null);
    },
  });

  function renderOwn(own: SchemaField, containerKey: string | null, siblingKeys: string[], pos: number) {
    const idx = fields.indexOf(own);
    const dp = memberDropProps(own.key, containerKey);
    return (
      <div key={own[FIELD_UID] ?? `own-${idx}`}>
        {lineBefore(own.key) && <DropLine />}
        <FieldCard
          field={own}
          reg={reg}
          keyConflict={!!own.key && !!disabledKeys?.has(own.key.trim())}
          otherKeys={[...(disabledKeys ?? []), ...fields.filter(f => f !== own).map(f => f.key)]}
          persistedKeys={persistedKeys}
          autoFocus={!!justAdded && own[FIELD_UID] === justAdded}
          onAutoFocused={() => setJustAdded(null)}
          onKeyRename={onKeyRename}
          open={openIndex === idx}
          onToggleOpen={() => setOpenIndex(o => o === idx ? null : idx)}
          onChange={patch => patchOwn(own, patch)}
          onRemove={() => removeOwn(own)}
          onMoveUp={pos > 0 ? () => moveKey(own.key, containerKey, siblingKeys[pos - 1]) : undefined}
          onMoveDown={pos < siblingKeys.length - 1 ? () => moveKey(own.key, containerKey, siblingKeys[pos + 2]) : undefined}
          isFirst={pos === 0}
          isLast={pos === siblingKeys.length - 1}
          {...dp}
        />
      </div>
    );
  }

  function renderInherited(inh: SchemaField, containerKey: string | null) {
    const dp = memberDropProps(inh.key, containerKey);
    return (
      <div key={`inh-${inh.key}`}>
        {lineBefore(inh.key) && <DropLine />}
        <InheritedRow field={inh} typeLabel={fieldTypeSummary(inh, reg)} {...dp} />
      </div>
    );
  }

  /** Член группы по ключу (грудповые поля всегда имеют непустой ключ). */
  function renderMemberByKey(key: string, containerKey: string | null, siblings: string[]) {
    const own = ownByKey.get(key);
    if (own) return renderOwn(own, containerKey, siblings, siblings.indexOf(key));
    const inh = inhByKey.get(key);
    if (inh) return renderInherited(inh, containerKey);
    return null; // осиротевший ключ — пропускаем
  }

  const dropZoneProps = (containerKey: string | null) => {
    const id = containerKey ?? '__ungrouped__';
    return {
      // Тело контейнера (не над полем — обработчик поля делает stopPropagation): бросок в конец.
      onDragOver: (e: React.DragEvent) => { if (dragKey) { e.preventDefault(); setDropTarget(id); setDropBeforeKey(null); } },
      onDrop: () => { if (dragKey) moveKey(dragKey, containerKey); setDragKey(null); setDropTarget(null); setDropBeforeKey(null); },
      highlighted: dropTarget === id,
      // Линия «в конец» — когда курсор над телом этого контейнера, но не над конкретным полем.
      appendLine: !!dragKey && dropTarget === id && dropBeforeKey === null,
    };
  };

  return (
    <div className="space-y-3">
      {/* Без группы (катч-олл) — первой: эти поля идут первыми в формах редактирования (#197) */}
      {(() => {
        const zone = dropZoneProps(null);
        return (
          <div className={`border rounded-md overflow-hidden transition-colors ${zone.highlighted ? 'border-primary bg-primary-container/30' : 'border-outline-variant border-dashed bg-surface-container-low'}`}>
            <div className="flex items-center gap-2 px-3 py-2 bg-surface border-b border-outline-variant">
              <span className="text-body-medium font-medium text-on-surface-variant">Без группы</span>
              <span className="text-body-small text-on-surface-variant">{ownUngrouped.length + inhUngrouped.length}</span>
            </div>
            <div className="p-2 space-y-2 min-h-[3rem]" onDragOver={zone.onDragOver} onDrop={zone.onDrop}>
              {ungroupedMembers.map((m, i) =>
                m.own
                  ? renderOwn(m.own, null, ungroupedMemberKeys, i)
                  : renderInherited(m.inh!, null))}
              {zone.appendLine && ungroupedMembers.length > 0 && <DropLine />}
              <Button type="button" variant="tonal" onClick={addField} icon={<Icon icon={Add} size={18} />} className="w-full justify-center">
                Добавить поле
              </Button>
            </div>
          </div>
        );
      })()}

      {/* Группы-карточки */}
      {groups.map((group, gi) => {
        const zone = dropZoneProps(group.key);
        const members = group.fieldKeys.filter(k => ownByKey.has(k) || inhByKey.has(k));
        const draggingThis = dragGroupKey === group.key;
        return (
          <div key={group.key}>
            {/* Место вставки при перетаскивании группы (перед этой карточкой) */}
            {!!dragGroupKey && !draggingThis && groupDropBefore === group.key && <DropLine />}
            <div
              className={`border rounded-md overflow-hidden transition-colors ${draggingThis ? 'opacity-40' : ''} ${zone.highlighted ? 'border-primary bg-primary-container/30' : 'border-outline-variant bg-surface-container-low'}`}
              onDragOver={e => { if (dragGroupKey && !draggingThis) { e.preventDefault(); setGroupDropBefore(group.key); } }}
              onDrop={() => { if (dragGroupKey && !draggingThis) { moveGroupTo(dragGroupKey, group.key); endGroupDrag(); } }}>
            {/* Шапка группы */}
            <div className="flex items-center gap-2 px-3 py-2 bg-surface border-b border-outline-variant">
              <IconButton label={`Переместить группу «${group.title || group.key}» перетаскиванием`}
                title="Перетащить для изменения порядка групп"
                size="sm"
                className="cursor-grab active:cursor-grabbing"
                draggable
                onDragStart={() => setDragGroupKey(group.key)}
                onDragEnd={endGroupDrag}>
                <Icon icon={DragIndicator} size={18} />
              </IconButton>
              <Icon icon={Layers} size={18} className="text-on-surface-variant shrink-0" />
              <input
                value={group.title}
                onChange={e => renameGroup(group.key, e.target.value)}
                placeholder="Название группы"
                className="flex-1 min-w-0 text-body-medium font-medium border-b border-transparent hover:border-outline focus:border-primary bg-transparent outline-none"
              />
              <span className="text-body-small text-on-surface-variant shrink-0">{members.length}</span>
              <div className="flex items-center gap-0.5 shrink-0">
                <MoveButtons onUp={() => moveGroup(group.key, -1)} onDown={() => moveGroup(group.key, 1)}
                  isFirst={gi === 0} isLast={gi === groups.length - 1} />
                <IconButton label="Удалить группу (поля → «Без группы»)"
                  size="sm"
                  danger
                  onClick={() => deleteGroup(group.key)}>
                  <Icon icon={Delete} size={18} />
                </IconButton>
              </div>
            </div>
            {/* Тело группы (drop-зона) */}
            <div className="p-2 space-y-2 min-h-[3rem]" onDragOver={zone.onDragOver} onDrop={zone.onDrop}>
              {members.length === 0 && (
                <p className="text-body-small text-on-surface-variant text-center py-3 select-none">
                  {dragKey ? 'Отпустите поле здесь' : 'Пусто — перетащите поле сюда'}
                </p>
              )}
              {members.map(k => renderMemberByKey(k, group.key, members))}
              {zone.appendLine && members.length > 0 && <DropLine />}
            </div>
            </div>
          </div>
        );
      })}

      {/* Добавить группу (заодно drop-зона «в конец» при перетаскивании групп) */}
      {!!dragGroupKey && groupDropBefore === '__end__' && <DropLine />}
      <div className="flex items-center gap-2"
        onDragOver={e => { if (dragGroupKey) { e.preventDefault(); setGroupDropBefore('__end__'); } }}
        onDrop={() => { if (dragGroupKey) { moveGroupTo(dragGroupKey, '__end__'); endGroupDrag(); } }}>
        <input
          value={newGroupTitle}
          onChange={e => setNewGroupTitle(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addGroup(); } }}
          placeholder="Название новой группы"
          className="flex-1 border border-dashed border-outline rounded-sm px-3 py-2 text-body-medium bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary focus:border-primary"
        />
        <Button type="button" variant="outlined" size="sm" onClick={addGroup} disabled={!newGroupTitle.trim()} icon={<Icon icon={Add} size={18} />}>
          Группа
        </Button>
      </div>
    </div>
  );
}

// ── Индикатор места вставки при drag&drop (issue: линия, куда ляжет поле) ──────
function DropLine() {
  return <div aria-hidden className="h-0.5 rounded-full bg-primary mb-2 shadow-[0_0_0_1px_var(--color-primary)]" />;
}

// ── Компактная read-only строка унаследованного поля внутри группы ────────────
function InheritedRow({ field, typeLabel, dragging, onDragStart, onDragEnd, onDragOver, onDrop }: {
  field: SchemaField;
  typeLabel: string;
  dragging?: boolean;
  onDragStart?: () => void;
  onDragEnd?: () => void;
  onDragOver?: (e: React.DragEvent) => void;
  onDrop?: (e: React.DragEvent) => void;
}) {
  return (
    <div
      className={`flex items-center gap-2 pl-2 pr-3 py-1.5 rounded-md border transition-colors ${dragging ? 'border-primary' : 'border-outline-variant bg-surface-container/40'}`}
      draggable onDragStart={onDragStart} onDragEnd={onDragEnd} onDragOver={onDragOver} onDrop={onDrop}>
      <Icon icon={DragIndicator} size={18} className="text-on-surface-variant shrink-0 cursor-grab" />
      <Icon icon={LockIcon} size={18} className="text-on-surface-variant shrink-0" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5">
          <span className="text-body-medium text-on-surface-variant truncate">{field.title || field.key}</span>
          {field.required && <span className="text-label-small text-error shrink-0">обяз.</span>}
        </span>
        <span className="block text-body-small text-on-surface-variant font-mono truncate">{field.key}</span>
      </span>
      <ModuleFieldBadge field={field} />
      <span className="text-label-small px-1.5 py-0.5 rounded-xs bg-warning-container text-on-warning-container shrink-0">унаслед.</span>
      <StatusBadge>{typeLabel}</StatusBadge>
    </div>
  );
}

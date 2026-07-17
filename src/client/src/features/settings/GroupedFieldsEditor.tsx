import { useState } from 'react';
import { GripVertical, Layers, Trash2, ArrowUp, ArrowDown, Plus, Lock } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import type { SchemaField, FieldGroup } from '@/shared/api/schema';
import { FieldCard, fieldTypeSummary, type FieldRegistries } from './FieldBuilder';

/**
 * Группированный редактор собственных полей (issue #197 Фаза C): группы = карточки-контейнеры,
 * поля-карточки внутри, «Без группы» — катч-олл снизу. Единственное членство (поле ровно в одной
 * группе). Унаследованные поля показываются компактной read-only строкой (редактируются в панели
 * «Унаследовано»), но участвуют в раскладке и перетаскиваются между группами.
 * Drag&drop: тащим поле за ручку → бросаем на другое поле (вставка перед ним) или на пустую зону
 * группы (в конец) / «Без группы» (разгруппировать). Порядок групп — стрелками на шапке группы.
 */
export function GroupedFieldsEditor({
  fields, onFieldsChange, groups, onGroupsChange, ungroupedOrder = [], onUngroupedOrderChange, parentEffectiveFields, disabledKeys, reg,
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
  reg: FieldRegistries;
}) {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const [dragKey, setDragKey] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null); // ключ группы (или '__ungrouped__') под курсором
  const [dropBeforeKey, setDropBeforeKey] = useState<string | null>(null); // поле, ПЕРЕД которым ляжет; null = в конец контейнера
  const [newGroupTitle, setNewGroupTitle] = useState('');

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
    onFieldsChange([...fields, { key: '', title: '', type: 'string', required: false }]);
    setOpenIndex(fields.length);
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
      <div key={`own-${idx}`}>
        {lineBefore(own.key) && <DropLine />}
        <FieldCard
          field={own}
          reg={reg}
          keyConflict={!!own.key && !!disabledKeys?.has(own.key.trim())}
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
          <div className={`border rounded-lg overflow-hidden transition-colors ${zone.highlighted ? 'border-brand bg-brand-subtle/30' : 'border-stroke border-dashed bg-base'}`}>
            <div className="flex items-center gap-2 px-3 py-2 bg-surface border-b border-stroke">
              <span className="text-sm font-medium text-fg3">Без группы</span>
              <span className="text-xs text-fg4">{ownUngrouped.length + inhUngrouped.length}</span>
            </div>
            <div className="p-2 space-y-2 min-h-[3rem]" onDragOver={zone.onDragOver} onDrop={zone.onDrop}>
              {ungroupedMembers.map((m, i) =>
                m.own
                  ? renderOwn(m.own, null, ungroupedMemberKeys, i)
                  : renderInherited(m.inh!, null))}
              {zone.appendLine && ungroupedMembers.length > 0 && <DropLine />}
              <Button type="button" variant="tonal" onClick={addField} icon={<Plus size={14} />} className="w-full justify-center">
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
        return (
          <div key={group.key}
            className={`border rounded-lg overflow-hidden transition-colors ${zone.highlighted ? 'border-brand bg-brand-subtle/30' : 'border-stroke bg-base'}`}>
            {/* Шапка группы */}
            <div className="flex items-center gap-2 px-3 py-2 bg-surface border-b border-stroke">
              <Layers size={14} className="text-fg4 shrink-0" />
              <input
                value={group.title}
                onChange={e => renameGroup(group.key, e.target.value)}
                placeholder="Название группы"
                className="flex-1 min-w-0 text-sm font-medium border-b border-transparent hover:border-stroke-strong focus:border-brand bg-transparent outline-none"
              />
              <span className="text-xs text-fg4 shrink-0">{members.length}</span>
              <div className="flex items-center gap-0.5 shrink-0">
                <button type="button" onClick={() => moveGroup(group.key, -1)} disabled={gi === 0}
                  className="p-1 text-fg4 hover:text-fg2 disabled:opacity-20" title="Выше"><ArrowUp size={13} /></button>
                <button type="button" onClick={() => moveGroup(group.key, 1)} disabled={gi === groups.length - 1}
                  className="p-1 text-fg4 hover:text-fg2 disabled:opacity-20" title="Ниже"><ArrowDown size={13} /></button>
                <button type="button" onClick={() => deleteGroup(group.key)}
                  className="p-1 text-fg4 hover:text-danger" title="Удалить группу (поля → «Без группы»)"><Trash2 size={13} /></button>
              </div>
            </div>
            {/* Тело группы (drop-зона) */}
            <div className="p-2 space-y-2 min-h-[3rem]" onDragOver={zone.onDragOver} onDrop={zone.onDrop}>
              {members.length === 0 && (
                <p className="text-xs text-fg4 text-center py-3 select-none">
                  {dragKey ? 'Отпустите поле здесь' : 'Пусто — перетащите поле сюда'}
                </p>
              )}
              {members.map(k => renderMemberByKey(k, group.key, members))}
              {zone.appendLine && members.length > 0 && <DropLine />}
            </div>
          </div>
        );
      })}

      {/* Добавить группу */}
      <div className="flex items-center gap-2">
        <input
          value={newGroupTitle}
          onChange={e => setNewGroupTitle(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addGroup(); } }}
          placeholder="Название новой группы"
          className="flex-1 border border-dashed border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand focus:border-brand"
        />
        <Button type="button" variant="outlined" size="sm" onClick={addGroup} disabled={!newGroupTitle.trim()} icon={<Plus size={14} />}>
          Группа
        </Button>
      </div>
    </div>
  );
}

// ── Индикатор места вставки при drag&drop (issue: линия, куда ляжет поле) ──────
function DropLine() {
  return <div aria-hidden className="h-0.5 rounded-full bg-brand mb-2 shadow-[0_0_0_1px_var(--color-brand)]" />;
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
      className={`flex items-center gap-2 pl-2 pr-3 py-1.5 rounded-lg border transition-colors ${dragging ? 'border-brand' : 'border-stroke bg-muted/40'}`}
      draggable onDragStart={onDragStart} onDragEnd={onDragEnd} onDragOver={onDragOver} onDrop={onDrop}>
      <GripVertical size={15} className="text-fg4 shrink-0 cursor-grab" />
      <Lock size={12} className="text-fg4 shrink-0" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5">
          <span className="text-sm text-fg2 truncate">{field.title || field.key}</span>
          {field.required && <span className="text-[11px] text-danger shrink-0">обяз.</span>}
        </span>
        <span className="block text-xs text-fg4 font-mono truncate">{field.key}</span>
      </span>
      <span className="text-[11px] px-1.5 py-0.5 rounded bg-warning-subtle text-warning shrink-0">унаслед.</span>
      <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-fg3 shrink-0">{typeLabel}</span>
    </div>
  );
}

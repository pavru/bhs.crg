import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, ContentCopy, Delete, Description, KeyboardArrowDown, KeyboardArrowUp, LockIcon, OpenInNew, Star, Warning } from '@/shared/ui/icons';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal } from '@/shared/ui/Modal';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import type { PickType } from '@/shared/ui/TypePicker';
import type { Template, DocumentType } from '@/shared/api/types';
import { useDeleteTemplate, type TemplateUsage } from '@/shared/api/templates';
import { ruCount } from '@/shared/utils/pluralize';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import type { TemplateGroup } from './groupTemplates';
import { Tooltip } from '@/shared/ui/Tooltip';

/** Карта использования версий (templateId → usage); пустой объект, пока не загружено. */
export type TemplateUsageMap = Record<string, TemplateUsage>;

// ─── Version cleanup modal ────────────────────────────────────────────────────

export function VersionCleanupModal({ group, usage = {}, onClose, onDeleted }: {
  group: TemplateGroup;
  usage?: TemplateUsageMap;
  onClose: () => void;
  onDeleted: (deletedIds: Set<string>) => void;
}) {
  const deleteMutation = useDeleteTemplate();
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState('');

  // Причина защиты версии от массового удаления (issue #364): рабочая / по умолчанию / запиннута.
  function protectReason(t: Template, isLatest: boolean): string | null {
    if (usage[t.id]?.count) return `🔒 используется в ${ruCount(usage[t.id].count, 'докум.', 'докум.', 'докум.')} — по одной`;
    if (t.isDefault) return '★ по умолчанию';
    if (isLatest) return 'рабочая версия';
    return null;
  }

  const allDefault = group.versions.length > 0 && group.versions.every(t => t.isDefault);
  const protectedIds = new Set<string>();
  if (group.versions.length > 0) protectedIds.add(group.versions[0].id);
  if (!allDefault) {
    for (const t of group.versions) {
      if (t.isDefault) protectedIds.add(t.id);
    }
  }
  // Запиннутые версии массово не удаляем — только индивидуально с предупреждением.
  for (const t of group.versions) {
    if (usage[t.id]?.count) protectedIds.add(t.id);
  }

  const [toDeleteIds, setToDeleteIds] = useState<Set<string>>(
    () => new Set(group.versions.filter(t => !protectedIds.has(t.id)).map(t => t.id)),
  );

  function toggleVersion(id: string) {
    setToDeleteIds(prev => toggleInSet(prev, id));
  }

  const toDelete = group.versions.filter(t => toDeleteIds.has(t.id));

  async function handleDelete() {
    setDeleting(true);
    setError('');
    const deletedIds = new Set<string>();
    try {
      for (const t of toDelete) {
        await deleteMutation.mutateAsync({ id: t.id, documentTypeId: t.documentTypeId });
        deletedIds.add(t.id);
      }
      onDeleted(deletedIds);
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка при удалении');
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Очистка старых версий"
      footer={
        <div className="flex justify-end gap-3">
          <button onClick={onClose} className="px-4 py-2 text-body-medium text-on-surface-variant state-layer rounded-sm">Отмена</button>
          <button onClick={handleDelete} disabled={deleting || toDelete.length === 0}
            className="px-4 py-2 text-body-medium bg-error hover:bg-error text-on-error rounded-sm disabled-container transition-colors">
            {deleting ? 'Удаление...' : `Удалить ${toDelete.length} вер.`}
          </button>
        </div>
      }>
      <div className="space-y-4">
        <p className="text-body-medium text-on-surface-variant">
          Шаблон <strong>«{group.name}»</strong> — {group.versions.length} версий.
          Отметьте версии для удаления. Рабочая, по умолчанию и запиннутые документами версии защищены —
          запиннутые удаляются по одной, с предупреждением.
        </p>
        <div className="border border-outline-variant rounded-md overflow-hidden">
          {group.versions.map((t, i) => {
            const protected_ = protectedIds.has(t.id);
            const checked = toDeleteIds.has(t.id);
            return (
              <label key={t.id}
                className={`flex items-center gap-3 px-3 py-2.5 text-body-medium cursor-pointer
                  ${i > 0 ? 'border-t border-surface-container' : ''}
                  ${protected_ ? 'opacity-60 cursor-not-allowed bg-surface-container-low' : checked ? 'bg-error-container hover:bg-error-container' : 'state-layer'}`}>
                <input
                  type="checkbox"
                  checked={checked}
                  disabled={protected_}
                  onChange={() => toggleVersion(t.id)}
                  className="w-4 h-4 rounded-xs border-outline text-error disabled-content"
                />
                <span className="font-mono text-on-surface-variant shrink-0">v{t.version}</span>
                <span className="flex items-center gap-1.5 flex-1 min-w-0">
                  {t.isActive && <span className="text-body-small text-on-success-container bg-success-container px-1.5 py-0.5 rounded-xs shrink-0">активный</span>}
                  {t.isDefault && <span className="text-body-small text-on-warning-container bg-warning-container px-1.5 py-0.5 rounded-xs shrink-0">по умолч.</span>}
                  {i === 0 && !t.isActive && <span className="text-body-small text-on-surface-variant shrink-0">последняя</span>}
                  {t.comment && <span className="text-body-small text-on-surface-variant truncate" title={t.comment}>{t.comment}</span>}
                </span>
                {protected_ && (
                  <span className="flex items-center gap-1 text-body-small text-on-surface-variant shrink-0">
                    {usage[t.id]?.count ? <Icon icon={LockIcon} size={18} className="text-on-surface-variant" /> : null}
                    {protectReason(t, i === 0) ?? 'защищена'}
                  </span>
                )}
                {!protected_ && checked && <span className="text-body-small text-error shrink-0">удалить</span>}
              </label>
            );
          })}
        </div>
        {toDelete.length === 0
          ? <p className="text-body-medium text-on-surface-variant">Ничего не выбрано для удаления.</p>
          : <p className="text-body-medium text-on-surface-variant">Будет удалено: <strong>{toDelete.length}</strong> вер.</p>
        }
        {error && <p className="text-body-medium text-error">{error}</p>}
      </div>
    </Modal>
  );
}

// ─── Doc type selector ────────────────────────────────────────────────────────

export function DocTypeSelector({ docTypes, selected, onSelect }: {
  docTypes: DocumentType[]; selected: string; onSelect: (id: string) => void;
}) {
  const nonAbstractDocs = docTypes
    .filter(dt => dt.kind === 'Document' && !dt.isAbstract)
    .map<PickType>(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Типы документов' }));
  return (
    <TypePickerField size="sm" className="w-56" recentKey="doc-type" title="Тип документа"
      placeholder="Выберите тип документа" aria-label="Тип документа"
      types={nonAbstractDocs} value={selected || undefined} onChange={id => id && onSelect(id)} />
  );
}

// ─── Grouped sidebar ──────────────────────────────────────────────────────────

export function TemplateSidebar({ groups, selectedTemplate, maxVersions, documentTypeId, usage = {}, onSelect, onNew, onDelete, onDeleteGroup, onDuplicate, onCleanup }: {
  groups: TemplateGroup[];
  selectedTemplate: Template | null;
  maxVersions: number;
  documentTypeId: string;
  usage?: TemplateUsageMap;
  onSelect: (t: Template) => void;
  onNew: () => void;
  onDelete: (t: Template) => void;
  onDeleteGroup: (g: TemplateGroup) => void;
  onDuplicate: (g: TemplateGroup) => void;
  onCleanup: (g: TemplateGroup) => void;
}) {
  /**
   * Раскрытые группы. Группа выбранного шаблона раскрывается САМА — но вычислением, а не эффектом
   * (issue #858).
   *
   * <p>Правило прежнее и держится на паре «набор + имя, для которого он посчитан». Пока выбрано
   * другое имя, оно подмешивается в набор на лету; свернул человек эту группу руками — запись
   * помечается его именем, и дальше набор берётся как есть. Смена выбора снова подмешает новое
   * имя, не тронув то, что человек свернул.</p>
   */
  const selectedName = selectedTemplate?.name ?? null;
  // Метка — ИДЕНТИФИКАТОР выбранного шаблона, а не имя группы. По имени свёрнутая группа не
  // раскрывалась бы снова: выбрали шаблон из другой группы, вернулись в эту — имя то же, метка
  // совпадает, и подмешивание не срабатывает. Новая версия, дубликат, свежесозданный шаблон — это
  // другой идентификатор, и группа раскроется, как раскрывалась эффектом (поймано ревью PR #865).
  const selectedId = selectedTemplate?.id ?? null;
  const [expanded, setExpanded] = useState<{ forId: string | null; names: Set<string> }>(
    () => ({ forId: null, names: new Set<string>() }),
  );
  const withSelected = (names: Set<string>) =>
    expanded.forId === selectedId || !selectedName ? names : new Set([...names, selectedName]);
  const expandedNames = withSelected(expanded.names);

  function toggleExpand(name: string) {
    setExpanded(prev => {
      const base = prev.forId === selectedId || !selectedName
        ? prev.names
        : new Set([...prev.names, selectedName]);
      return { forId: selectedId, names: toggleInSet(base, name) };
    });
  }

  function handleGroupHeaderClick(group: TemplateGroup) {
    const wasExpanded = expandedNames.has(group.name);
    if (!wasExpanded) {
      const active = group.versions.find(t => t.isActive) ?? group.versions[0];
      if (active) onSelect(active);
    }
    toggleExpand(group.name);
  }

  return (
    <aside className="w-64 border-r border-outline-variant bg-surface flex flex-col">
      <div className="px-4 py-3 border-b border-surface-container flex items-center justify-between">
        <span className="text-body-medium font-medium text-on-surface-variant">Шаблоны</span>
        <Tooltip content="Новый шаблон" name>
          <button onClick={onNew} className="text-primary state-layer">
            <Icon icon={Add} size={18} />
          </button>
        </Tooltip>
      </div>
      <TemplateAssetsPanel
        key={`assets-${documentTypeId}`}
        scope="DocumentType" scopeId={documentTypeId} title="Ассеты типа документа"
        hintScopes={[{ scope: 'System', scopeId: null, label: 'системных' }]}
      />
      <div className="flex-1 overflow-auto py-1">
        {groups.length === 0 && (
          <div className="px-4 py-3 text-body-medium text-on-surface-variant">Шаблонов нет</div>
        )}
        {groups.map(group => {
          const isExpanded = expandedNames.has(group.name);
          const hasDefault = group.versions.some(t => t.isDefault);
          const tooMany = group.versions.length >= maxVersions;
          const latestVersion = group.versions[0]?.version ?? 1;
          const isGroupSelected = selectedTemplate?.name === group.name;

          return (
            <div key={group.name}>
              <div className={`state-layer flex items-center group/grp ${isGroupSelected ? 'bg-primary-container' : ''}`}>
                <button
                  onClick={() => handleGroupHeaderClick(group)}
                  className="flex-1 flex items-center gap-1.5 px-3 py-2.5 text-left min-w-0"
                >
                  {isExpanded
                    ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant shrink-0" />
                    : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" />}
                  {hasDefault && <Icon icon={Star} size={18} className="fill-warning text-warning shrink-0" />}
                  <span className={`text-body-medium truncate flex-1 ${isGroupSelected ? 'text-primary font-medium' : 'text-on-surface'}`}>
                    {group.name}
                  </span>
                  <span className="text-body-small text-on-surface-variant shrink-0">v{latestVersion}</span>
                  {tooMany && (
                    <Icon icon={Warning} size={18} className="text-warning shrink-0" />
                  )}
                </button>
                <Tooltip content="Дублировать шаблон" name>
                  <button
                    onClick={() => onDuplicate(group)}
                    className="px-2 py-2.5 text-on-surface-variant hover:text-primary opacity-0 group-hover/grp:opacity-100 transition-all shrink-0"
                  >
                    <Icon icon={ContentCopy} size={18} />
                  </button>
                </Tooltip>
                <Tooltip content="Удалить шаблон со всеми версиями" name>
                  <button
                    onClick={() => onDeleteGroup(group)}
                    className="px-2 py-2.5 text-on-surface-variant hover:text-error opacity-0 group-hover/grp:opacity-100 transition-all shrink-0"
                  >
                    <Icon icon={Delete} size={18} />
                  </button>
                </Tooltip>
              </div>

              {isExpanded && (
                <div className="border-b border-surface-container">
                  {group.versions.map(t => {
                    const used = usage[t.id]?.count ?? 0;
                    // Удалять индивидуально нельзя рабочую/дефолтную версию (защита active/default — на уровне UI).
                    const deleteBlocked = t.isActive || t.isDefault;
                    return (
                    <div key={t.id}
                      className={`state-layer flex items-center group/ver pl-5
                        ${selectedTemplate?.id === t.id ? 'bg-primary-container' : ''}`}>
                      <button
                        onClick={() => onSelect(t)}
                        className={`flex-1 flex items-center gap-1.5 px-2 py-1.5 text-left min-w-0
                          ${selectedTemplate?.id === t.id ? 'text-primary' : 'text-on-surface-variant'}`}
                      >
                        <span className="text-body-small font-mono shrink-0 w-6">v{t.version}</span>
                        <span className="flex items-center gap-1 flex-1 min-w-0">
                          {t.isActive && <span className="text-body-small text-success shrink-0">активный</span>}
                          {t.isDefault && <span className="text-body-small text-warning shrink-0">по умолч.</span>}
                          {used > 0 && (
                            <Tooltip content={`Используется в ${used} докум.`}>
                              <span className="flex items-center gap-0.5 text-body-small text-on-surface-variant shrink-0">
                                <Icon icon={Description} size={18} /> {used}
                              </span>
                            </Tooltip>
                          )}
                          {t.comment && (
                            <span className="text-body-small text-on-surface-variant truncate" title={t.comment}>{t.comment}</span>
                          )}
                        </span>
                      </button>
                      <div className="pr-1 opacity-0 group-hover/ver:opacity-100 focus-within:opacity-100 transition-all shrink-0">
                        <RowActionsMenu ariaLabel={`Действия версии v${t.version}`} actions={[
                          { key: 'open', label: 'Открыть', icon: <Icon icon={OpenInNew} size={18} />, onSelect: () => onSelect(t) },
                          {
                            key: 'delete', label: 'Удалить версию…', danger: true, icon: <Icon icon={Delete} size={18} />,
                            disabled: deleteBlocked,
                            badge: used > 0 ? `${used} докум.` : undefined,
                            onSelect: () => onDelete(t),
                          },
                        ]} />
                      </div>
                    </div>
                    );
                  })}
                  {tooMany && (
                    <div className="mx-3 mb-2 mt-1 px-2 py-1.5 bg-warning-container border border-outline-variant rounded-sm flex items-center gap-2">
                      <Icon icon={Warning} size={18} className="text-warning shrink-0" />
                      <span className="text-body-small text-warning flex-1">Много версий</span>
                      <button
                        onClick={() => onCleanup(group)}
                        className="text-body-small text-warning underline hover:text-warning shrink-0"
                      >
                        Очистить
                      </button>
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </aside>
  );
}


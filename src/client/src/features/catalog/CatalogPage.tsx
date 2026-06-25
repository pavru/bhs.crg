import { useState, useMemo } from 'react';
import { Plus, Pencil, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  useListCatalogEntities,
  useCreateCatalogEntity,
  useUpdateCatalogEntity,
  useDeleteCatalogEntity,
} from '@/shared/api/catalog';
import type { CatalogEntity } from '@/shared/api/types';

function tryPrettyJson(val: unknown): string {
  try {
    return JSON.stringify(val, null, 2);
  } catch {
    return '{}';
  }
}

function tryParseJson(s: string): { ok: boolean; error?: string } {
  try {
    JSON.parse(s);
    return { ok: true };
  } catch (e) {
    return { ok: false, error: String(e) };
  }
}

interface EntityFormProps {
  entity?: CatalogEntity;
  knownTypes: string[];
  onClose: () => void;
}

function EntityForm({ entity, knownTypes, onClose }: EntityFormProps) {
  const [entityType, setEntityType] = useState(entity?.entityType ?? '');
  const [displayName, setDisplayName] = useState(entity?.displayName ?? '');
  const [dataJson, setDataJson] = useState(tryPrettyJson(entity?.data ?? {}));
  const [error, setError] = useState('');

  const createMutation = useCreateCatalogEntity();
  const updateMutation = useUpdateCatalogEntity();
  const isPending = createMutation.isPending || updateMutation.isPending;

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const parsed = tryParseJson(dataJson);
    if (!parsed.ok) {
      setError('Неверный JSON в поле Данные: ' + parsed.error);
      return;
    }
    try {
      if (entity) {
        await updateMutation.mutateAsync({ id: entity.id, displayName, data: dataJson });
      } else {
        await createMutation.mutateAsync({ entityType, displayName, data: dataJson });
      }
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-4">
      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">Тип сущности</label>
        <input
          list="known-types"
          value={entityType}
          onChange={(e) => setEntityType(e.target.value)}
          disabled={!!entity}
          required
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand disabled:bg-base disabled:text-fg3"
        />
        <datalist id="known-types">
          {knownTypes.map((t) => (
            <option key={t} value={t} />
          ))}
        </datalist>
      </div>
      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">Наименование</label>
        <input
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          required
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand"
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">Данные (JSON)</label>
        <textarea
          value={dataJson}
          onChange={(e) => setDataJson(e.target.value)}
          rows={8}
          spellCheck={false}
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand resize-y"
        />
      </div>
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex justify-end gap-3 pt-2">
        <button
          type="button"
          onClick={onClose}
          className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md transition-colors"
        >
          Отмена
        </button>
        <button
          type="submit"
          disabled={isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50"
        >
          {isPending ? 'Сохранение...' : 'Сохранить'}
        </button>
      </div>
    </form>
  );
}

export function CatalogPage() {
  const [typeFilter, setTypeFilter] = useState('');
  const [search, setSearch] = useState('');
  const [modal, setModal] = useState<{ open: boolean; entity?: CatalogEntity }>({ open: false });

  const { data: entities = [], isLoading } = useListCatalogEntities(typeFilter || undefined);
  const deleteMutation = useDeleteCatalogEntity();

  const knownTypes = useMemo(() => [...new Set(entities.map((e) => e.entityType))].sort(), [entities]);

  const filtered = useMemo(() => {
    if (!search) return entities;
    const s = search.toLowerCase();
    return entities.filter(
      (e) => e.displayName.toLowerCase().includes(s) || e.entityType.toLowerCase().includes(s),
    );
  }, [entities, search]);

  function handleDelete(entity: CatalogEntity) {
    if (!confirm(`Удалить "${entity.displayName}"?`)) return;
    deleteMutation.mutate(entity.id);
  }

  return (
    <div className="p-6">
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-semibold text-fg1">Каталог сущностей</h1>
        <button
          onClick={() => setModal({ open: true })}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors"
        >
          <Plus size={16} /> Добавить
        </button>
      </div>

      <div className="flex gap-3 mb-5">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Поиск по наименованию..."
          className="flex-1 border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand"
        />
        <select
          value={typeFilter}
          onChange={(e) => setTypeFilter(e.target.value)}
          className="border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand"
        >
          <option value="">Все типы</option>
          {knownTypes.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>

      <div className="bg-surface border border-stroke rounded-xl overflow-hidden">
        {isLoading ? (
          <div className="p-10 text-center text-fg4 text-sm">Загрузка...</div>
        ) : filtered.length === 0 ? (
          <div className="p-10 text-center text-fg4 text-sm">Нет записей</div>
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-base border-b border-stroke">
              <tr>
                <th className="text-left px-4 py-3 font-medium text-fg2 w-48">Тип</th>
                <th className="text-left px-4 py-3 font-medium text-fg2">Наименование</th>
                <th className="px-4 py-3 w-20" />
              </tr>
            </thead>
            <tbody>
              {filtered.map((entity) => (
                <tr key={entity.id} className="border-b border-muted last:border-0 hover:bg-base">
                  <td className="px-4 py-3">
                    <span className="bg-brand-subtle text-brand-hover text-xs px-2 py-0.5 rounded font-medium">
                      {entity.entityType}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-fg1">{entity.displayName}</td>
                  <td className="px-4 py-3">
                    <div className="flex gap-2 justify-end">
                      <button
                        onClick={() => setModal({ open: true, entity })}
                        className="text-fg4 hover:text-brand transition-colors"
                        title="Редактировать"
                      >
                        <Pencil size={14} />
                      </button>
                      <button
                        onClick={() => handleDelete(entity)}
                        className="text-fg4 hover:text-danger transition-colors"
                        title="Удалить"
                      >
                        <Trash2 size={14} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <Modal
        open={modal.open}
        onOpenChange={(open) => setModal({ open })}
        title={modal.entity ? 'Редактировать запись' : 'Добавить запись'}
        wide
      >
        {modal.open && (
          <EntityForm
            entity={modal.entity}
            knownTypes={knownTypes}
            onClose={() => setModal({ open: false })}
          />
        )}
      </Modal>
    </div>
  );
}

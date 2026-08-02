import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { CheckCircle2, AlertTriangle, ChevronRight, ChevronDown, Loader2, Trash2 } from 'lucide-react';
import { useAuditDocumentType, useApplyAuditFixes, type AuditFix } from '@/shared/api/documentTypes';

/**
 * Отчёт аудита типа + применение исправлений (issue #348/#350). Группировка по категории → по
 * конкретному расхождению (путь) с подсчётом инстансов. Действия применяются ко ВСЕМ инстансам строки
 * (батч): удалить осиротевший ключ/очистить невалидное, либо переименовать верхнеуровневый осиротевший
 * ключ в поле схемы (только в пустую цель — иначе бэкенд пропустит). Мутация данных — с подтверждением.
 */
const CATEGORY_LABEL: Record<string, string> = {
  'orphan-key': 'Осиротевшие поля (нет в текущей схеме)',
  'type-mismatch': 'Несовпадение вида значения с типом поля',
  // issue #642: тонкий слой — базовый тип, шаблон и границы примитива, разбор даты. Те же правила,
  // что и при выпуске документа, но здесь их видят и записи общих данных.
  'value-type': 'Значение не соответствует объявленному типу',
};

interface Row { code: string; path: string; message: string; instances: { id: string; name: string }[] }

export function TypeAuditModal({ typeId, typeName, schemaFieldKeys, open, onClose }: {
  typeId: string; typeName: string; schemaFieldKeys: string[]; open: boolean; onClose: () => void;
}) {
  const { data, isLoading, isError } = useAuditDocumentType(typeId, open);
  const applyFixes = useApplyAuditFixes(typeId);
  const toast = useToast();
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [confirmRow, setConfirmRow] = useState<Row | null>(null);
  const [renameTarget, setRenameTarget] = useState<Record<string, string>>({});

  const groups = useMemo(() => {
    const byRow = new Map<string, Row>();
    for (const f of data?.findings ?? []) {
      const key = `${f.code} ${f.path} ${f.message}`;
      let row = byRow.get(key);
      if (!row) { row = { code: f.code, path: f.path, message: f.message, instances: [] }; byRow.set(key, row); }
      row.instances.push({ id: f.instanceId, name: f.instanceName || '(без имени)' });
    }
    const byCat = new Map<string, Row[]>();
    for (const row of byRow.values()) {
      if (!byCat.has(row.code)) byCat.set(row.code, []);
      byCat.get(row.code)!.push(row);
    }
    for (const rows of byCat.values()) rows.sort((a, b) => b.instances.length - a.instances.length);
    return [...byCat.entries()];
  }, [data]);

  const total = data?.findings.length ?? 0;
  const isTopLevel = (path: string) => !path.includes('.') && !path.includes('[');

  async function apply(row: Row, action: 'remove' | 'rename', targetKey?: string) {
    const fixes: AuditFix[] = row.instances.map(i => ({ instanceId: i.id, action, path: row.path, targetKey }));
    try {
      const res = await applyFixes.mutateAsync(fixes);
      toast.success(`Применено: ${res.applied}${res.skipped ? `, пропущено: ${res.skipped}` : ''}`);
    } catch {
      toast.error('Не удалось применить исправление');
    }
  }

  return (
    <>
      <Modal open={open} onOpenChange={o => { if (!o) onClose(); }} title={`Аудит типа «${typeName}»`} wide>
        {isLoading ? (
          <div className="flex items-center gap-2 py-8 justify-center text-fg4 text-sm">
            <Loader2 size={16} className="animate-spin" /> Проверка инстансов…
          </div>
        ) : isError ? (
          <p className="text-sm text-danger py-6 text-center">Не удалось выполнить аудит.</p>
        ) : (
          <div className="space-y-4">
            <p className="text-xs text-fg4">
              Проверено инстансов (с подтипами): <b className="text-fg2">{data?.instanceCount ?? 0}</b>.
              {total > 0 && <> Найдено расхождений: <b className="text-fg2">{total}</b>.</>}
            </p>

            {total === 0 ? (
              <div className="flex items-center gap-2 p-3 rounded-md text-sm bg-success-subtle text-success">
                <CheckCircle2 size={15} className="shrink-0" /> Данные инстансов соответствуют текущей схеме.
              </div>
            ) : (
              groups.map(([code, rows]) => (
                <div key={code} className="rounded-md border border-stroke overflow-hidden">
                  <div className="flex items-center gap-2 px-3 py-2 bg-base text-xs font-medium text-fg2">
                    <AlertTriangle size={13} className="text-warning shrink-0" />
                    {CATEGORY_LABEL[code] ?? code}
                    <span className="text-fg4 font-normal">· {rows.length}</span>
                  </div>
                  <div className="divide-y divide-muted">
                    {rows.map(row => {
                      // Сообщение в ключе обязательно: у одного значения бывает несколько претензий
                      // сразу («не целое» и «меньше допустимого»), и обе лежат по одному пути.
                      const k = `${row.code} ${row.path} ${row.message}`;
                      const isOpen = expanded.has(k);
                      const canRename = row.code === 'orphan-key' && isTopLevel(row.path);
                      return (
                        <div key={k}>
                          <button type="button"
                            onClick={() => setExpanded(s => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; })}
                            className="w-full flex items-start gap-2 px-3 py-2 text-left hover:bg-base transition-colors">
                            {isOpen ? <ChevronDown size={14} className="text-fg4 shrink-0 mt-0.5" /> : <ChevronRight size={14} className="text-fg4 shrink-0 mt-0.5" />}
                            <span className="min-w-0 flex-1">
                              <code className="text-xs text-fg2 break-all">{row.path}</code>
                              <span className="block text-xs text-fg4">{row.message}</span>
                            </span>
                            <span className="text-xs text-fg4 tabular-nums shrink-0 mt-0.5">{row.instances.length} инст.</span>
                          </button>
                          {isOpen && (
                            <div className="px-3 pb-3 pl-9 space-y-2">
                              <ul className="space-y-0.5">
                                {row.instances.map(i => <li key={i.id} className="text-xs text-fg3 truncate">{i.name}</li>)}
                              </ul>
                              <div className="flex flex-wrap items-center gap-2 pt-1">
                                <Button variant="text" size="sm" danger icon={<Trash2 size={13} />}
                                  disabled={applyFixes.isPending}
                                  onClick={() => setConfirmRow(row)}>
                                  Удалить во всех ({row.instances.length})
                                </Button>
                                {canRename && (
                                  <>
                                    <span className="text-fg4 text-xs">или переименовать в</span>
                                    <select value={renameTarget[k] ?? ''} disabled={applyFixes.isPending}
                                      onChange={e => setRenameTarget(t => ({ ...t, [k]: e.target.value }))}
                                      className="text-xs border border-stroke rounded px-1.5 py-1 bg-surface text-fg1">
                                      <option value="">поле схемы…</option>
                                      {schemaFieldKeys.map(f => <option key={f} value={f}>{f}</option>)}
                                    </select>
                                    <Button variant="text" size="sm" disabled={!renameTarget[k] || applyFixes.isPending}
                                      onClick={() => apply(row, 'rename', renameTarget[k])}>
                                      Переименовать
                                    </Button>
                                  </>
                                )}
                              </div>
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>
              ))
            )}
          </div>
        )}
      </Modal>

      <ConfirmDialog
        open={!!confirmRow}
        onOpenChange={o => { if (!o) setConfirmRow(null); }}
        title="Удалить значение из данных?"
        description={confirmRow
          ? <p>Ключ <code className="font-mono">{confirmRow.path}</code> будет удалён из реквизитов <b>{confirmRow.instances.length}</b> инстанс(ов). Действие необратимо (значение сохраняется в журнале ответа).</p>
          : null}
        confirmLabel="Удалить"
        onConfirm={() => { const r = confirmRow; setConfirmRow(null); if (r) void apply(r, 'remove'); }}
      />
    </>
  );
}

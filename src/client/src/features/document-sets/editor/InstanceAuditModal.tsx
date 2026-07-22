import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { CheckCircle2, AlertTriangle, Loader2, Trash2 } from 'lucide-react';
import { useAuditInstance, useApplyInstanceAuditFixes } from '@/shared/api/documentSets';
import type { AuditFinding } from '@/shared/api/documentTypes';

/**
 * Аудит ОДНОГО документа в пользовательском режиме (issue #352) — юзер видит расхождения своего
 * документа с текущей схемой и лечит их сам (без админа): удалить осиротевшее поле / очистить
 * невалидное, либо переименовать верхнеуровневый осиротевший ключ в поле схемы. Reuse серверной
 * машинерии типового аудита, но scope — этот инстанс.
 */
const CATEGORY_LABEL: Record<string, string> = {
  'orphan-key': 'Поля, которых нет в текущей схеме',
  'type-mismatch': 'Несовпадение вида значения с типом поля',
};

export function InstanceAuditModal({ setId, instanceId, docName, schemaFieldKeys, open, onClose }: {
  setId: string; instanceId: string; docName: string; schemaFieldKeys: string[]; open: boolean; onClose: () => void;
}) {
  const { data: findings, isLoading, isError } = useAuditInstance(setId, instanceId, open);
  const applyFixes = useApplyInstanceAuditFixes(setId, instanceId);
  const toast = useToast();
  const [confirm, setConfirm] = useState<AuditFinding | null>(null);
  const [renameTarget, setRenameTarget] = useState<Record<string, string>>({});

  const groups = useMemo(() => {
    const byCat = new Map<string, AuditFinding[]>();
    for (const f of findings ?? []) {
      if (!byCat.has(f.code)) byCat.set(f.code, []);
      byCat.get(f.code)!.push(f);
    }
    return [...byCat.entries()];
  }, [findings]);

  const total = findings?.length ?? 0;
  const isTopLevel = (path: string) => !path.includes('.') && !path.includes('[');

  async function apply(action: 'remove' | 'rename', path: string, targetKey?: string) {
    try {
      const res = await applyFixes.mutateAsync([{ action, path, targetKey }]);
      if (res.applied > 0) toast.success('Документ исправлен');
      else toast.info(res.outcomes[0]?.reason ?? 'Изменений не внесено');
    } catch { toast.error('Не удалось применить исправление'); }
  }

  return (
    <>
      <Modal open={open} onOpenChange={o => { if (!o) onClose(); }} title={`Аудит документа «${docName}»`} wide>
        {isLoading ? (
          <div className="flex items-center gap-2 py-8 justify-center text-fg4 text-sm">
            <Loader2 size={16} className="animate-spin" /> Проверка…
          </div>
        ) : isError ? (
          <p className="text-sm text-danger py-6 text-center">Не удалось выполнить аудит.</p>
        ) : total === 0 ? (
          <div className="flex items-center gap-2 p-3 rounded-md text-sm bg-success-subtle text-success">
            <CheckCircle2 size={15} className="shrink-0" /> Документ соответствует текущей схеме — исправлять нечего.
          </div>
        ) : (
          <div className="space-y-4">
            <p className="text-xs text-fg4">
              Данные документа расходятся с текущей схемой типа. Их можно почистить, не обращаясь к администратору.
              Удаление необратимо (старое значение вернётся в ответе для ручного отката).
            </p>
            {groups.map(([code, items]) => (
              <div key={code} className="rounded-md border border-stroke overflow-hidden">
                <div className="flex items-center gap-2 px-3 py-2 bg-base text-xs font-medium text-fg2">
                  <AlertTriangle size={13} className="text-warning shrink-0" />
                  {CATEGORY_LABEL[code] ?? code}
                  <span className="text-fg4 font-normal">· {items.length}</span>
                </div>
                <div className="divide-y divide-muted">
                  {items.map(f => {
                    const canRename = f.code === 'orphan-key' && isTopLevel(f.path);
                    return (
                      <div key={f.path} className="px-3 py-2.5">
                        <div className="flex items-start gap-2">
                          <span className="min-w-0 flex-1">
                            <code className="text-xs text-fg2 break-all">{f.path}</code>
                            <span className="block text-xs text-fg4">{f.message}</span>
                          </span>
                        </div>
                        <div className="flex flex-wrap items-center gap-2 pt-1.5">
                          <Button variant="text" size="sm" danger icon={<Trash2 size={13} />}
                            disabled={applyFixes.isPending} onClick={() => setConfirm(f)}>
                            Удалить
                          </Button>
                          {canRename && (
                            <>
                              <span className="text-fg4 text-xs">или переименовать в</span>
                              <select value={renameTarget[f.path] ?? ''} disabled={applyFixes.isPending}
                                onChange={e => setRenameTarget(t => ({ ...t, [f.path]: e.target.value }))}
                                className="text-xs border border-stroke rounded px-1.5 py-1 bg-surface text-fg1">
                                <option value="">поле схемы…</option>
                                {schemaFieldKeys.map(k => <option key={k} value={k}>{k}</option>)}
                              </select>
                              <Button variant="text" size="sm" disabled={!renameTarget[f.path] || applyFixes.isPending}
                                onClick={() => apply('rename', f.path, renameTarget[f.path])}>
                                Переименовать
                              </Button>
                            </>
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>
        )}
      </Modal>

      <ConfirmDialog
        open={!!confirm}
        onOpenChange={o => { if (!o) setConfirm(null); }}
        title="Удалить значение из документа?"
        description={confirm
          ? <p>Поле <code className="font-mono">{confirm.path}</code> будет удалено из этого документа. Действие необратимо.</p>
          : null}
        confirmLabel="Удалить"
        onConfirm={() => { const f = confirm; setConfirm(null); if (f) void apply('remove', f.path); }}
      />
    </>
  );
}

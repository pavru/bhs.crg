import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { usePreviewMoveDocument, useMoveDocument, type CopyWarning } from '@/shared/api/documentSets';
import type { Construction } from '@/shared/api/types';

interface TargetSet { id: string; name: string; constructionId: string }

/**
 * Поток «Перенести документ в другой комплект» (issue #283, фаза D): пикер комплекта → превью
 * (затронутые ссылки + блокировка по входящим ссылкам) → перенос → переход в целевой комплект.
 * Строже копирования: документ уходит из текущего; блокируется, если на него ссылаются (guard #269).
 */
export function MoveDocumentDialog({ open, onClose, setId, currentSetName, instance, constructions }: {
  open: boolean;
  onClose: () => void;
  setId: string;
  currentSetName: string;
  instance: { id: string; name: string };
  constructions: Construction[];
}) {
  const navigate = useNavigate();
  const toast = useToast();
  const moveMutation = useMoveDocument();
  const [target, setTarget] = useState<TargetSet | null>(null);
  const targetRef = useRef<TargetSet | null>(null);

  useEffect(() => { if (!open) { setTarget(null); targetRef.current = null; } }, [open]);

  const options: PickType[] = [];
  const meta = new Map<string, TargetSet>();
  for (const c of constructions)
    for (const s of c.sections)
      for (const ds of s.documentSets) {
        if (ds.id === setId) continue;
        options.push({ id: ds.id, name: ds.name, code: s.name, section: c.name });
        meta.set(ds.id, { id: ds.id, name: ds.name, constructionId: c.id });
      }

  function handleSelect(id: string) {
    const t = meta.get(id) ?? null;
    targetRef.current = t;
    setTarget(t);
  }

  const { data: preview, isFetching } = usePreviewMoveDocument(setId, instance.id, target?.id);
  const blockedBy = preview?.blockedBy ?? [];
  const warnings = preview?.warnings ?? [];
  const isBlocked = !isFetching && blockedBy.length > 0;

  async function handleMove() {
    if (!target) return;
    await moveMutation.mutateAsync({ setId, instanceId: instance.id, targetSetId: target.id });
    const t = target;
    onClose();
    navigate(`/document-sets/${t.constructionId}/sets/${t.id}`);
    toast.success(`Перенесено в «${t.name}»`);
  }

  return (
    <>
      <TypePicker
        open={open && target === null}
        onOpenChange={o => { if (!o && !targetRef.current) onClose(); }}
        title="Перенести в комплект"
        recentKey="copy-target-set"
        types={options}
        onSelect={handleSelect}
      />

      <ConfirmDialog
        open={open && target !== null}
        onOpenChange={o => { if (!o) { setTarget(null); targetRef.current = null; onClose(); } }}
        title={`Перенести «${instance.name}» в «${target?.name ?? ''}»?`}
        errorTitle="Перенос невозможен"
        blocked={isBlocked
          ? <BlockedByRefs currentSetName={currentSetName} names={blockedBy} />
          : undefined}
        description={
          isFetching
            ? <p className="text-fg4">Проверка ссылок…</p>
            : <MoveInfo currentSetName={currentSetName} warnings={warnings} />
        }
        requireCheckbox={!isBlocked && warnings.length > 0
          ? 'Понимаю: связи, специфичные для комплекта, могут не перенестись'
          : undefined}
        confirmLabel="Перенести"
        onConfirm={handleMove}
      />
    </>
  );
}

function BlockedByRefs({ currentSetName, names }: { currentSetName: string; names: string[] }) {
  return (
    <div>
      <p className="mb-1.5 font-medium">На документ ссылаются в комплекте «{currentSetName}» — сначала снимите ссылки:</p>
      <ul className="list-disc pl-4 space-y-0.5">
        {names.map(n => <li key={n}>{n}</li>)}
      </ul>
    </div>
  );
}

function MoveInfo({ currentSetName, warnings }: { currentSetName: string; warnings: CopyWarning[] }) {
  return (
    <>
      <p>Документ будет удалён из комплекта «{currentSetName}». Собранный PDF обоих комплектов устареет.</p>
      {warnings.length > 0 && (
        <ul className="list-disc pl-4 mt-1.5 space-y-0.5">
          {warnings.map(w => (
            <li key={w.kind}>
              {w.label}{w.count > 1 ? ` (${w.count})` : ''}
              {w.names.length > 0 && <span className="text-fg4">: {w.names.join(', ')}</span>}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

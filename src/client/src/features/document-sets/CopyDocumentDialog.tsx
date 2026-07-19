import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { usePreviewCopyDocument, useCopyDocument } from '@/shared/api/documentSets';
import { CopyWarnings } from './CopyWarnings';
import type { Construction } from '@/shared/api/types';

interface TargetSet { id: string; name: string; constructionId: string }

/**
 * Поток «Скопировать документ в другой комплект» (issue #283, фаза C): пикер комплекта (в стиле
 * TypePicker, подпись «Стройка › Раздел») → превью затронутых ссылок (dry-run) в диалоге ДО
 * подтверждения → копирование → тост с переходом. Стратегия B «умная очистка» (backend);
 * опция A «снимок» — фаза C2.
 */
export function CopyDocumentDialog({ open, onClose, setId, instance, constructions }: {
  open: boolean;
  onClose: () => void;
  setId: string;
  instance: { id: string; name: string };
  constructions: Construction[];
}) {
  const navigate = useNavigate();
  const toast = useToast();
  const copyMutation = useCopyDocument();
  const [target, setTarget] = useState<TargetSet | null>(null);
  const targetRef = useRef<TargetSet | null>(null);

  useEffect(() => { if (!open) { setTarget(null); targetRef.current = null; } }, [open]);

  // Плоский список комплектов для пикера (текущий исключаем — копирование только в другой).
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

  const { data: warnings = [], isFetching } = usePreviewCopyDocument(setId, instance.id, target?.id);

  async function handleCopy() {
    if (!target) return;
    await copyMutation.mutateAsync({ setId, instanceId: instance.id, targetSetId: target.id });
    const t = target;
    toast.success(`Скопировано в «${t.name}»`, {
      action: { label: 'Перейти', onClick: () => navigate(`/document-sets/${t.constructionId}/sets/${t.id}`) },
    });
    onClose();
  }

  return (
    <>
      <TypePicker
        open={open && target === null}
        onOpenChange={o => { if (!o && !targetRef.current) onClose(); }}
        title="Скопировать в комплект"
        recentKey="copy-target-set"
        types={options}
        onSelect={handleSelect}
      />

      <ConfirmDialog
        open={open && target !== null}
        onOpenChange={o => { if (!o) { setTarget(null); targetRef.current = null; onClose(); } }}
        title={`Скопировать «${instance.name}» в «${target?.name ?? ''}»?`}
        description={
          isFetching ? <p className="text-fg4">Проверка ссылок…</p>
            : warnings.length === 0
              ? <p>Ссылки в порядке — будет создана полная копия в выбранном комплекте.</p>
              : <><p className="mb-1.5">При копировании в другой комплект:</p><CopyWarnings warnings={warnings} /></>
        }
        confirmLabel="Скопировать"
        confirmDanger={false}
        onConfirm={handleCopy}
      />
    </>
  );
}

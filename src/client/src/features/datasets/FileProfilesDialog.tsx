import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useSetFileRecognitionProfiles } from '@/shared/api/datasets';
import { useListRecognitionProfiles, useRecognitionKinds } from '@/shared/api/recognitionProfiles';
import type { DataSetFile } from '@/shared/api/types';

/**
 * Профили распознавания НАБОРА (issue #412): штамп, обложка/титул и счёт читают файл целиком, поэтому
 * их профиль задаётся здесь — в отличие от таблиц, где профиль привязывается к группе листов.
 *
 * Какие виды сюда попадают, решает сервер (kindInfo.scope), а не зашитый на клиенте список: иначе
 * добавление вида требовало бы правки фронта.
 */
export function FileProfilesDialog({ file, onClose }: { file: DataSetFile; onClose: () => void }) {
  const { data: kinds = [] } = useRecognitionKinds();
  const { data: profiles = [] } = useListRecognitionProfiles();
  const save = useSetFileRecognitionProfiles(file.id);

  const fileKinds = useMemo(() => kinds.filter(k => k.scope === 'File'), [kinds]);
  const [map, setMap] = useState<Record<string, string>>(() => ({ ...(file.recognitionProfiles ?? {}) }));
  const [error, setError] = useState('');

  const dirty = JSON.stringify(map) !== JSON.stringify(file.recognitionProfiles ?? {});

  async function handleSave() {
    setError('');
    // Отправляем ВСЕ виды: снятые приходят как null, иначе сервер не отличит «не трогали» от «сняли».
    const payload: Record<string, string | null> = {};
    for (const k of fileKinds) payload[k.kind] = map[k.kind] ?? null;
    try {
      await save.mutateAsync(payload);
      onClose();
    } catch (e) {
      const resp = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(resp ?? 'Не удалось сохранить');
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Профили распознавания набора" wide
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} disabled={!dirty} loading={save.isPending}>
            {save.isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <div className="px-6 py-4 space-y-4 min-w-[520px]">
        <p className="text-xs text-fg4">
          Промпты распознавания задаём мы — здесь выбирается, с какими параметрами (набором полей) они
          применяются к этому набору. Не выбрано — используется встроенный профиль.
          Параметры таблиц задаются у групп листов в «Разбиении».
        </p>

        {fileKinds.map(k => {
          const options = profiles.filter(p => p.kind === k.kind);
          return (
            <div key={k.kind}>
              <label className="block text-sm font-medium text-fg1 mb-1">{k.label}</label>
              <select value={map[k.kind] ?? ''}
                onChange={e => setMap(m => {
                  const next = { ...m };
                  if (e.target.value) next[k.kind] = e.target.value; else delete next[k.kind];
                  return next;
                })}
                className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1">
                <option value="">— встроенный профиль</option>
                {options.map(p => (
                  <option key={p.id} value={p.id}>{p.name}{p.isBuiltIn ? ' (встроенный)' : ''}</option>
                ))}
              </select>
            </div>
          );
        })}

        {error && <p className="text-sm text-danger">{error}</p>}
        <p className="text-[11px] text-fg4">
          Смена профиля не перезапускает распознавание — новые параметры применятся при следующем запуске.
        </p>
      </div>
    </Modal>
  );
}

import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useSetFileRecognitionProfiles } from '@/shared/api/datasets';
import {
  useHiddenRecognitionProfiles, useListRecognitionProfiles, useRecognitionKinds,
} from '@/shared/api/recognitionProfiles';
import { hiddenLabel, withBoundOption } from '@/shared/api/recognitionProfileGroups';
import type { DataSetFile } from '@/shared/api/types';
import { Select, SelectItem } from '@/shared/ui/Select';

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
  const { data: hidden = [] } = useHiddenRecognitionProfiles();
  const fileKinds = useMemo(() => kinds.filter(k => k.scope === 'File'), [kinds]);
  const save = useSetFileRecognitionProfiles(file.id);
  // Привязки к профилям выключенных модулей, ВИДА которых на этом экземпляре тоже нет
  // (issue #1075). Без отдельной строки такая привязка была бы невидима: она стоит, действовать
  // начнёт с включением модуля, а окно о ней молчит. Если же вид предлагается (заводской профиль
  // остался за выключенным модулем, а вид теперь объявляет другой), привязка показана в селекте
  // этого вида — см. `withBoundOption` ниже, — и второй строки для неё нет.
  const dormant = Object.entries(file.recognitionProfiles ?? {})
    .filter(([kind]) => !fileKinds.some(k => k.kind === kind))
    .map(([, id]) => hidden.find(h => h.id === id))
    .filter(h => h !== undefined);

  const [map, setMap] = useState<Record<string, string>>(() => ({ ...(file.recognitionProfiles ?? {}) }));
  const [error, setError] = useState('');

  const dirty = JSON.stringify(map) !== JSON.stringify(file.recognitionProfiles ?? {});

  async function handleSave() {
    setError('');
    // Отправляем только ИЗМЕНЁННЫЕ виды; снятые — как null. Вид, которого в теле нет, сервер не
    // трогает. Отправь мы всё, нетронутая привязка к профилю выключенного модуля ушла бы на
    // проверку — и человек получил бы отказ на правку, которой не делал.
    const original = file.recognitionProfiles ?? {};
    const payload: Record<string, string | null> = {};
    for (const k of fileKinds)
      if ((map[k.kind] ?? null) !== (original[k.kind] ?? null)) payload[k.kind] = map[k.kind] ?? null;
    try {
      await save.mutateAsync(payload);
      onClose();
    } catch (e) {
      const resp = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(resp ?? 'Не удалось сохранить');
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Профили распознавания набора"
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} disabled={!dirty} loading={save.isPending}>
            {save.isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <p className="text-body-small text-on-surface-variant">
          Промпты распознавания задаём мы — здесь выбирается, с какими параметрами (набором полей) они
          применяются к этому набору. Не выбрано — используется встроенный профиль.
          Параметры таблиц задаются у групп листов в «Разбиении».
        </p>

        {fileKinds.map(k => {
          const options = withBoundOption(
            profiles.filter(p => p.kind === k.kind)
              .map(p => ({ id: p.id, name: p.isBuiltIn ? `${p.name} (встроенный)` : p.name })),
            map[k.kind], hidden);
          return (
            <Select key={k.kind} label={k.label} value={map[k.kind] ?? ''} emptyItem="— встроенный профиль"
              onValueChange={v => setMap(m => {
                const next = { ...m };
                if (v) next[k.kind] = v; else delete next[k.kind];
                return next;
              })}>
              {options.map(p => <SelectItem key={p.id} value={p.id} disabled={p.disabled}>{p.name}</SelectItem>)}
            </Select>
          );
        })}

        {dormant.map(h => (
          <div key={h.id}>
            <p className="text-body-medium font-medium text-on-surface-variant mb-1">{h.kindLabel}</p>
            {/* Текстом, а не выключенным селектом: длинная подпись в селекте обрезается, и
                обрезается как раз причина — «модуль … выключен». */}
            <p className="border border-outline-variant rounded-sm px-2 py-1.5 text-body-medium bg-surface-container text-on-surface-variant">{hiddenLabel(h)}</p>
            <p className="text-label-small text-on-surface-variant mt-1">
              Привязка сохранена и начнёт действовать, когда модуль включат.
            </p>
          </div>
        ))}

        {error && <p className="text-body-medium text-error">{error}</p>}
        <p className="text-label-small text-on-surface-variant">
          Смена профиля не перезапускает распознавание — новые параметры применятся при следующем запуске.
        </p>
      </div>
    </Modal>
  );
}

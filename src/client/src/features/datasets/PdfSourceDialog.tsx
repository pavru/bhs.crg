import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useCreatePdfSource, useRecognizeFile } from '@/shared/api/datasets';
import { useTagRegistry, datasetTags } from '@/shared/api/tags';
import { usePdfProfiles } from '@/shared/api/recognitionProfiles';

/**
 * Выбор профиля препроцессинга PDF-набора (issue #38/#44). Ставит профиль на НАБОР и сразу запускает
 * распознавание — единым вызовом по fileId для ОБОИХ профилей (backend дискриминирует по
 * DataSetFile.PreprocessingProfile, см. PdfProfileRegistry). Ни один профиль источников не создаёт —
 * оба пишут сырьё на набор (Grouping/InvoiceRawData), кандидаты (Обложка/Титул/Документы или
 * Шапка/Товары) создаёт пользователь. Распознавание больше не прячется в меню источника.
 *
 * Перечень профилей и их тексты приходят с сервера (issue #1075). Зашитый здесь список предлагал
 * ГОСТ и там, где модуль исполнительной документации выключен, — и выбор кончался отказом сервера.
 */
export function PdfSourceDialog(
  { fileId, onClose, onRecognizeError }:
  { fileId: string; onClose: () => void; onRecognizeError?: (err: unknown) => void },
) {
  const [name, setName] = useState('');
  const { data: profiles, isLoading: profilesLoading } = usePdfProfiles();
  // Выбор пользователя; пока он не выбирал — первый из предложенных. Не эффектом: значение
  // выводится из ответа сервера, и хранить его копию незачем.
  const [chosen, setChosen] = useState<string | null>(null);
  const current = profiles?.find(p => p.profile === chosen) ?? profiles?.[0];
  const [tags, setTags] = useState<string[]>([]);
  const [error, setError] = useState('');
  const { data: allTags = [] } = useTagRegistry();
  const create = useCreatePdfSource();
  const recognizeFile = useRecognizeFile();

  function toggleTag(code: string) {
    setTags(prev => prev.includes(code) ? prev.filter(t => t !== code) : [...prev, code]);
  }

  async function handleSave() {
    if (!current) return;
    if (!name.trim()) { setError('Укажите название'); return; }
    setError('');
    try {
      await create.mutateAsync({
        fileId, name: name.trim(), profile: current.profile,
        tags: current.structureTags && tags.length ? tags : null,
      });
      // Профиль выбран → сразу распознаём, единым вызовом по НАБОРУ для любого профиля.
      //
      // Окно закрывается сразу, а отказ всплывает у родителя (issue #801). Ждать здесь нельзя:
      // профиль «Счёт» распознаётся СИНХРОННО, то есть ожидание ответа держало бы модалку открытой
      // весь прогон — минуты на локальной модели. Но и терять отказ, как было раньше (mutate без
      // onError и сразу onClose), нельзя: именно так молчала слепая модель.
      recognizeFile.mutate({ fileId }, { onError: err => onRecognizeError?.(err) });
      onClose();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(msg ?? (e instanceof Error ? e.message : 'Ошибка сохранения'));
    }
  }

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title="Распознать PDF (профиль)"
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} loading={create.isPending}
            disabled={!current}>
            {create.isPending ? 'Создание…' : 'Создать и распознать'}
          </Button>
        </div>
      }>
      <div className="space-y-4 min-w-[420px]">
        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} autoFocus
          hint={current ? `Например: ${current.nameHint}` : undefined} />

        {current && (
          <Select label="Профиль распознавания" value={current.profile} onValueChange={setChosen}>
            {profiles!.map(p => <SelectItem key={p.profile} value={p.profile}>{p.title}</SelectItem>)}
          </Select>
        )}
        {!current && !profilesLoading && (
          <p className="text-sm text-fg3">
            На этом экземпляре нет ни одного профиля для PDF: модули, которые их читают, выключены.
          </p>
        )}

        {current?.structureTags && datasetTags(allTags).length > 0 && (
          <div>
            <p className="text-sm font-medium text-fg1 mb-1">Структура PDF</p>
            <div className="space-y-1.5">
              {datasetTags(allTags).map(t => (
                <label key={t.code} className="flex items-start gap-2 text-sm text-fg2 cursor-pointer">
                  <input type="checkbox" checked={tags.includes(t.code)} onChange={() => toggleTag(t.code)}
                    className="mt-0.5 shrink-0" />
                  <span>
                    {t.label}
                    <span className="block text-xs text-fg4">{t.description}</span>
                  </span>
                </label>
              ))}
            </div>
          </div>
        )}

        {current && <p className="text-xs text-fg4">{current.summary}</p>}

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

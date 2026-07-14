import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useCreatePdfSource, useRecognizeFile } from '@/shared/api/datasets';
import { useTagRegistry, datasetTags } from '@/shared/api/tags';

/**
 * Выбор профиля препроцессинга PDF-набора (issue #38/#44). Ставит профиль на НАБОР и сразу запускает
 * распознавание — единым вызовом по fileId для ОБОИХ профилей (backend дискриминирует по
 * DataSetFile.PreprocessingProfile, см. PdfProfileRegistry). Ни один профиль источников не создаёт —
 * оба пишут сырьё на набор (Grouping/InvoiceRawData), кандидаты (Обложка/Титул/Документы или
 * Шапка/Товары) создаёт пользователь. Распознавание больше не прячется в меню источника.
 */
export function PdfSourceDialog({ fileId, onClose }: { fileId: string; onClose: () => void }) {
  const [name, setName] = useState('');
  const [profile, setProfile] = useState<'gost-titleblock' | 'invoice'>('gost-titleblock');
  const [tags, setTags] = useState<string[]>([]);
  const [error, setError] = useState('');
  const { data: allTags = [] } = useTagRegistry();
  const create = useCreatePdfSource();
  const recognizeFile = useRecognizeFile();

  function toggleTag(code: string) {
    setTags(prev => prev.includes(code) ? prev.filter(t => t !== code) : [...prev, code]);
  }

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    setError('');
    try {
      await create.mutateAsync({
        fileId, name: name.trim(), profile,
        tags: profile === 'gost-titleblock' && tags.length ? tags : null,
      });
      // Профиль выбран → сразу распознаём, единым вызовом по НАБОРУ для любого профиля.
      recognizeFile.mutate({ fileId });
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
          <Button type="button" variant="filled" onClick={handleSave} loading={create.isPending}>
            {create.isPending ? 'Создание…' : 'Создать и распознать'}
          </Button>
        </div>
      }>
      <div className="space-y-4 min-w-[420px]">
        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} autoFocus
          hint={profile === 'invoice' ? 'Счёт на оплату' : 'Реестр листов'} />

        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Профиль распознавания</label>
          <Select value={profile} onValueChange={v => setProfile(v as 'gost-titleblock' | 'invoice')}
            aria-label="Профиль распознавания">
            <SelectItem value="gost-titleblock">Основная надпись (ГОСТ Р 21.101-2020) — реестр по страницам</SelectItem>
            <SelectItem value="invoice">Счёт на оплату — шапка + таблица товаров</SelectItem>
          </Select>
        </div>

        {profile === 'gost-titleblock' && datasetTags(allTags).length > 0 && (
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

        <p className="text-xs text-fg4">
          {profile === 'invoice'
            ? 'Сразу запустится распознавание — оно одним вызовом извлечёт реквизиты счёта и таблицу товаров. Результат появится как кандидаты «Шапка» и «Товары» под списком источников — создайте из них источники в один клик.'
            : 'Сразу запустится распознавание — оно постранично извлечёт основную надпись по ГОСТ Р 21.101-2020 и сгруппирует листы по шифру документа. Результат появится как кандидаты (Документы/Обложка/Титульный лист) под списком источников — создайте из них источники в один клик.'}
        </p>

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

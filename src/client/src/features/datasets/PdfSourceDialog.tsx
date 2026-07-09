import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { useCreatePdfSource, useRecognizePdfSource } from '@/shared/api/datasets';
import { useTagRegistry, datasetTags } from '@/shared/api/tags';

/**
 * Набор-уровневая настройка распознавания PDF (issue #36): выбор профиля + структурных тэгов
 * (scope Dataset). Создаёт первичный источник («Документы»/«шапка») и СРАЗУ запускает распознавание —
 * распознавание больше не прячется в меню источника.
 */
export function PdfSourceDialog({ fileId, onClose }: { fileId: string; onClose: () => void }) {
  const [name, setName] = useState('');
  const [profile, setProfile] = useState<'gost-titleblock' | 'invoice'>('gost-titleblock');
  const [tags, setTags] = useState<string[]>([]);
  const [error, setError] = useState('');
  const { data: allTags = [] } = useTagRegistry();
  const create = useCreatePdfSource();
  const recognize = useRecognizePdfSource();

  function toggleTag(code: string) {
    setTags(prev => prev.includes(code) ? prev.filter(t => t !== code) : [...prev, code]);
  }

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    setError('');
    try {
      const created = await create.mutateAsync({
        fileId, name: name.trim(), profile,
        tags: profile === 'gost-titleblock' && tags.length ? tags : null,
      });
      // Сразу запускаем распознавание (набор-уровневый флоу): профиль + распознать одним действием.
      recognize.mutate({ id: created.id });
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
          <button type="button" onClick={onClose}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-base text-fg2 hover:bg-muted">
            Отмена
          </button>
          <button type="button" onClick={handleSave} disabled={create.isPending}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-brand text-white hover:bg-brand-hover disabled:opacity-50">
            {create.isPending ? 'Создание…' : 'Создать и распознать'}
          </button>
        </div>
      }>
      <div className="space-y-4 min-w-[420px]">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Название</label>
          <input value={name} onChange={e => setName(e.target.value)} autoFocus
            placeholder={profile === 'invoice' ? 'Счёт на оплату' : 'Реестр листов'}
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm" />
        </div>

        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Профиль распознавания</label>
          <select value={profile} onChange={e => setProfile(e.target.value as 'gost-titleblock' | 'invoice')}
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm">
            <option value="gost-titleblock">Основная надпись (ГОСТ Р 21.101-2020) — реестр по страницам</option>
            <option value="invoice">Счёт на оплату — шапка + таблица товаров</option>
          </select>
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
            ? 'После создания запустите распознавание («Распознать») на источнике «шапка» — оно за один вызов извлечёт реквизиты счёта и таблицу товаров, распознаются оба созданных источника вместе.'
            : 'Создаст источник «Документы» (документы группируются по названию, с разрезанием PDF на файл под каждый). После создания запустите распознавание («Распознать») — оно постранично извлечёт основную надпись по ГОСТ Р 21.101-2020. «Обложка» и «Титульный лист» затем можно добавить в один клик из распознанного набора (кандидаты под списком источников).'}
        </p>

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

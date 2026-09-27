import { useRef, useState } from 'react';
import { Upload, Trash2 } from 'lucide-react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import {
  useBranding, useSaveProductName, useUploadLogo, useRemoveLogo, logoUrl,
} from '@/shared/api/branding';

/** Тот же предел, что на сервере (BrandingDefaults.MaxProductNameLength). */
const MAX_NAME = 64;

/**
 * Название продукта и логотип компании (ТЗ CORE-25.1, issue #967).
 *
 * Своя секция, а не поле в «Интеграциях»: там внешние службы, а здесь — как система называется и
 * выглядит на ЭТОМ экземпляре. Видно результат сразу: шапка и заголовок вкладки читают ту же
 * настройку, что правится здесь.
 */
export function BrandingSection() {
  const { data } = useBranding();
  const saveName = useSaveProductName();
  const uploadLogo = useUploadLogo();
  const removeLogo = useRemoveLogo();
  const toast = useToast();
  const fileInput = useRef<HTMLInputElement>(null);

  // Значение формы = правка пользователя, иначе сохранённое. Эффектом не синхронизируем: ввод
  // затирался бы ответом сервера ровно в момент набора.
  const [edited, setEdited] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const name = edited ?? (data?.isCustom ? data.productName : '');
  const logo = logoUrl(data);

  async function submitName() {
    try {
      await saveName.mutateAsync(name.trim() ? name.trim() : null);
      setEdited(null);
      toast.success(name.trim() ? 'Название сохранено.' : 'Название сброшено к общему.');
    } catch (e) {
      toast.apiError(e, 'Не удалось сохранить название.');
    }
  }

  async function pickLogo(file: File | undefined) {
    if (!file) return;
    try {
      await uploadLogo.mutateAsync(file);
      toast.success('Логотип загружен.');
    } catch (e) {
      toast.apiError(e, 'Не удалось загрузить логотип.');
    }
  }

  return (
    <CollapsibleSection title="Название и логотип" storageKey="branding" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Видны в шапке, на странице входа и в заголовке вкладки браузера. Пока название не задано,
        система называет себя общим именем и показывает свой значок.
      </p>

      <div>
        <TextField
          containerClassName="max-w-sm"
          label="Название продукта"
          value={name}
          maxLength={MAX_NAME}
          onChange={e => setEdited(e.target.value)}
          hint={data?.isCustom
            ? 'Пусто — вернуться к общему названию'
            : `Не задано, действует общее: ${data?.productName ?? ''}`}
        />
      </div>

      <div className="flex items-center gap-3">
        <Button variant="filled" onClick={submitName} disabled={saveName.isPending}>Сохранить</Button>
      </div>

      <div className="border-t border-stroke pt-4">
        <p className="text-xs text-fg3 mb-2">
          Логотип — PNG, JPEG, WebP или SVG, до 5 МБ. Он же доступен печатным формам как ассет
          уровня системы: в шаблоне это{' '}
          <code className="text-[11px]">image(&quot;/assets/company-logo.png&quot;)</code>{' '}
          — с тем расширением, с каким файл загружен.
        </p>

        <div className="flex items-center gap-4">
          <div className="flex items-center justify-center w-16 h-16 rounded-lg border border-stroke bg-surface overflow-hidden">
            {logo
              ? <img src={logo} alt="Логотип компании" className="max-w-full max-h-full object-contain" />
              : <span className="text-[11px] text-fg4">нет</span>}
          </div>

          <input
            ref={fileInput}
            type="file"
            accept=".png,.jpg,.jpeg,.webp,.svg"
            className="hidden"
            onChange={e => { void pickLogo(e.target.files?.[0]); e.target.value = ''; }}
          />
          <Button variant="outlined" icon={<Upload size={14} />}
            onClick={() => fileInput.current?.click()} disabled={uploadLogo.isPending}>
            {data?.hasLogo ? 'Заменить' : 'Загрузить'}
          </Button>
          {data?.hasLogo && (
            <Button variant="text" icon={<Trash2 size={14} />}
              onClick={() => setRemoving(true)} disabled={removeLogo.isPending}>
              Убрать
            </Button>
          )}
        </div>
      </div>

      <ConfirmDialog
        open={removing}
        onOpenChange={o => { if (!o) setRemoving(false); }}
        title="Убрать логотип?"
        description="Система вернётся к своему значку. Печатные формы, ставящие логотип, останутся без него."
        confirmLabel="Убрать логотип"
        // Отказ показывает сам диалог (он остаётся открытым и называет причину), поэтому здесь
        // только успех: тост о неудаче дублировал бы уже показанное на месте.
        onConfirm={async () => {
          await removeLogo.mutateAsync();
          setRemoving(false);
          toast.success('Логотип убран.');
        }}
      />
    </CollapsibleSection>
  );
}

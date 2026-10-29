import { useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Check, ContentCopy, Delete, ImageIcon, KeyboardArrowDown, KeyboardArrowUp, Refresh, Title, Upload } from '@/shared/ui/icons';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { IconButton } from '@/shared/ui/Button';
import {
  useListTemplateAssets, useUploadTemplateAsset, useReplaceTemplateAsset, useDeleteTemplateAsset,
  type TemplateAssetDto, type TemplateAssetScope,
} from '@/shared/api/templateAssets';
import { assetReference } from '@/shared/api/templateAssetRef';
import { Tooltip } from '@/shared/ui/Tooltip';

const ACCEPT = '.png,.jpg,.jpeg,.webp,.gif,.svg,.ttf,.otf,.ttc';

function apiError(e: unknown, fallback: string): string {
  const err = e as { response?: { data?: { error?: string } }; message?: string };
  return err?.response?.data?.error || err?.message || fallback;
}

/**
 * Ассет строкой, которой на него ССЫЛАЮТСЯ, а не под которой он хранится (issue #476).
 *
 * Раньше строка давала `name` жирным и `fileName` приглушённым, а то, что пишут в Typst-коде,
 * пользователь собирал в уме из двух кусков. Формы ссылки у картинки и шрифта разные: путь
 * `/assets/Имя.png` против имени семейства — файл шрифта при генерации переименовывается в
 * `font_0.ttf`, и сослаться на него нельзя.
 */
function AssetReference({ asset }: { asset: TemplateAssetDto }) {
  const [copied, setCopied] = useState(false);
  const reference = assetReference(asset);

  if (reference === null) {
    // Семейство не распозналось при загрузке — сослаться на шрифт нечем, и молчать об этом нельзя:
    // «имя ассета» тут не работает, а выглядело бы рабочим.
    return (
      <span className="text-xs truncate flex-1 min-w-0">
        <span className="text-on-surface">{asset.name}</span>
        <span className="text-error"> — семейство не распознано, шрифт не подключится</span>
      </span>
    );
  }

  return (
    <Tooltip content={`${asset.fileName} — скопировать ссылку`}>
      <button type="button"
        onClick={() => {
          void navigator.clipboard?.writeText(reference);
          setCopied(true);
          setTimeout(() => setCopied(false), 1200);
        }}
        className="flex-1 min-w-0 flex items-start gap-1.5 text-left group/ref">
        {/* Переносим, а не обрезаем: панель живёт в узкой колонке, и «/assets/Г…» не отвечает на
            вопрос, ради которого строку и переделали, — что писать в шаблоне. Имя файла ушло в
            подсказку: теперь оно не конкурирует за ширину с тем, на что реально ссылаются. */}
        <code className="text-xs font-mono text-on-surface break-all leading-snug">{reference}</code>
        {copied
          ? <Icon icon={Check} size={18} className="text-success shrink-0 mt-0.5" />
          : <Icon icon={ContentCopy} size={18} className="text-on-surface-variant opacity-0 group-hover/ref:opacity-100 shrink-0 mt-0.5" />}
      </button>
    </Tooltip>
  );
}

// ─── One asset row ──────────────────────────────────────────────────────────────

function AssetRow({ asset }: { asset: TemplateAssetDto }) {
  const replaceMutation = useReplaceTemplateAsset();
  const deleteMutation = useDeleteTemplateAsset();
  const replaceInputRef = useRef<HTMLInputElement>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState('');

  async function handleReplace(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setError('');
    try { await replaceMutation.mutateAsync({ id: asset.id, file }); }
    catch (err) { setError(apiError(err, 'Не удалось заменить файл.')); }
  }

  return (
    <div className="flex items-center gap-2 px-2 py-1.5 rounded hover:bg-surface-container-low group">
      {asset.kind === 'Image'
        ? <Icon icon={ImageIcon} size={18} className="text-primary shrink-0" />
        : <Icon icon={Title} size={18} className="text-tertiary shrink-0" />}
      <AssetReference asset={asset} />
      {error && <span className="text-xs text-error shrink-0">{error}</span>}
      <IconButton label="Заменить файл" size="sm" onClick={() => replaceInputRef.current?.click()}
        disabled={replaceMutation.isPending}
        className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 shrink-0">
        <Icon icon={Refresh} size={18} />
      </IconButton>
      <input ref={replaceInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleReplace} />
      <IconButton label="Удалить" size="sm" danger onClick={() => setConfirmDelete(true)}
        className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 shrink-0">
        <Icon icon={Delete} size={18} />
      </IconButton>
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Удалить ассет «${asset.name}»?`}
        description={
          <p>
            Если этот ассет используется в коде шаблона (image(...)/#set text(font: ...)), генерация
            PDF с ним начнёт падать с ошибкой Typst — сам факт использования здесь не проверяется.
          </p>
        }
        confirmLabel="Удалить"
        onConfirm={() => deleteMutation.mutateAsync(asset.id)}
      />
    </div>
  );
}

// ─── Upload button ──────────────────────────────────────────────────────────────

function UploadButton({ scope, scopeId }: { scope: TemplateAssetScope; scopeId: string | null }) {
  const uploadMutation = useUploadTemplateAsset();
  const inputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState('');

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    const name = window.prompt('Имя ассета (используется в коде шаблона для картинок):', file.name.replace(/\.[^.]+$/, ''));
    if (!name) return;
    setError('');
    try { await uploadMutation.mutateAsync({ file, scope, scopeId, name }); }
    catch (err) { setError(apiError(err, 'Не удалось загрузить файл.')); }
  }

  return (
    <div className="flex items-center gap-2">
      <button type="button" onClick={() => inputRef.current?.click()}
        disabled={uploadMutation.isPending}
        className="flex items-center gap-1 text-xs text-primary hover:text-brand-hover disabled:opacity-50">
        <Icon icon={Upload} size={18} /> Добавить ассет
      </button>
      <input ref={inputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFile} />
      {error && <span className="text-xs text-error">{error}</span>}
    </div>
  );
}

// ─── Panel (переиспользуется на всех трёх уровнях — issue #62) ─────────────────

export interface TemplateAssetsHint {
  scope: TemplateAssetScope;
  scopeId: string | null;
  label: string;
}

export function TemplateAssetsPanel({ scope, scopeId, title, hintScopes }: {
  scope: TemplateAssetScope; scopeId: string | null; title: string; hintScopes?: TemplateAssetsHint[];
}) {
  const [open, setOpen] = useState(false);
  const { data: assets = [] } = useListTemplateAssets(scope, scopeId);

  return (
    <div className="border-t border-outline-variant bg-surface">
      <button onClick={() => setOpen(v => !v)}
        className="w-full flex items-center gap-2 px-4 py-2.5 hover:bg-surface-container-low transition-colors text-left">
        <Icon icon={ImageIcon} size={18} className="text-on-surface-variant" />
        <span className="text-xs font-medium text-on-surface-variant flex-1">
          {title}{assets.length > 0 ? ` (${assets.length})` : ''}
        </span>
        {open ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant" /> : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant" />}
      </button>
      {open && (
        <div className="px-4 pb-4 pt-2 border-t border-surface-container space-y-2">
          {assets.length === 0 ? (
            <p className="text-xs text-on-surface-variant italic">Ассетов нет.</p>
          ) : (
            <div className="space-y-0.5">
              {assets.map(a => <AssetRow key={a.id} asset={a} />)}
            </div>
          )}
          <UploadButton scope={scope} scopeId={scopeId} />
          {hintScopes && hintScopes.length > 0 && <CrossLevelHint hints={hintScopes} />}
        </div>
      )}
    </div>
  );
}

function CrossLevelHint({ hints }: { hints: TemplateAssetsHint[] }) {
  return (
    <>
      {hints.map(h => <HintCount key={`${h.scope}-${h.scopeId}`} hint={h} />)}
    </>
  );
}

function HintCount({ hint }: { hint: TemplateAssetsHint }) {
  const { data: assets = [] } = useListTemplateAssets(hint.scope, hint.scopeId);
  if (assets.length === 0) return null;
  return (
    <p className="text-caption text-on-surface-variant">Также доступно: {assets.length} {hint.label}</p>
  );
}

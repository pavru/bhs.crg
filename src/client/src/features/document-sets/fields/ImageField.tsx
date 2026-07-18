import { useState } from 'react';
import { Image as ImageIcon, Trash2, ChevronDown, ChevronRight } from 'lucide-react';
import type { ImageValue } from '@/shared/api/schema';

/**
 * Поле-изображение. Значение — объект `{ src: data-URI, width?, height?, align?, fit? }` (issue #246):
 * размер/выравнивание задаются здесь, в инстансе (раньше — в определении типа). Легаси-значение
 * (голая data-URI строка) читается как `{ src }` без размера.
 */
export function ImageField({ value, onChange }: {
  value: unknown; onChange: (val: ImageValue | null) => void;
}) {
  const [sizeOpen, setSizeOpen] = useState(false);
  const img = normalize(value);
  const hasSize = !!(img && (img.width || img.height || img.align || img.fit));

  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      if (typeof reader.result === 'string') onChange({ ...(img ?? {}), src: reader.result });
    };
    reader.readAsDataURL(file);
    e.target.value = '';
  }

  const patch = (p: Partial<ImageValue>) => {
    if (!img) return;
    const next: ImageValue = { ...img, ...p };
    // Пустые строки в опциях убираем, чтобы значение не тащило пустышки.
    (['width', 'height', 'align', 'fit'] as const).forEach(k => { if (!next[k]) delete next[k]; });
    onChange(next);
  };

  if (!img) {
    return (
      <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-6 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
        <ImageIcon size={20} className="text-fg4" />
        <span className="text-sm text-fg3">Нажмите для выбора изображения</span>
        <span className="text-xs text-fg4">PNG, JPG, SVG, WEBP</span>
        <input type="file" accept="image/*" className="hidden" onChange={handleFile} />
      </label>
    );
  }

  return (
    <div className="space-y-2">
      <div className="border border-stroke rounded-lg overflow-hidden bg-base flex items-center justify-center p-2 max-h-52">
        <img src={img.src} alt="" className="max-h-48 max-w-full object-contain" />
      </div>

      <div className="flex items-center gap-4">
        <button type="button" onClick={() => onChange(null)}
          className="flex items-center gap-1.5 text-xs text-danger hover:text-danger transition-colors">
          <Trash2 size={12} /> Удалить изображение
        </button>
        <button type="button" onClick={() => setSizeOpen(o => !o)}
          className="flex items-center gap-1 text-xs text-fg3 hover:text-fg1 transition-colors">
          {sizeOpen ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          Размер и выравнивание{!sizeOpen && hasSize ? ' ·' : ''}
        </button>
      </div>

      {sizeOpen && (
        <div className="flex flex-wrap items-center gap-2 pl-1">
          <input value={img.width ?? ''} onChange={e => patch({ width: e.target.value })}
            placeholder="ширина (напр. 4cm)"
            className="w-36 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand" />
          <input value={img.height ?? ''} onChange={e => patch({ height: e.target.value })}
            placeholder="высота"
            className="w-24 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand" />
          <select value={img.align ?? ''} onChange={e => patch({ align: (e.target.value || undefined) as ImageValue['align'] })}
            className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand">
            <option value="">выравнивание</option>
            <option value="left">слева</option>
            <option value="center">по центру</option>
            <option value="right">справа</option>
          </select>
          <select value={img.fit ?? ''} onChange={e => patch({ fit: (e.target.value || undefined) as ImageValue['fit'] })}
            className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand">
            <option value="">fit (вписывание)</option>
            <option value="contain">contain</option>
            <option value="cover">cover</option>
            <option value="stretch">stretch</option>
          </select>
        </div>
      )}
    </div>
  );
}

/** Приводит значение к объекту {src, ...} или null. Понимает легаси-строку data-URI. */
function normalize(value: unknown): ImageValue | null {
  if (typeof value === 'string') return value.startsWith('data:image') ? { src: value } : null;
  if (value && typeof value === 'object') {
    const src = (value as { src?: unknown }).src;
    if (typeof src === 'string' && src.startsWith('data:image')) return value as ImageValue;
  }
  return null;
}

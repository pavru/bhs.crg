import { Image as ImageIcon, Trash2 } from 'lucide-react';

export function ImageField({ value, onChange }: {
  value: unknown; onChange: (val: string | null) => void;
}) {
  const dataUri = typeof value === 'string' && value.startsWith('data:image') ? value : null;

  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => { if (typeof reader.result === 'string') onChange(reader.result); };
    reader.readAsDataURL(file);
    e.target.value = '';
  }

  if (dataUri) {
    return (
      <div className="space-y-2">
        <div className="border border-stroke rounded-lg overflow-hidden bg-base flex items-center justify-center p-2 max-h-52">
          <img src={dataUri} alt="" className="max-h-48 max-w-full object-contain" />
        </div>
        <button type="button" onClick={() => onChange(null)}
          className="flex items-center gap-1.5 text-xs text-danger hover:text-danger transition-colors">
          <Trash2 size={12} /> Удалить изображение
        </button>
      </div>
    );
  }

  return (
    <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-6 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
      <ImageIcon size={20} className="text-fg4" />
      <span className="text-sm text-fg3">Нажмите для выбора изображения</span>
      <span className="text-xs text-fg4">PNG, JPG, SVG, WEBP</span>
      <input type="file" accept="image/*" className="hidden" onChange={handleFile} />
    </label>
  );
}

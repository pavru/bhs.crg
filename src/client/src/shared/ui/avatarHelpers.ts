/**
 * Содержимое аватара: инициалы из имени и уменьшённая картинка. Отдельным модулем от `Avatar.tsx`
 * (issue #858): файл, экспортирующий и компонент, и функции, лишает себя горячей перезагрузки —
 * правка компонента перезагружает страницу целиком вместо подмены на месте.
 */
/** Инициалы для аватара: 2 буквы из имени (или email). */
export function initialsOf(name?: string | null, email?: string | null): string {
  const src = (name || email || '').trim();
  const parts = src.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return src.slice(0, 2).toUpperCase();
}

/**
 * Уменьшает выбранный файл до квадрата ≤ maxPx (обрезка по центру) и возвращает data-URI (JPEG).
 * Держит аватар в несколько КБ — пригодно для хранения строкой в БД.
 */
export function downscaleToDataUri(file: File, maxPx = 256, quality = 0.85): Promise<string> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => {
      try {
        const side = Math.min(image.width, image.height);
        const sx = (image.width - side) / 2;
        const sy = (image.height - side) / 2;
        const target = Math.min(maxPx, side);
        const canvas = document.createElement('canvas');
        canvas.width = target;
        canvas.height = target;
        const ctx = canvas.getContext('2d');
        if (!ctx) { reject(new Error('Не удалось обработать изображение')); return; }
        ctx.drawImage(image, sx, sy, side, side, 0, 0, target, target);
        resolve(canvas.toDataURL('image/jpeg', quality));
      } catch (e) {
        reject(e instanceof Error ? e : new Error('Ошибка обработки изображения'));
      } finally {
        URL.revokeObjectURL(url);
      }
    };
    image.onerror = () => { URL.revokeObjectURL(url); reject(new Error('Не удалось загрузить изображение')); };
    image.src = url;
  });
}

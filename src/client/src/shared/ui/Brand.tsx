import type { CSSProperties } from 'react';
import { useBranding, logoUrl } from '@/shared/api/branding';
import { useImageFailure } from './useImageFailure';

/**
 * Знак экземпляра: логотип компании, если он загружен, иначе общий значок продукта.
 *
 * Логотип вписывается в квадрат знака целиком (`object-contain`): логотипы приходят любых пропорций,
 * и обрезка по центру срезала бы половину надписи — а это ровно то, что заказчик сюда поставил.
 */
export function BrandLogo(
  { className = '', iconSize = 22, style }:
  { className?: string; iconSize?: number; style?: CSSProperties },
) {
  const { data } = useBranding();
  const src = logoUrl(data);
  // Картинка не загрузилась — рисуем общий значок, а не битую рамку с alt-текстом. Признак
  // hasLogo с сервера означает лишь, что строка ассета ЕСТЬ: файл могли убрать мимо системы, а
  // ответ про оформление живёт в клиенте десять минут (ревью PR #1061). Отказ помнится по адресу
  // вместе с меткой значков и недолго — так же, как в рамке настройки: см. `useImageFailure`.
  const [failed, fail] = useImageFailure(src && `${src}|${data?.icons.version ?? ''}`);

  if (src && !failed) {
    return (
      <span className={`flex items-center justify-center shrink-0 overflow-hidden ${className}`} style={style}>
        <img src={src} alt={data?.productName ?? ''} className="max-w-full max-h-full object-contain"
          onError={fail} />
      </span>
    );
  }

  return (
    <span className={`flex items-center justify-center shrink-0 ${className}`} style={style}>
      <ProductMark size={iconSize} />
    </span>
  );
}

/**
 * Знак продукта — документ с галкой. Рисунок свой, вне набора значков shared/ui/icons (решение владельца
 * 28.10.2026, sharedui-md3-03): знак не меняется вместе с набором. Линии — прежний FileCheck2 lucide
 * (ISC), толщина 2 в квадрате 24.
 */
function ProductMark({ size }: { size: number }) {
  return (
    <svg viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth={2}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden focusable="false">
      <path d="M10.5 22H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.706.706l3.588 3.588A2.4 2.4 0 0 1 20 8v6" />
      <path d="M14 2v5a1 1 0 0 0 1 1h5" />
      <path d="m14 20 2 2 4-4" />
    </svg>
  );
}

import type { ReactNode } from 'react';
import { FieldDensityContext } from './fieldBox';
import { InLayerContext } from './layerPlace';

/**
 * Граница слоя — окна или листа (`Modal`, `SideSheet`, `BottomSheet`): внутри — новое место. Размер поля
 * клетки (`FieldDensityContext`) сюда не идёт: контекст React проходит сквозь портал, и окно, открытое
 * из клетки сетки правки, рисовало бы свои поля полями клетки. А `InLayerContext` говорит тем, кто в
 * слое выглядит иначе, что они в слое (поиск списка — 40, а не 56). Одна обёртка на оба — чтобы новый
 * слой не поставил один контекст и не забыл второй.
 */
export function LayerBoundary({ children }: { children: ReactNode }) {
  return (
    <FieldDensityContext.Provider value={null}>
      <InLayerContext.Provider value>{children}</InLayerContext.Provider>
    </FieldDensityContext.Provider>
  );
}

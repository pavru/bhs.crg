import { createContext } from 'react';

/**
 * Внутри окна или листа (`Modal`, `SideSheet`, `BottomSheet`) — `true`. Нужен тем, кто на странице и
 * в слое выглядит по-разному: поле поиска списка на странице — search bar 56, в слое — плотное 40
 * (sharedui-md3-15). `FieldDensityContext` для этого не годится: слои отдают в нём `null`, и страницу от
 * слоя им не отличить.
 */
export const InLayerContext = createContext(false);

import { useContext, type ComponentProps } from 'react';
import { createPortal } from 'react-dom';
import { Banner } from './Banner';
import { ModalBannerContext } from './modalControl';

/**
 * Сообщение окна — banner в начале окна ([UI-73], Р1-А `sharedui-md3-17`): под шапкой, над телом, и не
 * прокручивается с ним. Ставится там, где известно, что сказать, — хоть в глубине тела: окно переносит его на
 * своё место. Тело остаётся обычным: отступы, черта у краёв прокрутки и подвал «Закрыть» — те же, что у любого
 * окна, без `flushBody` и своей раскладки в каждом экране (ревью PR #1760).
 *
 * <p>Пока окно не смонтировало место, не рисуется вовсе: нарисованный в теле и перенесённый следом banner
 * дважды объявлялся бы диктору. Вне окна (и в окне `flushBody`) — обычный `Banner` по месту. Шагу окна
 * (`WindowStep`) не годится: место — у формы окна и под шагом скрыто вместе с ней.</p>
 */
export function ModalBanner(props: ComponentProps<typeof Banner>) {
  const place = useContext(ModalBannerContext);
  if (!place) return <Banner {...props} />;
  return place.host ? createPortal(<Banner {...props} />, place.host) : null;
}

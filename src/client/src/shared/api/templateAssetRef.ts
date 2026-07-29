import type { TemplateAssetDto } from './templateAssets';

/**
 * Как на ассет ССЫЛАЮТСЯ из Typst-кода (issue #476) — в отличие от того, как он хранится.
 *
 * Форма ссылки у картинки и у шрифта разная, и это не косметика:
 * - картинка материализуется в `/assets/{Имя}{расширение}` и адресуется этим путём;
 * - шрифт Typst находит по имени СЕМЕЙСТВА через `--font-path`, а файл на диске переименовывается в
 *   `font_0.ttf` — имя файла не значит ничего.
 *
 * Панель раньше показывала `name` + `fileName`, и то, что пишут в коде, пользователь собирал в уме.
 */

/** Расширение из имени файла, вместе с точкой («.png»); пусто, если расширения нет. */
export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot > 0 ? fileName.slice(dot) : '';
}

/**
 * Путь картинки внутри временной папки генерации — ровно то, что пишут в `image("…")`.
 *
 * ОТ КОРНЯ, с ведущим «/» (issue #513): Typst резолвит путь относительно файла, В КОТОРОМ НАПИСАН
 * вызов, поэтому относительный `assets/…` верен только в файле из корня. Мы эту строку вставляем в
 * редактор, в том числе в файл дерева библиотеки на любой глубине, — там относительная форма молча
 * не нашла бы файл.
 */
export function imageAssetPath(asset: Pick<TemplateAssetDto, 'name' | 'fileName'>): string {
  return `/assets/${asset.name}${extensionOf(asset.fileName)}`;
}

/**
 * Строка, которой на ассет ссылаются. Для шрифта это имя семейства; если оно не распозналось при
 * загрузке, честно возвращаем null — выдать вместо него `name` значило бы предложить строку,
 * которая молча не сработает.
 */
export function assetReference(asset: TemplateAssetDto): string | null {
  return asset.kind === 'Image' ? imageAssetPath(asset) : asset.fontFamilyName;
}

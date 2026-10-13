import { describe, expect, it } from 'vitest';
import type { BrandIcons } from '@/shared/api/branding';
import { elongatedWarning, homeScreenNameHint, iconsFailure } from './brandingIcons';

const built = (aspect: number | null): BrandIcons => ({ state: 'custom', version: 'v', problem: null, aspect });
const failed = (problem: string | null): BrandIcons => ({ state: 'failed', version: 'std', problem, aspect: null });
const standard: BrandIcons = { state: 'standard', version: 'std', problem: null, aspect: null };

describe('предупреждение о вытянутом логотипе', () => {
  it('квадратный и «знак над надписью» предупреждения не дают', () => {
    expect(elongatedWarning(built(1), true)).toBeNull();
    expect(elongatedWarning(built(1.99), true)).toBeNull();
    expect(elongatedWarning(built(0.51), true)).toBeNull();
  });

  // Число точек проверяется глазами по превью рядом — оно обязано сходиться с тем, что строит
  // сервер: логотипу на вкладке отведено 14 точек из 16.
  it.each([[2, 7], [4, 4], [6, 2], [40, 1]])('шириной %d:1 занимает %d из 16 точек по высоте', (aspect, points) => {
    expect(elongatedWarning(built(aspect), true)).toContain(`логотип занимает ${points} из 16 точек по высоте`);
  });

  it('высокий логотип — то же, но «по ширине»', () => {
    expect(elongatedWarning(built(0.25), true)).toContain('занимает 4 из 16 точек по ширине');
  });

  // Заменив надпись знаком при незаданном названии, компания осталась бы вовсе без имени на экране.
  it('название не задано — предупреждение зовёт его задать', () => {
    const tail = 'Название тогда задайте выше — иначе оно нигде не будет написано.';

    expect(elongatedWarning(built(4), false)).toContain(tail);
    expect(elongatedWarning(built(4), true)).not.toContain(tail);
  });

  it('значки не построены или логотипа нет — предупреждать не о чем', () => {
    expect(elongatedWarning(failed('file'), true)).toBeNull();
    expect(elongatedWarning(standard, true)).toBeNull();
    expect(elongatedWarning(built(null), true)).toBeNull();
  });
});

describe('отказ построения значков', () => {
  it('пока отказа нет — слов нет', () => {
    expect(iconsFailure(standard)).toBeNull();
    expect(iconsFailure(built(1))).toBeNull();
  });

  // Совет зависит от причины: при битом файле «загрузите ещё раз» вернуло бы тот же отказ, а при
  // сбое сервера «сохраните в PNG» отправило бы чинить исправный файл.
  it.each([
    ['file', 'Сохраните логотип в PNG и загрузите его.'],
    ['size', 'Картинка слишком велика в точках.'],
    ['builder', 'Причина — сбой на сервере, а не файл.'],
    ['timeout', 'если повторится — сохраните его в PNG и загрузите.'],
    [null, 'если повторится — сохраните его в PNG и загрузите.'],
    ['что-то новое', 'если повторится — сохраните его в PNG и загрузите.'],
  ])('причина %s — свой совет', (problem, advice) => {
    const text = iconsFailure(failed(problem))!;

    expect(text).toContain('Значки из логотипа не построены. Логотип сохранён и виден в шапке');
    expect(text).toContain(advice);
  });

  it('сбой сервера не отправляет чинить файл, а битый файл — грузить его снова', () => {
    expect(iconsFailure(failed('builder'))).not.toContain('PNG');
    expect(iconsFailure(failed('file'))).not.toContain('ещё раз');
  });
});

describe('название под значком приложения', () => {
  it('подсказка появляется после двенадцати знаков', () => {
    expect(homeScreenNameHint('ТКИ.Стройка')).toBeNull();          // 11
    expect(homeScreenNameHint('Двенадцать12')).toBeNull();         // 12
    expect(homeScreenNameHint('Стройка-Монтаж')).toBe('Под значком приложения поместится около 12 знаков.');
    expect(homeScreenNameHint('   Двенадцать12   ')).toBeNull();   // пробелы по краям не в счёт
  });
});

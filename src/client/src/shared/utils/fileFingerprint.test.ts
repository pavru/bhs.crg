import { describe, expect, it } from 'vitest';
// Тот же Web Crypto, что в браузере в защищённом контексте: в Node он есть всегда.
import cases from './fileFingerprintCases.json';
import { PART_BYTES, Sha256, fingerprintOf, ownFingerprint, subtleFingerprint } from './fileFingerprint';

/**
 * Отпечаток браузера обязан побайтно совпасть с серверным (COST-6.7): обе ветки — своя и `crypto.subtle` —
 * сверяются с общим набором примеров C#/TS и с эталонными векторами FIPS 180-4, а своя ещё и на границах
 * частей, по которым она читает файл.
 */

const subtle = globalThis.crypto.subtle;
const blob = (bytes: Uint8Array) => new Blob([bytes as BlobPart]);
const generated = (length: number) => Uint8Array.from({ length }, (_, i) => (i * 31 + 7) % 256);
const ascii = (text: string) => new TextEncoder().encode(text);
const fromHex = (hex: string) => Uint8Array.from(hex.match(/../g) ?? [], b => parseInt(b, 16));

const FIPS: { name: string; bytes: () => Uint8Array; sha256: string }[] = [
  { name: 'пустая строка', bytes: () => new Uint8Array(0), sha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855' },
  { name: '«abc»', bytes: () => ascii('abc'), sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad' },
  { name: '448 бит', bytes: () => ascii('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'), sha256: '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1' },
  { name: 'миллион «a»', bytes: () => new Uint8Array(1_000_000).fill(0x61), sha256: 'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0' },
];

describe('отпечаток файла', () => {
  it.each(FIPS)('эталон FIPS 180-4: $name — обе ветки', async ({ bytes, sha256 }) => {
    expect(await ownFingerprint(blob(bytes()))).toBe(sha256);
    expect(await subtleFingerprint(subtle, blob(bytes()))).toBe(sha256);
  });

  it.each(cases.cases.map(c => [c.name, c] as const))('общий набор примеров C#/TS: %s — обе ветки', async (_, c) => {
    const bytes = 'hex' in c && c.hex ? fromHex(c.hex) : generated(c.length ?? 0);
    expect(await ownFingerprint(blob(bytes))).toBe(c.sha256);
    expect(await subtleFingerprint(subtle, blob(bytes))).toBe(c.sha256);
  });

  it('границы блока 55/56/63/64/65 байт: обе ветки равны друг другу', async () => {
    for (const length of [55, 56, 63, 64, 65, 119, 120, 128]) {
      const b = blob(generated(length));
      expect(await ownFingerprint(b), `длина ${length}`).toBe(await subtleFingerprint(subtle, b));
    }
  });

  it('границы частей: размер части ±1 и части не кратные блоку дают тот же отпечаток', async () => {
    const part = 1000;
    for (const length of [part - 1, part, part + 1, 2 * part + 17]) {
      const b = blob(generated(length));
      const expected = await subtleFingerprint(subtle, b);
      for (const size of [part - 1, part, part + 1, 64, 63])
        expect(await ownFingerprint(b, undefined, size), `длина ${length}, часть ${size}`).toBe(expected);
    }
  }, 30_000);

  it('часть по умолчанию — 4 МБ', () => {
    expect(PART_BYTES).toBe(4 * 1024 * 1024);
  });

  it('поток: update кусками равен update целиком', () => {
    const bytes = generated(1000);
    const whole = new Sha256().update(bytes).digest();
    const parts = new Sha256();
    for (let at = 0; at < bytes.length; at += 37) parts.update(bytes.subarray(at, at + 37));
    expect(parts.digest()).toEqual(whole);
  });

  it('отменённый счёт обрывается отказом, а не неверным отпечатком', async () => {
    const stop = new AbortController();
    stop.abort();
    await expect(ownFingerprint(blob(generated(10)), stop.signal)).rejects.toThrow();
    await expect(subtleFingerprint(subtle, blob(generated(10)), stop.signal)).rejects.toThrow();
  });

  it('вне защищённого контекста fingerprintOf берёт свою ветку и даёт тот же результат', async () => {
    const was = Object.getOwnPropertyDescriptor(globalThis, 'isSecureContext');
    Object.defineProperty(globalThis, 'isSecureContext', { value: false, configurable: true });
    try {
      expect(await fingerprintOf(blob(ascii('abc')))).toBe(FIPS[1].sha256);
    } finally {
      if (was) Object.defineProperty(globalThis, 'isSecureContext', was);
      else delete (globalThis as { isSecureContext?: boolean }).isSecureContext;
    }
  });
});

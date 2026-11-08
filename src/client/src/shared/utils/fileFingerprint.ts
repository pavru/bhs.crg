/**
 * Отпечаток файла — SHA-256 содержимого шестнадцатеричной строкой в нижнем регистре (COST-6.7). По нему
 * ДО выгрузки спрашивают, нет ли уже счёта с тем же файлом (`GET /api/costs/invoices/same-file`), и он
 * обязан побайтно совпасть с тем, что сервер считает при приёме файла: разойдись они, вопрос отвечал бы
 * «двойников нет» про файл, у которого они есть. Совпадение сверяет общий набор примеров C#/TS
 * (`fileFingerprintCases.json`).
 *
 * <p><b>Почему свой SHA-256.</b> `crypto.subtle` браузер даёт только в защищённом контексте — по HTTPS или
 * на localhost, а поставка по умолчанию открыта по `http://…:8080` (issue #848, прецедент — `localId.ts`).
 * Только `crypto.subtle` значил бы, что в типовой установке окно повтора не появится никогда, — отказ,
 * переодетый в результат. Поэтому в защищённом контексте — он, а без него — своя реализация FIPS 180-4 по
 * частям файла, с уступкой циклу событий между частями: экран не замирает на пятидесяти мегабайтах.
 * Зависимости ради одной функции нет: она добавила бы поставщика в цепочку сборки клиента.</p>
 *
 * <p><b>Замер</b> (задача 1.2, 09.11.2026): своя реализация, файл 50 МБ, вкладка по HTTP — см.
 * {@link OWN_SPEED_NOTE}. Меньше секунды — в Web Worker не переносится; больше — переезжает туда без смены
 * формы {@link fingerprintOf}.</p>
 */

/** Часть файла, которая читается за раз своей реализацией. */
export const PART_BYTES = 4 * 1024 * 1024;

/** Число замера (задача 1.2) — в одном месте с кодом, которого оно касается. */
export const OWN_SPEED_NOTE = '50 МБ — 0,41–0,45 с (три прогона; Chrome 149 без окна, i9-11900KF, вкладка по http://192.168.1.34:5187, isSecureContext = false; 09.11.2026)';

/** Посчитать отпечаток — путём, который есть в этом контексте. Отменённый `signal` обрывает счёт между частями. */
export async function fingerprintOf(file: Blob, signal?: AbortSignal): Promise<string> {
  const subtle = globalThis.isSecureContext ? globalThis.crypto?.subtle : undefined;
  return subtle ? subtleFingerprint(subtle, file, signal) : ownFingerprint(file, signal);
}

/** Путь защищённого контекста. Файлы идут по одному, поэтому в памяти один файл (не больше предела, 50 МБ). */
export async function subtleFingerprint(subtle: SubtleCrypto, file: Blob, signal?: AbortSignal): Promise<string> {
  signal?.throwIfAborted();
  const digest = await subtle.digest('SHA-256', await file.arrayBuffer());
  signal?.throwIfAborted();
  return hex(new Uint8Array(digest));
}

/** Свой путь — по частям `part` байт (по умолчанию {@link PART_BYTES}); размер части — для проверки границ. */
export async function ownFingerprint(file: Blob, signal?: AbortSignal, part = PART_BYTES): Promise<string> {
  const hash = new Sha256();
  for (let at = 0; at < file.size; at += part) {
    if (at > 0) await yieldToLoop();
    signal?.throwIfAborted();
    hash.update(new Uint8Array(await file.slice(at, at + part).arrayBuffer()));
  }
  signal?.throwIfAborted();
  return hex(hash.digest());
}

/** Отдать ход циклу событий: отрисовка и ввод не ждут, пока досчитается весь файл. */
const yieldToLoop = () => new Promise<void>(resolve => setTimeout(resolve, 0));

const hex = (bytes: Uint8Array) => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');

/** Константы раундов — первые 32 бита дробных частей кубических корней первых 64 простых (FIPS 180-4, 4.2.2). */
const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/** SHA-256 потоком (FIPS 180-4): `update` сколько угодно раз, `digest` — один. */
export class Sha256 {
  private readonly h = new Uint32Array([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ]);
  private readonly w = new Uint32Array(64);
  /** Недобранный блок: байты, не кратные 64, ждут следующего `update`. */
  private readonly tail = new Uint8Array(64);
  private filled = 0;
  /** Длина в байтах: числом JS точно до 2^53 — с запасом больше любого файла здесь. */
  private length = 0;

  update(data: Uint8Array): this {
    this.length += data.length;
    let at = 0;
    if (this.filled > 0) {
      const take = Math.min(64 - this.filled, data.length);
      this.tail.set(data.subarray(0, take), this.filled);
      this.filled += take;
      at = take;
      if (this.filled < 64) return this;
      this.block(this.tail, 0);
      this.filled = 0;
    }
    for (; at + 64 <= data.length; at += 64) this.block(data, at);
    if (at < data.length) {
      this.tail.set(data.subarray(at), 0);
      this.filled = data.length - at;
    }
    return this;
  }

  digest(): Uint8Array {
    const bits = this.length * 8;
    // Дополнение: единичный бит, нули до 56 байт по модулю 64, длина в битах 64-битным big-endian.
    const pad = new Uint8Array((this.filled < 56 ? 56 : 120) - this.filled + 8);
    pad[0] = 0x80;
    const view = new DataView(pad.buffer);
    view.setUint32(pad.length - 8, Math.floor(bits / 2 ** 32));
    view.setUint32(pad.length - 4, bits >>> 0);
    this.length -= pad.length; // дополнение в длину сообщения не входит
    this.update(pad);
    const out = new Uint8Array(32);
    const outView = new DataView(out.buffer);
    this.h.forEach((v, i) => outView.setUint32(i * 4, v));
    return out;
  }

  private block(data: Uint8Array, at: number) {
    const w = this.w;
    for (let i = 0; i < 16; i++) {
      const j = at + i * 4;
      w[i] = (data[j] << 24) | (data[j + 1] << 16) | (data[j + 2] << 8) | data[j + 3];
    }
    for (let i = 16; i < 64; i++) {
      const a = w[i - 15], b = w[i - 2];
      const s0 = rotr(a, 7) ^ rotr(a, 18) ^ (a >>> 3);
      const s1 = rotr(b, 17) ^ rotr(b, 19) ^ (b >>> 10);
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
    }
    const h = this.h;
    let a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], k = h[7];
    for (let i = 0; i < 64; i++) {
      const t1 = (k + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i]) | 0;
      const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0;
      k = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += k;
  }
}

const rotr = (x: number, n: number) => (x >>> n) | (x << (32 - n));

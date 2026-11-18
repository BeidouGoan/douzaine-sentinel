/**
 * generate_icons.mjs — 「斗在吗」App 图标位图生成器
 * ============================================================
 * 为什么需要它：
 *   自适应图标（mipmap-anydpi-v26）只覆盖 API 26+。
 *   本工程 minSdk = 24，API 24/25 的桌面读的是传统 PNG 位图，
 *   所以必须补一套各密度回退图，否则老系统上图标是空白的。
 *
 * 为什么不用设计软件导出：
 *   矢量前景（ic_launcher_foreground.xml）是单一真源。
 *   用 SDF 光栅化从同一套几何参数生成 PNG，保证位图与矢量完全一致，
 *   以后改形状只需改一处参数、重跑本脚本。
 *
 * 用法：
 *   node tools/generate_icons.mjs
 *
 * 输出：
 *   src/main/res/mipmap-{m,h,xh,xxh,xxxh}dpi/ic_launcher.png
 *   src/main/res/mipmap-{m,h,xh,xxh,xxxh}dpi/ic_launcher_round.png
 */

import { deflateSync } from 'node:zlib';
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const RES = join(ROOT, 'src', 'main', 'res');

// ------------------------------------------------------------------ //
// 几何参数（单位：108，与 ic_launcher_foreground.xml 完全一致）
// ------------------------------------------------------------------ //

const BRAND = [0x2f, 0x6f, 0xe0];   // #2F6FE0
const WHITE = [0xff, 0xff, 0xff];

const CX = 54, CY = 58;
const OUTER_R = 23, OUTER_W = 6, OUTER_A = 0.95;
const INNER_R = 16, INNER_W = 4.5, INNER_A = 0.5;
const PUPIL_R = 9;

const CORNER = 26;                 // 圆角半径

// 雷达弧覆盖上半周（屏幕坐标 y 向下 ⇒ 上半周对应 atan2 角度 [-π, 0]）
// a0 = 左端点 (cx-R, cy)，a1 = 右端点 (cx+R, cy)
const ARC_A0 = -Math.PI;
const ARC_A1 = 0;

// ------------------------------------------------------------------ //
// 有向距离场（SDF）
// ------------------------------------------------------------------ //

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);

/** 圆角矩形 SDF：负值在内部 */
function sdRoundRect(px, py, halfW, halfH, r) {
  const qx = Math.abs(px) - halfW + r;
  const qy = Math.abs(py) - halfH + r;
  const ax = Math.max(qx, 0), ay = Math.max(qy, 0);
  return Math.hypot(ax, ay) + Math.min(Math.max(qx, qy), 0) - r;
}

/** 圆 SDF：负值在内部 */
function sdCircle(px, py, cx, cy, r) {
  return Math.hypot(px - cx, py - cy) - r;
}

/**
 * 圆弧（带圆头端点）SDF：负值在描边内
 *
 * ★ 角度约定：屏幕坐标 y 向下，atan2(dy,dx) 增大 = 顺时针。
 *   所以「上半周圆弧」= 角度区间 [a0, a1]，其中 a0 < a1 且 a1-a0 ≤ π。
 *   这里不做 atan2 的 [0,2π) 归一化，改用「相对 a0 的偏移量」，
 *   否则跨越 ±π 分支时会算反。
 */
function sdArc(px, py, cx, cy, R, halfWidth, a0, a1) {
  const dx = px - cx, dy = py - cy;
  const r = Math.hypot(dx, dy);

  let rel = Math.atan2(dy, dx) - a0;
  while (rel < 0) rel += Math.PI * 2;
  while (rel >= Math.PI * 2) rel -= Math.PI * 2;

  const span = a1 - a0;
  if (rel <= span) return Math.abs(r - R) - halfWidth;

  // 落在角度区间外：取最近端点（圆头端点 = 端点处再减去半宽）
  const ex0 = cx + R * Math.cos(a0), ey0 = cy + R * Math.sin(a0);
  const ex1 = cx + R * Math.cos(a1), ey1 = cy + R * Math.sin(a1);
  const d0 = Math.hypot(px - ex0, py - ey0);
  const d1 = Math.hypot(px - ex1, py - ey1);
  return Math.min(d0, d1) - halfWidth;
}

/** 覆盖率：距离转 0..1 的抗锯齿权重 */
function coverage(d) {
  return clamp(0.5 - d, 0, 1);
}

/** 前景形状的不透明度（白） */
function foregroundAlpha(u, v) {
  // u,v 为 0..1 的画板坐标 → 108 单位
  const x = u * 108, y = v * 108;

  const aOuter = coverage(sdArc(x, y, CX, CY, OUTER_R, OUTER_W / 2, ARC_A0, ARC_A1)) * OUTER_A;
  const aInner = coverage(sdArc(x, y, CX, CY, INNER_R, INNER_W / 2, ARC_A0, ARC_A1)) * INNER_A;
  const aPupil = coverage(sdCircle(x, y, CX, CY, PUPIL_R));

  // 白色前景按 alpha 叠加（两层弧半透明叠加时用 source-over）
  // 先算白色自身的覆盖率，再叠到底色上
  const white = aOuter + (1 - aOuter) * (aInner + (1 - aInner) * aPupil);
  return clamp(white, 0, 1);
}

// ------------------------------------------------------------------ //
// PNG 编码（最小实现：真彩色 + alpha，8bit）
// ------------------------------------------------------------------ //

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

function encodePng(width, height, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;    // bit depth
  ihdr[9] = 6;    // color type: RGBA
  ihdr[10] = 0;   // compression
  ihdr[11] = 0;   // filter
  ihdr[12] = 0;   // interlace

  // 每行前置 filter byte 0
  const raw = Buffer.alloc(height * (width * 4 + 1));
  for (let y = 0; y < height; y++) {
    const rowStart = y * (width * 4 + 1);
    raw[rowStart] = 0;
    rgba.copy(raw, rowStart + 1, y * width * 4, (y + 1) * width * 4);
  }

  return Buffer.concat([
    sig,
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ------------------------------------------------------------------ //
// 渲染
// ------------------------------------------------------------------ //

/**
 * @param {number} size    输出像素尺寸
 * @param {boolean} round  true = 圆形图标，false = 圆角方形
 */
function render(size, round) {
  const rgba = Buffer.alloc(size * size * 4);
  const SS = 3;                       // 3×3 超采样
  const scale = 108 / size;            // 每个像素对应多少 108 单位

  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let bgHits = 0;
      let fgSum = 0;

      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const u = (px + (sx + 0.5) / SS) / size;
          const v = (py + (sy + 0.5) / SS) / size;
          const x = u * 108, y = v * 108;

          // 背景形状（SDF 以画布中心为原点，所以要减去 54）
          let bgD;
          if (round) {
            bgD = sdCircle(x - 54, y - 54, 0, 0, 54);
          } else {
            bgD = sdRoundRect(x - 54, y - 54, 54, 54, CORNER);
          }
          if (bgD <= 0) bgHits++;

          fgSum += foregroundAlpha(u, v);
        }
      }

      const samples = SS * SS;
      const bgA = bgHits / samples;
      const fgA = fgSum / samples;

      const i = (py * size + px) * 4;

      if (bgA <= 0) {
        // 背景之外：前景若延伸出来也要画（这里前景全在内部，理论上不会）
        rgba[i] = WHITE[0]; rgba[i + 1] = WHITE[1]; rgba[i + 2] = WHITE[2];
        rgba[i + 3] = Math.round(fgA * 255);
        continue;
      }

      // 白色前景以 alpha 合成到底色上
      const a = fgA;
      rgba[i] = Math.round(BRAND[0] * (1 - a) + WHITE[0] * a);
      rgba[i + 1] = Math.round(BRAND[1] * (1 - a) + WHITE[1] * a);
      rgba[i + 2] = Math.round(BRAND[2] * (1 - a) + WHITE[2] * a);
      rgba[i + 3] = Math.round(bgA * 255);
    }
  }

  return encodePng(size, size, rgba);
}

// ------------------------------------------------------------------ //
// 输出
// ------------------------------------------------------------------ //

const DENSITIES = [
  ['mdpi', 48],
  ['hdpi', 72],
  ['xhdpi', 96],
  ['xxhdpi', 144],
  ['xxxhdpi', 192],
];

for (const [dpi, size] of DENSITIES) {
  const dir = join(RES, `mipmap-${dpi}`);
  mkdirSync(dir, { recursive: true });

  writeFileSync(join(dir, 'ic_launcher.png'), render(size, false));
  writeFileSync(join(dir, 'ic_launcher_round.png'), render(size, true));

  console.log(`mipmap-${dpi}: ${size}×${size}  ✓`);
}

// 预览图（大尺寸，便于肉眼检查）
const previewDir = join(ROOT, 'docs', 'img');
mkdirSync(previewDir, { recursive: true });
writeFileSync(join(previewDir, 'icon_preview.png'), render(512, false));
console.log('preview → docs/img/icon_preview.png');

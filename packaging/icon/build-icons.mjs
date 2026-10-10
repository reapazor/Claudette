// Draws Claudette's icon (DESIGN.md §2, "Packaging and signing"): Clawd, Claude Code's mascot, with hair on top, a
// hair tie and a ponytail. Every size is drawn from the sprite below with a whole number of pixels per cell, so the
// small sizes stay crisp. Writes:
//   packaging/icon/claudette.svg              the icon as vectors, also Rider's project icon (.idea/.idea.Claudette/.idea/icon.svg)
//   packaging/icon/claudette-1024.png         the macOS icon, on an ivory tile; build-dmg.sh scales it
//   src/Claudette.App/Assets/claudette.ico    the window and executable icon
//   packaging/windows/Assets/*.png            the MSIX logos, and the taskbar's unplated sizes
//   src/Claudette.App/Assets/AppIcon/*.png    the animations' frames (DESIGN.md §10): the taskbar overlay's pulsing
//                                             spark and running hourglass, and the Dock icon typing, waving and
//                                             waiting by an hourglass (AppIconAnimations)
//   src/Claudette.App/Assets/Mascot/mascot.json  Claudette on the composer's poses and props, as cells (DESIGN.md §5)
// Run from anywhere: node packaging/icon/build-icons.mjs

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateSync } from 'node:zlib';

const root = join(dirname(fileURLToPath(import.meta.url)), '../..');

// 12 cells square: 4 rows of hair over Clawd's 8-row body, with the ponytail's tail beside the head.
const hair = [
  '........HHH.',
  '.......HHHHH',
  '...HHHHKH.HH',
  '..HHHHHHH.HH',
];
const legs = ['..O.O..O.O..', '..O.O..O.O..'];
const pose = {
  stand: ['..OOOOOOOO..', '..OEOOOOEO..', 'OOOOOOOOOOOO', 'OOOOOOOOOOOO', '..OOOOOOOO..', '..OOOOOOOO..', ...legs],
  // Typing: the arms bob in turn.
  typeLeft: ['..OOOOOOOO..', 'OOOEOOOOEO..', 'OOOOOOOOOO..', '..OOOOOOOOOO', '..OOOOOOOOOO', '..OOOOOOOO..', ...legs],
  typeRight: ['..OOOOOOOO..', '..OEOOOOEOOO', '..OOOOOOOOOO', 'OOOOOOOOOO..', 'OOOOOOOOOO..', '..OOOOOOOO..', ...legs],
  // Waving: the left arm up beside the head.
  wave: ['OOOOOOOOOO..', 'OOOEOOOOEO..', '..OOOOOOOOOO', '..OOOOOOOOOO', '..OOOOOOOO..', '..OOOOOOOO..', ...legs],
};
const claudette = body => [...hair, body[0].slice(0, 11) + 'H', ...body.slice(1)];
const sprite = claudette(pose.stand);

const palette = {
  O: '#D77757', // Claude Code's "claude" colour
  E: '#141413',
  H: '#A8492F',
  K: '#B8336A',
};
const tile = '#F0EEE6'; // Claude's ivory, behind the macOS icon
const cells = sprite.length;

// Claude's spark for the taskbar overlay, in sizes to pulse through: 2 px a cell, in Claude's ivory, so it reads over
// Claudette's orange.
const spark = [
  ['.X.', 'XXX', '.X.'],
  ['X.X.X', '.XXX.', 'XXXXX', '.XXX.', 'X.X.X'],
  ['X..X..X', '.X.X.X.', '..XXX..', 'XXXXXXX', '..XXX..', '.X.X.X.', 'X..X..X'],
  ['X...X...X', '.X..X..X.', '..X.X.X..', '...XXX...', 'XXXXXXXXX', '...XXX...', '..X.X.X..', '.X..X..X.', 'X...X...X'],
];
const sparkColors = { X: tile };

// An hourglass, while a tab waits for a usage limit to reset: X its frame, E the empty glass, S the sand. A dark frame
// and ivory glass, so it reads on a light or dark taskbar, and Claude's orange sand.
const hourglass = [
  'XXXXXXXXX',
  '.XEEEEEX.',
  '.XEEEEEX.',
  '.XEEEEEX.',
  '..XEEEX..',
  '...XEX...',
  '..XEEEX..',
  '.XEEEEEX.',
  '.XEEEEEX.',
  '.XEEEEEX.',
  'XXXXXXXXX',
];
const hourglassColors = { X: palette.E, E: tile, S: palette.O };

/**
 * The hourglass with sand in the lowest `top` rows of the top bulb and the lowest `bottom` rows of the bottom one, and
 * running through the neck between them while both have some.
 */
function sand(top, bottom) {
  const neck = hourglass.length >> 1;
  const middle = hourglass[0].length >> 1;
  const last = hourglass.length - 1;
  return hourglass.map((row, y) => [...row].map((ch, x) => {
    if (ch !== 'E') return ch;
    const inTop = y < neck && y >= neck - top;
    const inBottom = y > neck && y >= last - bottom;
    const running = top > 0 && bottom > 0 && x === middle && y >= neck;
    return inTop || inBottom || running ? 'S' : 'E';
  }).join(''));
}

/** Rows turned a quarter clockwise. */
const turn = rows => [...rows[0]].map((_, x) => rows.map((_, y) => rows[rows.length - 1 - y][x]).join(''));

// The sand runs down, rests, and the hourglass turns over: once sideways, then full at the top again.
const sandFrames = [sand(4, 0), sand(3, 1), sand(2, 2), sand(1, 3), sand(0, 4)];
sandFrames.push(turn(sandFrames[4]));

// ── Claudette on the composer (DESIGN.md §5) ─────────────────────────────
// Her poses, from the same sprite, and the small things she has with her. The app draws them as cells, so they're
// written as rows rather than images. Each pose's last row is her feet, on the composer's top edge.

/** Rows with the cells of `patch` written over them, `patch` being { row: 'cells' } with '?' leaving a cell as it was. */
const patched = (rows, patch) => rows.map((row, y) => patch[y] === undefined ? row
  : [...row].map((ch, x) => (patch[y][x] ?? '?') === '?' ? ch : patch[y][x]).join(''));

const standing = claudette(pose.stand);
const closedEyes = rows => patched(rows, { 5: '..OOOOOOOO..' });
const walkLegs = [
  { 11: '....O....O..' }, // the first and third legs up
  {},
  { 11: '..O....O....' }, // the second and fourth
  {},
];
// Arms up; the right arm covers the ponytail's end.
const armsUp = { 4: 'OOOOOOOOOOOO', 5: 'OOOEOOOOEOOO', 6: '..OOOOOOOO..', 7: '..OOOOOOOO..' };
// Leaning on the edge: she stands two cells lower, behind it, with her arms on top.
const leaning = patched(standing, { 6: '..OOOOOOOO..', 7: '..OOOOOOOO..', 8: 'OOOOOOOOOOOO', 9: 'OOOOOOOOOOOO' });
// Hanging from the edge by her hands, from behind it: two rows taller, her arms up past her hair.
const hanging = [
  'OO........OO',
  'OO........OO',
  ...patched(hair, { 0: 'OO????????OO', 1: 'OO????????OO', 2: 'OO????????OO', 3: 'OO????????OO' }),
  'OOOOOOOOOOOO',
  '..OEOOOOEO..',
  '..OOOOOOOO..',
  '..OOOOOOOO..',
  '..OOOOOOOO..',
  '..OOOOOOOO..',
  ...legs,
];
const lookLeft = { 5: '..EOOOOEOO..' };
const lookRight = { 5: '..OOEOOOOE..' };

const mascotPoses = {
  stand: standing,
  blink: closedEyes(standing),
  lookLeft: patched(standing, lookLeft),
  lookRight: patched(standing, lookRight),
  // Looking down at the edge, before she topples off it.
  lookDown: patched(closedEyes(standing), { 6: 'OOOEOOOOEOOO' }),
  ...Object.fromEntries(walkLegs.flatMap((step, i) => [
    [`walkLeft-${i}`, patched(standing, { ...lookLeft, ...step })],
    [`walkRight-${i}`, patched(standing, { ...lookRight, ...step })],
  ])),
  wave: claudette(pose.wave),
  // The waving arm a row higher, beside her hair.
  waveHigh: patched(claudette(pose.wave), { 3: 'OO?????????', 5: '..OEOOOOEO..' }),
  armsUp: patched(standing, armsUp),
  stretch: patched(standing, { ...armsUp, 5: 'OOOOOOOOOOOO' }),
  typeLeft: claudette(pose.typeLeft),
  typeRight: claudette(pose.typeRight),
  lean: leaning,
  leanBlink: closedEyes(leaning),
  hang: hanging,
  hangLookLeft: patched(hanging, { 7: '..EOOOOEOO..' }),
  hangLookRight: patched(hanging, { 7: '..OOEOOOOE..' }),
};

// What she has with her. Z takes the theme's muted text colour, so it reads on light and dark.
const mascotProps = {
  // Asleep: a small z, then a bigger one higher up.
  z: ['ZZZZ', '..Z.', '.Z..', 'ZZZZ'],
  bigZ: ['ZZZZZ', '...Z.', '..Z..', '.Z...', 'ZZZZZ'],
  // Startled.
  bang: ['ZZ', 'ZZ', 'ZZ', '..', 'ZZ'],
  // The back of her laptop's lid, in front of her while she types.
  laptop: ['.LLLLLLLL.', '.LLLLLLLL.', '.LLLLLLLL.', 'BBBBBBBBBB'],
  // While a usage limit holds the task: a small hourglass whose sand runs down.
  'hourglass-0': ['XXXXX', '.SSS.', '..S..', '.GGG.', 'XXXXX'],
  'hourglass-1': ['XXXXX', '.GSG.', '..S..', '.GSG.', 'XXXXX'],
  'hourglass-2': ['XXXXX', '.GGG.', '..G..', '.SSS.', 'XXXXX'],
};

const mascotPalette = {
  ...palette,
  L: '#8F8D86', // the lid, a warm grey that reads on light and dark
  B: '#5F5E59',
  X: '#8F8D86',
  G: tile,
  S: palette.O,
};

function mascotJson() {
  const rows = frames => Object.entries(frames).map(([name, cells]) => `    ${JSON.stringify(name)}: ${JSON.stringify(cells)}`).join(',\n');
  return [
    '{',
    '  "_comment": "Drawn by packaging/icon/build-icons.mjs; change the poses there. Z is the theme\'s muted text colour.",',
    `  "palette": ${JSON.stringify(mascotPalette)},`,
    '  "poses": {',
    rows(mascotPoses),
    '  },',
    '  "props": {',
    rows(mascotProps),
    '  }',
    '}',
    '',
  ].join('\n');
}

// Pixels per cell at each size. At 24, 36 and 48, the taskbar at 100%, 150% and 200%, she fills the icon.
const scale = { 16: 1, 20: 1, 24: 2, 30: 2, 32: 2, 36: 3, 40: 3, 44: 3, 48: 4, 50: 3, 64: 5, 128: 9, 150: 8, 256: 18 };

// ── Drawing ──────────────────────────────────────────────────────────────
const rgb = hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));

/** A transparent RGBA image. */
const image = (width, height) => ({ width, height, data: Buffer.alloc(width * height * 4) });

/** A sprite at `k` pixels per cell, its top-left corner at (x, y). */
function drawSprite(img, k, x, y, rows = sprite, colors = palette) {
  rows.forEach((row, cy) => [...row].forEach((ch, cx) => {
    if (ch === '.') return;
    const pixel = [...rgb(colors[ch]), 255];
    for (let py = y + cy * k; py < y + (cy + 1) * k; py++) {
      for (let px = x + cx * k; px < x + (cx + 1) * k; px++) {
        img.data.set(pixel, (py * img.width + px) * 4);
      }
    }
  }));
}

/** A rounded square, its edges antialiased from 4×4 samples a pixel. */
function drawTile(img, x, y, size, radius, hex) {
  const [r, g, b] = rgb(hex);
  const n = 4;
  for (let py = y; py < y + size; py++) {
    for (let px = x; px < x + size; px++) {
      let inside = 0;
      for (let sy = 0; sy < n; sy++) {
        for (let sx = 0; sx < n; sx++) {
          const fx = px - x + (sx + 0.5) / n;
          const fy = py - y + (sy + 0.5) / n;
          const cx = Math.min(Math.max(fx, radius), size - radius);
          const cy = Math.min(Math.max(fy, radius), size - radius);
          if ((fx - cx) ** 2 + (fy - cy) ** 2 <= radius * radius) inside++;
        }
      }
      if (inside > 0) img.data.set([r, g, b, Math.round((255 * inside) / (n * n))], (py * img.width + px) * 4);
    }
  }
}

/** The sprite centred in a transparent `width`×`height` image. */
function icon(width, height = width) {
  const k = scale[height];
  const img = image(width, height);
  drawSprite(img, k, Math.floor((width - cells * k) / 2), Math.floor((height - cells * k) / 2));
  return img;
}

/**
 * The macOS icon, or a frame of the Dock's animation: Apple's grid, an 824/1024 rounded square with the sprite at 58%
 * of its width. The frames are drawn at 512, where the sprite is exactly half its size in the 1024 icon, so the Dock
 * icon doesn't shift when an animation starts or stops.
 */
function macIcon(size = 1024, rows = sprite) {
  const img = image(size, size);
  const unit = size / 1024;
  drawTile(img, 100 * unit, 100 * unit, 824 * unit, 185 * unit, tile);
  const k = 40 * unit;
  const at = size / 2 - (cells * k) / 2;
  drawSprite(img, k, at, at, rows);
  return img;
}

/** A frame of the taskbar overlay's spark: 32 px, as Windows wants overlays at twice their 16 px. */
function sparkIcon(rows) {
  const img = image(32, 32);
  const at = 16 - rows.length;
  drawSprite(img, 2, at, at, rows, sparkColors);
  return img;
}

/**
 * A frame of the taskbar overlay's hourglass: 32 px, 2 px a cell, as near the middle as an even pixel goes, so its cells
 * stay whole when the taskbar halves it at 100%.
 */
function hourglassIcon(rows) {
  const img = image(32, 32);
  const even = cells => 2 * Math.floor((16 - cells) / 2);
  drawSprite(img, 2, even(rows[0].length), even(rows.length), rows, hourglassColors);
  return img;
}

/**
 * A frame of the Dock's waiting animation: Claudette standing, and the hourglass on the tile's bottom-right corner,
 * like a badge, its cells three-fifths of hers so it reads at Dock sizes.
 */
function waitingIcon(rows) {
  const size = 512;
  const img = macIcon(size);
  const unit = size / 1024;
  const k = 24 * unit;
  const centre = 808 * unit;
  drawSprite(img, k, centre - (rows[0].length * k) / 2, centre - (rows.length * k) / 2, rows, hourglassColors);
  return img;
}

// ── Files ────────────────────────────────────────────────────────────────
function png({ width, height, data }) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height); // each row starts with filter type 0, none
  for (let y = 0; y < height; y++) data.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  const chunk = (type, body) => {
    const out = Buffer.alloc(12 + body.length);
    out.writeUInt32BE(body.length, 0);
    out.write(type, 4, 'ascii');
    body.copy(out, 8);
    out.writeUInt32BE(crc32(out.subarray(4, 8 + body.length)), 8 + body.length);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bits per channel
  header[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** An .ico holding each image as a PNG, which Windows has read since Vista. */
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(1, 2); // an icon, not a cursor
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  const bodies = images.map((img, i) => {
    const body = png(img);
    const entry = 6 + 16 * i;
    header[entry] = img.width % 256; // 0 means 256
    header[entry + 1] = img.height % 256;
    header.writeUInt16LE(1, entry + 4); // colour planes
    header.writeUInt16LE(32, entry + 6); // bits per pixel
    header.writeUInt32LE(body.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += body.length;
    return body;
  });
  return Buffer.concat([header, ...bodies]);
}

function svg() {
  const byColor = new Map();
  sprite.forEach((row, y) => {
    for (let x = 0; x < row.length; ) {
      let end = x + 1;
      while (end < row.length && row[end] === row[x]) end++;
      if (row[x] !== '.') {
        const fill = palette[row[x]];
        byColor.set(fill, [...(byColor.get(fill) ?? []), `<rect x="${x}" y="${y}" width="${end - x}" height="1"/>`]);
      }
      x = end;
    }
  });
  const groups = [...byColor].map(([fill, rects]) => `  <g fill="${fill}">${rects.join('')}</g>`);
  return [
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${cells} ${cells}" shape-rendering="crispEdges">`,
    '  <!-- Drawn by packaging/icon/build-icons.mjs; change the sprite there. -->',
    ...groups,
    '</svg>',
    '',
  ].join('\n');
}

function write(path, content) {
  const full = join(root, path);
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, content);
  console.log(`${path} (${content.length} bytes)`);
}

write('packaging/icon/claudette.svg', svg());
write('.idea/.idea.Claudette/.idea/icon.svg', svg());
write('packaging/icon/claudette-1024.png', png(macIcon()));
write('src/Claudette.App/Assets/claudette.ico', ico([16, 20, 24, 32, 40, 48, 64, 128, 256].map(size => icon(size))));

const assets = 'packaging/windows/Assets';
for (const size of [16, 20, 24, 30, 32, 36, 40, 48, 64, 256]) {
  write(`${assets}/Square44x44Logo.targetsize-${size}_altform-unplated.png`, png(icon(size)));
}
write(`${assets}/Square44x44Logo.png`, png(icon(44)));
write(`${assets}/StoreLogo.png`, png(icon(50)));
write(`${assets}/Square150x150Logo.png`, png(icon(150)));
write(`${assets}/Wide310x150Logo.png`, png(icon(310, 150)));

// The animations' frames, in the order AppIconAnimations shows them.
const frames = 'src/Claudette.App/Assets/AppIcon';
[spark[0], spark[1], spark[2], spark[3], spark[2], spark[1]].forEach((rows, i) => write(`${frames}/spark-${i}.png`, png(sparkIcon(rows))));
[pose.typeLeft, pose.typeRight].forEach((body, i) => write(`${frames}/typing-${i}.png`, png(macIcon(512, claudette(body)))));
[pose.wave, pose.stand].forEach((body, i) => write(`${frames}/waving-${i}.png`, png(macIcon(512, claudette(body)))));
sandFrames.forEach((rows, i) => write(`${frames}/hourglass-${i}.png`, png(hourglassIcon(rows))));
sandFrames.forEach((rows, i) => write(`${frames}/waiting-${i}.png`, png(waitingIcon(rows))));

// Claudette on the composer: her poses and props, which the app draws cell by cell (Mascot/MascotArt).
write('src/Claudette.App/Assets/Mascot/mascot.json', mascotJson());

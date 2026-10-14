// Encode the approved glass-wave logo as Windows multi-size icons. No desktop UI.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
(async () => {
  const root = path.resolve(__dirname, '../../src/PairUp.App');
  const svg = fs.readFileSync(path.join(root, 'Web/assets/shengyu-glass-logo.png'));
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
  const images = await Promise.all(sizes.map(size => sharp(svg).resize(size, size).png().toBuffer()));
  const header = Buffer.alloc(6 + 16 * sizes.length);
  header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, i) => {
    const at = 6 + 16 * i;
    header[at] = header[at + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, at + 4); header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(images[i].length, at + 8); header.writeUInt32LE(offset, at + 12);
    offset += images[i].length;
  });
  fs.writeFileSync(path.join(root, 'app.ico'), Buffer.concat([header, ...images]));
  fs.mkdirSync(path.join(root, 'Assets'), { recursive: true });
  await sharp(svg).resize(512, 512).png().toFile(path.join(root, 'Assets/shengyu.png'));
  console.log('Approved 声屿 glass mark exported at ' + sizes.join(', ') + ' px; no desktop window opened.');
})().catch(error => { console.error(error); process.exitCode = 1; });

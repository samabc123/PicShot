import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = dirname(fileURLToPath(import.meta.url));
const assetDir = resolve(here, '../../src/artwork');
const proofDir = resolve(here, '../../build/artwork-proof');
const channels = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript' };
const server = createServer(async (req, res) => {
  const path = resolve(here, '.' + new URL(req.url, 'http://localhost').pathname);
  if (!path.startsWith(here + sep) || !channels[extname(path)]) {
    res.writeHead(404).end();
    return;
  }
  try {
    const body = await readFile(path);
    res.writeHead(200, { 'Content-Type': channels[extname(path)] }).end(body);
  } catch {
    res.writeHead(404).end();
  }
});

await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
try {
  browser = await chromium.launch({
    channel: process.env.PICSHOT_BROWSER || 'chrome',
    args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader']
  });
  await mkdir(assetDir, { recursive: true });
  await mkdir(proofDir, { recursive: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  page.on('pageerror', error => console.error(error));
  await page.goto(`http://127.0.0.1:${server.address().port}/studio.html`);
  await page.waitForFunction(() => !!window.artwork);
  const names = await page.evaluate(() => window.artwork.names);
  const renders = [];
  for (const name of names) {
    const { image, metrics } = await page.evaluate(name => window.artwork.draw(name), name);
    const area = metrics.width * metrics.height;
    assert(metrics.opaque > area * 0.04, `${name}: insufficient visible pixels`);
    assert(metrics.transparent > area * 0.20, `${name}: missing transparency`);
    assert(metrics.corners.every(alpha => alpha === 0), `${name}: corner is not transparent`);
    const [x0, y0, x1, y1] = metrics.bounds;
    assert((name.startsWith('nail') || x0 > 0) && y0 > 0 &&
      x1 < metrics.width - 1 && y1 < metrics.height - 1, `${name}: unexpected clipping (${metrics.bounds})`);
    renders.push([name, Buffer.from(image.split(',')[1], 'base64')]);
    console.log(name, metrics.bounds, `${Math.round(metrics.transparent / area * 100)}% transparent`);
  }

  const original = await page.evaluate(() => window.artwork.draw('basket-back').image);
  const rotated = await page.evaluate(() => window.artwork.orbit(0.34));
  assert.notEqual(rotated, original, 'The geometry must respond to a camera rotation');
  await page.evaluate(() => { document.body.style.background = '#17201f'; });
  await page.screenshot({ path: resolve(proofDir, 'studio-orbit.png') });
  await page.setViewportSize({ width: 390, height: 520 });
  await page.evaluate(() => {
    document.body.style.background = '#eef2ee';
    window.artwork.draw('basket-back');
  });
  const size = await page.locator('canvas').boundingBox();
  assert(size && size.width <= 390 && size.height <= 520);
  await page.screenshot({ path: resolve(proofDir, 'studio-compact.png') });
  for (const [name, data] of renders) await writeFile(resolve(assetDir, `${name}.png`), data);
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

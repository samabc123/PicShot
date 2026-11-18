import * as T from 'three';
import { RoomEnvironment } from './node_modules/three/examples/jsm/environments/RoomEnvironment.js';

const vector = (x, y, z) => new T.Vector3(x, y, z);
const renderer = new T.WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true });
renderer.setClearColor(0, 0);
renderer.toneMapping = T.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.12;
document.body.appendChild(renderer.domElement);
const pmrem = new T.PMREMGenerator(renderer);
const room = new RoomEnvironment();
const reflections = pmrem.fromScene(room, 0.06);
room.dispose();
pmrem.dispose();

const finish = (color, roughness = 0.72, metalness = 0) =>
  new T.MeshStandardMaterial({ color, roughness, metalness });

function lighting() {
  const scene = new T.Scene();
  scene.environment = reflections.texture;
  scene.environmentIntensity = 0.44;
  scene.add(new T.HemisphereLight(0xf2f8ff, 0x776959, 1.25));
  for (const [color, strength, position] of [
    [0xfff0da, 2.6, [-3, 7, 5]],
    [0xc5e2ff, 0.92, [5, 3, -5]]
  ]) {
    const light = new T.DirectionalLight(color, strength);
    light.position.set(...position);
    scene.add(light);
  }
  return scene;
}

function lens(width, height, eye, aim) {
  const camera = new T.OrthographicCamera(-width / 2, width / 2, height / 2, -height / 2, 0.01, 100);
  camera.position.set(...eye);
  camera.lookAt(...aim);
  return camera;
}

class SampledCurve extends T.Curve {
  constructor(sample) { super(); this.sample = sample; }
  getPoint(t) { return this.sample(t); }
}

function tube(parent, sample, radius, material, steps = 64) {
  const mesh = new T.Mesh(new T.TubeGeometry(new SampledCurve(sample), steps, radius, 7), material);
  parent.add(mesh);
  return mesh;
}

const tau = 2 * Math.PI;
const rimY = 0.66;
const footY = -0.82;
const warpCount = 88;
const reeds = [0x36605b, 0x457069, 0x3a6661, 0x527b72, 0x31544f].map(c => finish(c));
const binding = finish(0x436b61, 0.55);
const twine = finish(0x7f9b8c);
const inner = finish(0x344d46, 0.96);
inner.side = T.DoubleSide;

function basketPoint(theta, h, offset = 0) {
  const swell = Math.sin(Math.PI * h) * 0.095;
  return vector((2.52 + 0.51 * h + swell + offset) * Math.cos(theta),
    T.MathUtils.lerp(footY, rimY, h),
    (1.46 + 0.56 * h + swell + offset) * Math.sin(theta));
}

function liner(parent, end) {
  const positions = [], indices = [];
  const segments = end === tau ? 128 : 64;
  for (let row = 0; row <= 8; row++) {
    for (let i = 0; i <= segments; i++) {
      const p = basketPoint(end * i / segments, row / 8, -0.047);
      positions.push(p.x, p.y, p.z);
      if (!i || !row) continue;
      const n = row * (segments + 1) + i;
      indices.push(n, n - 1, n - segments - 2, n, n - segments - 2, n - segments - 1);
    }
  }
  const geometry = new T.BufferGeometry();
  geometry.setAttribute('position', new T.Float32BufferAttribute(positions, 3));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();
  parent.add(new T.Mesh(geometry, inner));
}

function oval(parent, h, end, offset, radius, material) {
  tube(parent, t => basketPoint(t * end, h, offset), radius, material, end === tau ? 128 : 64);
}

function footShadow(parent) {
  const canvas = Object.assign(document.createElement('canvas'), { width: 128, height: 128 });
  const ctx = canvas.getContext('2d');
  const gradient = ctx.createRadialGradient(64, 64, 7, 64, 64, 64);
  gradient.addColorStop(0, 'rgba(16,29,27,.40)');
  gradient.addColorStop(0.68, 'rgba(16,29,27,.19)');
  gradient.addColorStop(1, 'rgba(16,29,27,0)');
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, 128, 128);
  const map = new T.CanvasTexture(canvas);
  map.colorSpace = T.SRGBColorSpace;
  const mesh = new T.Mesh(new T.PlaneGeometry(6.7, 4.0),
    new T.MeshBasicMaterial({ map, transparent: true, depthWrite: false, toneMapped: false }));
  mesh.rotation.x = -Math.PI / 2;
  mesh.position.set(0.16, footY - 0.07, 0.05);
  parent.add(mesh);
}

function basket(frontOnly = false) {
  const group = new T.Group();
  const end = frontOnly ? Math.PI : tau;
  liner(group, end);
  if (!frontOnly) {
    footShadow(group);
    const floor = new T.Mesh(new T.CylinderGeometry(1, 1, 0.07, 96), inner);
    floor.scale.set(2.53, 1, 1.48);
    floor.position.y = footY;
    group.add(floor);
  }

  // Alternate the radial order where each horizontal reed crosses an upright.
  const courses = 14;
  for (let row = 0; row < courses; row++) {
    const h = (row + 0.5) / courses;
    tube(group, t => {
      const theta = t * end;
      const phase = theta * warpCount / 2 + row * Math.PI;
      return basketPoint(theta, h, 0.018 + 0.031 * Math.cos(phase));
    }, 0.048, reeds[(row * 3 + 1) % reeds.length], frontOnly ? 220 : 440);
  }
  for (let i = 0; i <= warpCount / (frontOnly ? 2 : 1) - (frontOnly ? 0 : 1); i++) {
    tube(group, h => basketPoint(i * tau / warpCount + 0.003 * Math.sin(h * Math.PI),
      h, 0.029 + 0.027 * Math.sin(h * courses * Math.PI + i * Math.PI)),
    0.034, reeds[(i * 7 + 2) % reeds.length], 55);
  }
  oval(group, 0, end, 0.004, 0.065, binding);
  oval(group, 1, end, 0, 0.098, binding);
  oval(group, 0.945, end, 0.052, 0.038, twine);
  tube(group, t => {
    const theta = t * end;
    const twist = theta * 80;
    const p = basketPoint(theta, 1, 0.094 * Math.cos(twist));
    p.y += 0.094 * Math.sin(twist);
    return p;
  }, 0.018, twine, frontOnly ? 320 : 640);

  if (!frontOnly) {
    for (const side of [-1, 1]) {
      const handle = t => vector(side * (2.81 + 0.27 * Math.sin(Math.PI * t)),
        0.52 + 0.65 * Math.sin(Math.PI * t), -0.88 + 1.34 * t);
      tube(group, handle, 0.083, binding, 44);
      for (let i = 2; i < 22; i++) {
        const p = handle(i / 24);
        const wrap = new T.Mesh(new T.TorusGeometry(0.081, 0.018, 6, 16), twine);
        wrap.position.copy(p);
        const axis = handle((i + 0.1) / 24).sub(p).normalize();
        wrap.quaternion.setFromUnitVectors(vector(0, 0, 1), axis);
        group.add(wrap);
      }
    }
  }
  return group;
}

const palettes = [
  ['#709ea7', '#375d68'], ['#ca866f', '#863f38'], ['#94a677', '#4f6642'],
  ['#9694b7', '#5d537f'], ['#d6b071', '#806137'], ['#7190b4', '#3d577c']
];
const folds = [[-0.23, 0.19], [0.15, -0.26], [-0.42, -0.04],
  [0.08, 0.32], [-0.36, 0.23], [0.29, -0.17]];

function knit(index) {
  const [main, trim] = palettes[index];
  const canvas = Object.assign(document.createElement('canvas'), { width: 256, height: 320 });
  const ctx = canvas.getContext('2d');
  ctx.scale(4, 4);
  ctx.fillStyle = main;
  ctx.fillRect(0, 0, 64, 80);
  ctx.fillStyle = trim;
  ctx.fill(new Path2D('M0,0 H64 V23 Q43,26 21,21 L0,21 Z M0,52 Q18,50 25,73 L25,80 H0 Z M64,41 Q42,39 44,60 L64,61 Z'));
  ctx.fillStyle = '#f7eee0d4';
  ctx.fill(new Path2D('M22,14 Q42,18 63,15 V17 Q42,20 22,16 Z M22,19 Q42,23 63,20 V21.2 Q42,24 22,20.2 Z'));
  for (let y = 1; y < 80; y += 1.25) {
    for (let x = (Math.floor(y * 0.8) % 2) * 0.55; x < 64; x += 1.35) {
      ctx.beginPath();
      ctx.moveTo(x, y);
      ctx.lineTo(x + 0.48, y + 0.72);
      ctx.lineTo(x + 0.96, y);
      ctx.strokeStyle = '#fff9eb50';
      ctx.lineWidth = 0.26;
      ctx.stroke();
    }
  }
  ctx.strokeStyle = '#fffbf082';
  ctx.setLineDash([0.7, 0.85]);
  ctx.lineWidth = 0.42;
  ctx.stroke(new Path2D('M10,54 Q21,56 25,70 M55,42 Q46,44 44,57'));
  const map = new T.CanvasTexture(canvas);
  map.colorSpace = T.SRGBColorSpace;
  map.anisotropy = 4;
  return map;
}

function sock(index) {
  const path = new T.Shape();
  const p = (x, y) => [(x - 32) / 10, (40 - y) / 10];
  // Mirrored coordinates keep the stitch image and the extrusion in registration.
  path.moveTo(...p(28, 10));
  path.quadraticCurveTo(...p(28, 7), ...p(32, 7));
  path.lineTo(...p(53, 8));
  path.quadraticCurveTo(...p(57, 8), ...p(57, 12));
  path.lineTo(...p(55, 31));
  path.bezierCurveTo(...p(55, 41), ...p(59, 50), ...p(49, 56));
  path.lineTo(...p(25, 70));
  path.bezierCurveTo(...p(17, 75), ...p(7, 75), ...p(4, 69));
  path.bezierCurveTo(...p(1, 62), ...p(6, 57), ...p(12, 53));
  path.lineTo(...p(23, 44));
  path.bezierCurveTo(...p(27, 41), ...p(27, 37), ...p(27, 33));
  path.closePath();
  const meshGeometry = new T.ExtrudeGeometry(path, {
    depth: 0.47, bevelEnabled: true, bevelThickness: 0.15, bevelSize: 0.13, bevelSegments: 4, curveSegments: 14
  });
  meshGeometry.translate(0, 0, -0.24);
  const pos = meshGeometry.attributes.position;
  const uv = meshGeometry.attributes.uv;
  for (let i = 0; i < pos.count; i++) uv.setXY(i, 0.5 + pos.getX(i) / 6.4, 0.5 + pos.getY(i) / 8);
  const material = finish(0xffffff, 0.93);
  material.map = knit(index);
  const mesh = new T.Mesh(meshGeometry, material);
  return mesh;
}

const basketCamera = lens(8, 4.8, [0, 7.65, 8.4], [0, 0.37, 0]);
const sockCamera = lens(6.4, 8, [0, 0, 12], [0, 0, 0]);

function smallBasket() {
  const scene = lighting();
  scene.add(basket());
  [[-0.94, 0.82, -0.24, 1, 0.33], [0, 0.94, -0.51, 0, -0.22],
    [0.98, 0.68, 0.09, 2, -0.44]].forEach(([x, y, z, color, spin]) => {
    const piece = sock(color);
    piece.scale.setScalar(0.32);
    piece.position.set(x, y, z);
    piece.rotation.set(-0.28, 0.08, spin);
    scene.add(piece);
  });
  return [scene, lens(6.85, 6.85 * 82 / 72, [3.8, 6.2, 9], [0, 0.9, 0])];
}

function pin(hover) {
  const scene = lighting();
  const steel = finish(0x91a9b2, 0.28, 0.78);
  const cap = finish(hover ? 0x91b7ab : 0xabbcbe, hover ? 0.23 : 0.35, 0.65);
  const band = finish(0x708886, 0.29, 0.74);
  // One unit equals one overlay DIP; the buried tip lies beyond its left edge.
  const buried = vector(-11, -11, -5);
  const crown = vector(23, 11, 6);
  const axis = crown.clone().sub(buried).normalize();
  const shank = new T.Mesh(new T.CylinderGeometry(2.6, 0, buried.distanceTo(crown), 32), steel);
  shank.position.copy(buried).lerp(crown, 0.5);
  shank.quaternion.setFromUnitVectors(vector(0, 1, 0), axis);
  scene.add(shank);
  const profile = [[0, -3.1], [3, -3.1], [4.65, -2], [6.7, -1.35], [7.5, -0.3],
    [7.55, 1.0], [6.95, 2.15], [5.85, 2.8], [3.4, 3.23], [0, 3.32]]
    .map(([radius, y]) => new T.Vector2(radius, y));
  const head = new T.Mesh(new T.LatheGeometry(profile, 48), cap);
  head.position.copy(crown);
  head.quaternion.copy(shank.quaternion);
  scene.add(head);
  const edge = new T.Mesh(new T.TorusGeometry(7.55, 0.21, 8, 48), band);
  edge.rotation.x = Math.PI / 2;
  head.add(edge);
  return [scene, lens(42, 42, [21, 0, 60], [21, 0, 0])];
}

const specs = {
  'basket-back': [1200, 720, () => { const s = lighting(); s.add(basket()); return [s, basketCamera]; }],
  'basket-front': [1200, 720, () => { const s = lighting(); s.add(basket(true)); return [s, basketCamera]; }],
  'basket-icon': [216, 246, smallBasket],
  'nail': [168, 168, () => pin(false)],
  'nail-hover': [168, 168, () => pin(true)]
};
for (let i = 0; i < palettes.length; i++) {
  specs[`sock-${i}`] = [192, 240, () => {
    const scene = lighting();
    const piece = sock(i);
    piece.rotation.set(...folds[i], 0);
    scene.add(piece);
    return [scene, sockCamera];
  }];
}

let preview;
function draw(name) {
  const [width, height, model] = specs[name];
  renderer.setSize(width, height);
  const [scene, camera] = model();
  renderer.render(scene, camera);
  preview = { scene, camera };
  return renderer.domElement;
}

function examine(canvas) {
  const probe = document.createElement('canvas');
  probe.width = canvas.width;
  probe.height = canvas.height;
  const ctx = probe.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(canvas, 0, 0);
  const { data } = ctx.getImageData(0, 0, probe.width, probe.height);
  let opaque = 0;
  let transparent = 0;
  let minX = probe.width, minY = probe.height, maxX = 0, maxY = 0;
  for (let i = 3; i < data.length; i += 4) {
    if (data[i] === 0) transparent++;
    if (data[i] <= 32) continue;
    opaque++;
    const x = ((i - 3) / 4) % probe.width, y = Math.floor((i - 3) / 4 / probe.width);
    minX = Math.min(minX, x); minY = Math.min(minY, y);
    maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
  }
  const corners = [3, (probe.width - 1) * 4 + 3, data.length - probe.width * 4 + 3, data.length - 1];
  return { opaque, transparent, width: probe.width, height: probe.height,
    bounds: [minX, minY, maxX, maxY], corners: corners.map(i => data[i]) };
}

window.artwork = {
  names: Object.keys(specs),
  draw: name => {
    const canvas = draw(name);
    return { image: canvas.toDataURL(), metrics: examine(canvas) };
  },
  orbit: angle => {
    const { scene, camera } = preview;
    camera.position.applyAxisAngle(vector(0, 1, 0), angle);
    camera.lookAt(0, 0.37, 0);
    renderer.render(scene, camera);
    return renderer.domElement.toDataURL();
  }
};

// How each kind of thing looks: three.js primitives with a toon material, posed from the state every frame.
import * as THREE from 'three';
import { ITEMS } from '../items.gen.js';

// Three flat bands of light: the toon look.
const ramp = new THREE.DataTexture(new Uint8Array([90, 170, 255]), 3, 1, THREE.RedFormat);
ramp.minFilter = ramp.magFilter = THREE.NearestFilter;
ramp.needsUpdate = true;

const materials = new Map();
export function toon(color) {
  if (!materials.has(color)) materials.set(color, new THREE.MeshToonMaterial({ color, gradientMap: ramp }));
  return materials.get(color);
}

const mesh = (geometry, color, x = 0, y = 0, z = 0) => {
  const m = new THREE.Mesh(geometry, toon(color));
  m.position.set(x, y, z);
  return m;
};

const shadow = new THREE.MeshBasicMaterial({ color: '#000000', transparent: true, opacity: 0.25, depthWrite: false });
export function blobShadow(radius) {
  const m = new THREE.Mesh(new THREE.CircleGeometry(radius, 16), shadow);
  m.rotation.x = -Math.PI / 2;
  m.position.y = 0.02;
  return m;
}

export function makeHero() {
  const g = new THREE.Group();
  const body = new THREE.Group();
  body.add(mesh(new THREE.CylinderGeometry(0.28, 0.36, 0.7, 10), '#2f7fb8', 0, 0.55, 0));
  body.add(mesh(new THREE.SphereGeometry(0.25, 14, 10), '#f2c9a0', 0, 1.1, 0));
  body.add(mesh(new THREE.SphereGeometry(0.26, 14, 10, 0, Math.PI * 2, 0, Math.PI / 2), '#6b3e1f', 0, 1.14, -0.02));
  body.add(mesh(new THREE.TorusGeometry(0.22, 0.06, 6, 14), '#e8833a', 0, 0.88, 0)).rotation.x = Math.PI / 2;
  const sword = new THREE.Group();
  sword.add(mesh(new THREE.BoxGeometry(0.06, 0.06, 0.8), '#d9e2ea', 0, 0, 0.45));
  sword.add(mesh(new THREE.BoxGeometry(0.26, 0.06, 0.06), '#8a5a2b', 0, 0, 0.05));
  sword.position.set(0.35, 0.7, 0.1);
  body.add(sword);
  g.add(body, blobShadow(0.4));
  g.userData = { body, sword };
  return g;
}

export function poseHero(g, h, time) {
  const { body, sword } = g.userData;
  g.position.set(h.pos.x, 0, h.pos.z);
  g.rotation.y = h.facing;
  // A swing sweeps the sword across the front; a roll tumbles the body forward.
  sword.rotation.y = h.swing > 0 ? 1.2 - (1 - h.swing / 0.3) * 2.6 : 0.3;
  body.rotation.x = h.roll > 0 ? (1 - h.roll / 0.4) * Math.PI * 2 : 0;
  body.position.y = h.roll > 0 ? -0.3 : 0;
  body.visible = !(h.invuln > 0 && Math.floor(time * 20) % 2 === 0);
}

export function makeMesh(e) {
  const g = new THREE.Group();
  const item = ITEMS[e.kind];
  switch (item ? item.shape : e.kind) {
    case 'pot':
      g.add(mesh(new THREE.SphereGeometry(0.34, 14, 10), '#c8743c', 0, 0.34, 0));
      g.add(mesh(new THREE.CylinderGeometry(0.17, 0.14, 0.2, 12), '#a85a2a', 0, 0.72, 0));
      break;
    case 'crate':
      g.add(mesh(new THREE.BoxGeometry(1, 1, 1), '#9a6a3a', 0, 0.5, 0));
      g.add(mesh(new THREE.BoxGeometry(1.04, 0.12, 1.04), '#6e4a28', 0, 0.5, 0));
      break;
    case 'chest': {
      g.add(mesh(new THREE.BoxGeometry(1.1, 0.5, 0.8), '#8a4b22', 0, 0.25, 0));
      const lid = new THREE.Group();
      lid.add(mesh(new THREE.BoxGeometry(1.1, 0.25, 0.8), '#b8862f', 0, 0.12, 0.4));
      lid.position.set(0, 0.5, -0.4);
      g.add(lid);
      g.userData.lid = lid;
      break;
    }
    case 'door':
      g.add(mesh(new THREE.BoxGeometry(2, 2.5, 0.3), '#5b3a1e', 0, 1.25, 0));
      g.add(mesh(new THREE.BoxGeometry(0.3, 0.4, 0.34), '#f2c94c', 0, 1.2, 0));
      break;
    case 'slime':
      g.add(mesh(new THREE.SphereGeometry(0.45, 16, 12), '#5fcf5a', 0, 0.35, 0));
      g.add(mesh(new THREE.SphereGeometry(0.07, 8, 6), '#1d2b1d', -0.15, 0.5, 0.36), mesh(new THREE.SphereGeometry(0.07, 8, 6), '#1d2b1d', 0.15, 0.5, 0.36));
      g.add(blobShadow(0.45));
      break;
    case 'gem':
      g.add(mesh(new THREE.OctahedronGeometry(0.25), item.color, 0, 0.45, 0));
      break;
    case 'heart':
      g.add(mesh(new THREE.SphereGeometry(0.13, 10, 8), item.color, -0.1, 0.5, 0), mesh(new THREE.SphereGeometry(0.13, 10, 8), item.color, 0.1, 0.5, 0));
      g.add(mesh(new THREE.ConeGeometry(0.2, 0.3, 10), item.color, 0, 0.32, 0)).rotation.z = Math.PI;
      break;
    case 'key':
      g.add(mesh(new THREE.TorusGeometry(0.12, 0.04, 6, 12), item.color, 0, 0.6, 0));
      g.add(mesh(new THREE.BoxGeometry(0.06, 0.35, 0.06), item.color, 0, 0.32, 0));
      break;
    default: {
      // A thing without a look of its own (a piece, a new kind): a small box in its colour.
      const size = e.size || 0.3;
      g.add(mesh(new THREE.BoxGeometry(size, size, size), e.color || '#cccccc', 0, size / 2, 0));
    }
  }
  return g;
}

export function poseMesh(g, e, time) {
  g.position.set(e.pos.x, e.pos.y || 0, e.pos.z);
  if (e.pickup) g.rotation.y = time * 2;
  if (e.kind === 'slime') {
    g.rotation.y = e.lungeDir ?? 0;
    g.scale.set(1, e.mode === 'tell' ? 0.7 : 1, 1);
    g.visible = !(e.mode === 'hurt' && Math.floor(time * 20) % 2 === 0);
  }
  if (e.kind === 'chest') g.userData.lid.rotation.x = e.opened ? -1.9 : 0;
  if (e.kind === 'door') g.visible = !e.open;
  g.rotation.z = e.shakeUntil > time ? Math.sin(time * 60) * 0.1 : 0;
}

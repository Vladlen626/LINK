// The grass as one instanced mesh, built once; only a cut (grassVersion changes) rewrites the blades' matrices.
import * as THREE from 'three';
import { toon } from './shapes.js';

export function makeGrass(scene, state) {
  const geometry = new THREE.ConeGeometry(0.06, 0.45, 4);
  geometry.translate(0, 0.22, 0);
  const blades = new THREE.InstancedMesh(geometry, toon('#4fae3c'), Math.max(1, state.grass.length));
  blades.count = state.grass.length;
  scene.add(blades);
  const m = new THREE.Matrix4(), q = new THREE.Quaternion(), s = new THREE.Vector3(), p = new THREE.Vector3();
  const up = new THREE.Vector3(0, 1, 0);
  let version = -1;
  return {
    update(state) {
      if (state.grassVersion === version) return;
      version = state.grassVersion;
      state.grass.forEach((blade, i) => {
        // A blade's own lean and height come from its place, so redrawing never makes it jump.
        const h = 0.7 + ((blade.x * 7.3 + blade.z * 3.1) % 1 + 1) % 1 * 0.6;
        q.setFromAxisAngle(up, blade.x * 13 + blade.z * 7);
        s.set(1, blade.cut ? 0 : h, 1);
        blades.setMatrixAt(i, m.compose(p.set(blade.x, 0, blade.z), q, s));
      });
      blades.instanceMatrix.needsUpdate = true;
    },
  };
}

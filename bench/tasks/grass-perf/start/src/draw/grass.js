// The grass: a blade mesh for each blade still standing, rebuilt from the state every frame so cuts show at once.
import * as THREE from 'three';
import { toon } from './shapes.js';

export function makeGrass(scene) {
  const geometry = new THREE.ConeGeometry(0.06, 0.45, 4);
  geometry.translate(0, 0.22, 0);
  const material = toon('#4fae3c');
  let field = new THREE.Group();
  scene.add(field);
  return {
    update(state) {
      scene.remove(field);
      field = new THREE.Group();
      for (const blade of state.grass) {
        if (blade.cut) continue;
        const m = new THREE.Mesh(geometry, material);
        // A blade's own lean and height come from its place, so redrawing never makes it jump.
        m.position.set(blade.x, 0, blade.z);
        m.rotation.y = blade.x * 13 + blade.z * 7;
        m.scale.y = 0.7 + ((blade.x * 7.3 + blade.z * 3.1) % 1 + 1) % 1 * 0.6;
        field.add(m);
      }
      scene.add(field);
    },
  };
}

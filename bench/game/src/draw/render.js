// Draws the state with three.js. It only reads the state: everything that moves is in the logic.
import * as THREE from 'three';
import { makeHero, makeMesh, poseHero, poseMesh, toon } from './shapes.js';
import { makeGrass } from './grass.js';

export function createView(canvas, state) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  const scene = new THREE.Scene();
  scene.background = new THREE.Color('#8ccfef');
  scene.fog = new THREE.Fog('#8ccfef', 35, 80);
  scene.add(new THREE.HemisphereLight('#ffffff', '#5b7d4a', 1.4));
  const sun = new THREE.DirectionalLight('#fff1d0', 2.2);
  sun.position.set(6, 12, 5);
  scene.add(sun);

  buildGround(scene, state.radius);
  for (const w of state.walls) {
    const wall = new THREE.Mesh(new THREE.BoxGeometry(w.w, w.h, w.d), toon(w.h > 1.1 ? '#9a978f' : '#b08850'));
    wall.position.set(w.x, w.h / 2, w.z);
    scene.add(wall);
  }
  const grass = makeGrass(scene, state);
  const hero = makeHero();
  scene.add(hero);
  const meshes = new Map();
  const camera = new THREE.PerspectiveCamera(state.camera.fov, 1, 0.1, 200);
  let pinned = null;

  return {
    draw(state) {
      const { clientWidth: w, clientHeight: h } = canvas;
      if (canvas.width !== Math.floor(w * renderer.getPixelRatio()) || camera.aspect !== w / h) {
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
      }
      const seen = new Set();
      for (const e of state.entities) {
        let m = meshes.get(e.id);
        if (!m) {
          m = makeMesh(e);
          meshes.set(e.id, m);
          scene.add(m);
        }
        poseMesh(m, e, state.time);
        seen.add(e.id);
      }
      for (const [id, m] of meshes) {
        if (!seen.has(id)) {
          scene.remove(m);
          meshes.delete(id);
        }
      }
      poseHero(hero, state.hero, state.time);
      grass.update(state);
      const view = pinned || state.camera;
      camera.fov = state.camera.fov;
      camera.position.set(view.pos.x, view.pos.y, view.pos.z);
      camera.lookAt(view.look.x, view.look.y, view.look.z);
      camera.updateProjectionMatrix();
      renderer.render(scene, camera);
    },
    // Pins the camera to a place and a point to look at (pictures from fixed angles); view(null) lets it follow again.
    view(pos, look) {
      pinned = pos ? { pos, look } : null;
    },
  };
}

function buildGround(scene, radius) {
  const sea = new THREE.Mesh(new THREE.CircleGeometry(200, 32), new THREE.MeshBasicMaterial({ color: '#3a8fc9' }));
  sea.rotation.x = -Math.PI / 2;
  sea.position.y = -0.4;
  const sand = new THREE.Mesh(new THREE.CylinderGeometry(radius + 1, radius + 2, 0.6, 48), toon('#e9d8a0'));
  sand.position.y = -0.32;
  const turf = new THREE.Mesh(new THREE.CylinderGeometry(radius - 0.5, radius - 0.5, 0.1, 48), toon('#7cc957'));
  turf.position.y = -0.05;
  scene.add(sea, sand, turf);
}

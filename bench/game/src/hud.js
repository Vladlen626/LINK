// The HUD over the game (#hud): hearts, gems and keys. It redraws only when one of them changes.
export function createHud(root) {
  let last = '';
  return {
    update(h) {
      const now = `${h.hp}/${h.maxHp}/${h.gems}/${h.keys}`;
      if (now === last) return;
      last = now;
      const hearts = Array.from({ length: h.maxHp }, (_, i) => `<span class="heart${i < h.hp ? '' : ' empty'}">&#9829;</span>`).join('');
      root.innerHTML = `<div class="hearts">${hearts}</div>` +
        `<div class="count gems"><span class="icon">&#9670;</span>${h.gems}</div>` +
        `<div class="count keys"><span class="icon">&#9919;</span>${h.keys}</div>`;
    },
  };
}

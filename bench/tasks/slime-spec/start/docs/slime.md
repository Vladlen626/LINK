# Slime (design)

The island's one enemy: a small green slime. Simple to read, fair to fight. Numbers are in metres and seconds.

## State
A slime is an entity of kind `slime` with `hp` and a `mode`: `idle`, `chase`, `tell`, `lunge`, `rest` or `hurt`.
Tests read these two fields.

## Rules
1. A slime has 2 HP: one sword hit does not defeat it.
2. Idle, it hops in place: a hop every 1.2 s, 0.4 m high. It does not wander.
3. It notices the hero within 6 m and starts to chase (`chase`).
4. Only in sight: a wall between them hides the hero.
5. Chasing, it moves straight toward the hero at 2.5 m/s.
6. It gives up when the hero is more than 10 m away, and is idle again.
7. Within 2 m of the hero it stops and tells for 0.5 s (`tell`): it squashes and does not move.
8. Then it lunges (`lunge`) at 6 m/s for 0.4 s, straight toward where the hero was when the tell began, then rests
   for 1 s (`rest`, still), then chases again.
9. A lunge that touches the hero takes one heart and knocks the hero back, 1 m or more away from the slime.
10. A sword hit takes 1 HP and knocks the slime back, 1.5 m or more away from the hero.
11. A hit slime blinks for 0.6 s (`hurt`): it cannot be hurt again while it blinks, and does not move on its own.
12. A defeated slime is gone at once and leaves a gem where it was.

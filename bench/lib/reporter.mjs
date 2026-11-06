// A node:test reporter that prints a check's result as one JSON line: { score, passed, total, notes }. Each top-level
// test is one case; a file that fails before its tests run counts as one failed case.
export default async function* reporter(source) {
  let passed = 0, total = 0;
  const notes = [];
  for await (const event of source) {
    if (event.type !== 'test:pass' && event.type !== 'test:fail') continue;
    if (event.data.nesting !== 0 || event.data.details?.type === 'suite') continue;
    total++;
    if (event.type === 'test:pass') {
      passed++;
    } else {
      const error = event.data.details?.error;
      const why = String(error?.cause?.message || error?.message || error || 'failed').split('\n')[0].slice(0, 160);
      notes.push(`${event.data.name}: ${why}`);
    }
  }
  yield `${JSON.stringify({ score: total ? Math.round((100 * passed) / total) : 0, passed, total, notes })}\n`;
}

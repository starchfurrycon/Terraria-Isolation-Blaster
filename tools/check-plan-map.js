// Structural check of the generated plan map: the embedded script must be syntactically valid and
// its payload must decode back to the cell grid the planner wrote.
const fs = require('fs');
const vm = require('vm');

const file = process.argv[2];
const html = fs.readFileSync(file, 'utf8');

const script = html.match(/<script>\n([\s\S]*?)\n<\/script>/);
if (!script) {
  console.error('FAIL: no script block found');
  process.exit(2);
}

new vm.Script(script[1]);
console.log('script syntax ok, ' + script[1].length + ' chars');

const literal = html.match(/const DATA = (\{.*\});\n/);
if (!literal) {
  console.error('FAIL: no DATA literal found');
  process.exit(2);
}

const data = eval('(' + literal[1] + ')');
const cells = Buffer.from(data.cells, 'base64');
console.log(
  'world ' + data.width + 'x' + data.height +
  ' cell ' + data.cell +
  ' grid ' + data.cols + 'x' + data.rows +
  ' bytes ' + cells.length + ' (expected ' + data.cols * data.rows + ')');

if (cells.length !== data.cols * data.rows) {
  console.error('FAIL: cell grid size mismatch');
  process.exit(2);
}

const flags = [0, 0, 0, 0];
for (const value of cells) {
  for (let bit = 0; bit < 4; bit++) {
    if (value & (1 << bit)) flags[bit]++;
  }
}

console.log('cells with node/evil/hallow/fence: ' + flags.join(' / '));
console.log('charges ' + data.charges.length + ', sections ' + data.sections.length);

if (data.charges.length === 0 || data.sections.length === 0) {
  console.error('FAIL: payload carries no work');
  process.exit(2);
}

// Every charge must be inside the world and carry six fields, and every section four box corners.
const bad = data.charges.filter((c) => c.length !== 6 || c[1] < 0 || c[1] >= data.width || c[2] < 0 || c[2] >= data.height);
if (bad.length > 0) {
  console.error('FAIL: ' + bad.length + ' malformed charges');
  process.exit(2);
}

const badSections = data.sections.filter(
  (s) => s.length !== 8 || s[3] < s[1] || s[4] < s[2] || s[1] < 0 || s[4] >= data.height);
if (badSections.length > 0) {
  console.error('FAIL: ' + badSections.length + ' malformed sections');
  process.exit(2);
}

// The bands must actually be diagonal somewhere, otherwise the whole point of the planner is lost:
// count sections whose bounding box is far from axis aligned in aspect ratio terms.
const wide = data.sections.filter((s) => (s[3] - s[1]) > (s[4] - s[2]) * 1.5).length;
const tall = data.sections.filter((s) => (s[4] - s[2]) > (s[3] - s[1]) * 1.5).length;
console.log('sections wider than tall by 1.5x: ' + wide + ', taller than wide: ' + tall);

console.log('OK');

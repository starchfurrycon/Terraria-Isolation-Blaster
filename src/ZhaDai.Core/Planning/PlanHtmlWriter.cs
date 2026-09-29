using System.Globalization;
using System.Text;
using ZhaDai.Core.Analysis;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Planning;

/// <summary>
/// Writes a self-contained interactive map of the plan: a downsampled world overview plus the
/// bands and every charge. Light palette, no external requests, no build step.
/// </summary>
public static class PlanHtmlWriter
{
    private const int CellSize = 16;

    public static void Write(BlastPlan plan, string path, string worldPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        File.WriteAllText(path, Build(plan, worldPath), new UTF8Encoding(false));
    }

    public static string Build(BlastPlan plan, string worldPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WorldMetadata world = plan.World;
        PlanOverview overview = plan.Overview ?? new PlanOverview(CellSize, 1, 1, new byte[1]);
        int cols = overview.Cols;
        int rows = overview.Rows;
        byte[] cells = overview.Cells;

        StringBuilder charges = new();
        charges.Append('[');
        for (int i = 0; i < plan.Charges.Count; i++)
        {
            BlastCharge charge = plan.Charges[i];
            if (i > 0)
            {
                charges.Append(',');
            }

            charges.Append('[').Append(charge.Order).Append(',')
                .Append(charge.X).Append(',').Append(charge.Y).Append(',')
                .Append(charge.SectionSequence).Append(',')
                .Append(charge.RetreatAvailable ? 1 : 0).Append(',')
                .Append(HazardBits(charge)).Append(']');
        }

        charges.Append(']');

        StringBuilder sections = new();
        sections.Append('[');
        for (int i = 0; i < plan.Sections.Count; i++)
        {
            FenceSection section = plan.Sections[i];
            if (i > 0)
            {
                sections.Append(',');
            }

            sections.Append('[').Append(section.Sequence).Append(',')
                .Append(section.Bounds.MinX).Append(',').Append(section.Bounds.MinY).Append(',')
                .Append(section.Bounds.MaxX).Append(',').Append(section.Bounds.MaxY).Append(',')
                .Append(section.FenceTiles).Append(',').Append(section.Charges).Append(',')
                .Append(section.SealedByFloodVerification ? 1 : 0).Append(']');
        }

        sections.Append(']');

        string data = string.Format(
            CultureInfo.InvariantCulture,
            "{{width:{0},height:{1},cell:{2},cols:{3},rows:{4},cells:\"{5}\",charges:{6},sections:{7}}}",
            world.Width,
            world.Height,
            overview.CellSize,
            cols,
            rows,
            Convert.ToBase64String(cells),
            charges,
            sections);

        string summary = string.Format(
            CultureInfo.InvariantCulture,
            "{0} · {1} × {2} · {3}",
            Escape(world.Title),
            world.Width,
            world.Height,
            world.HardMode ? "困难模式" : "肉前");

        string stats = string.Format(
            CultureInfo.InvariantCulture,
            "隔离段 {0} · 封带 {1} 格 · 雷管 {2} 发 · 约 {3} 组 · 封住感染源 {4} 格 · 预计 {5} 分钟",
            plan.Summary.Sections,
            plan.Summary.FenceTiles,
            plan.Summary.Charges,
            plan.Summary.DynamiteStacks,
            plan.Summary.EnclosedSeedTiles,
            Math.Round(plan.Summary.EstimatedPlayerSeconds / 60d, 1));

        string seal = plan.Summary.AllSectionsSealed ? "全部封住" : "有段未封住";

        return Template
            .Replace("__DATA__", data, StringComparison.Ordinal)
            .Replace("__TITLE__", Escape(world.Title), StringComparison.Ordinal)
            .Replace("__SUMMARY__", summary, StringComparison.Ordinal)
            .Replace("__STATS__", stats, StringComparison.Ordinal)
            .Replace("__SEAL__", seal, StringComparison.Ordinal)
            .Replace("__SEALCLASS__", plan.Summary.AllSectionsSealed ? "ok" : "bad", StringComparison.Ordinal)
            .Replace("__WORLDPATH__", Escape(Path.GetFileName(worldPath)), StringComparison.Ordinal);
    }

    private static int HazardBits(BlastCharge charge)
    {
        int bits = 0;
        if (charge.LavaInBlast) bits |= 1;
        if (charge.WaterInBlast) bits |= 2;
        if (charge.TrapInBlast) bits |= 4;
        if (charge.GravestoneInBlast) bits |= 8;
        if (charge.ExplosivesInBlast) bits |= 16;
        return bits;
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal);

    private const string Template = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>炸带 · __TITLE__</title>
<style>
  :root{
    --canvas:#fafafb; --surface:#ffffff; --subtle:#f4f5f7; --border:#d6dae0;
    --text:#20242c; --muted:#7a828e; --accent:#4078c8; --accent-soft:#e8f0fc;
    --evil:#8f5ac8; --hallow:#d474b8; --fence:#4078c8; --charge:#269660; --bad:#c64040;
  }
  *{box-sizing:border-box}
  html,body{height:100%;margin:0}
  body{background:var(--canvas);color:var(--text);
    font:13px/1.6 "Microsoft YaHei UI","Microsoft YaHei","Noto Sans SC","Segoe UI",sans-serif;
    display:flex;flex-direction:column}
  header{display:flex;align-items:center;gap:16px;padding:12px 20px;background:var(--surface);
    border-bottom:1px solid var(--border)}
  header h1{margin:0;font-size:15px;font-weight:600;letter-spacing:.06em}
  header .name{color:var(--accent);font-weight:600;letter-spacing:.18em}
  header .meta{color:var(--muted);font-size:12px}
  header .spacer{flex:1}
  .actions{display:flex;gap:8px}
  button{border:1px solid var(--border);background:var(--surface);color:var(--text);
    padding:5px 12px;border-radius:2px;cursor:pointer;font:inherit;font-size:12px}
  button:hover{border-color:var(--accent);color:var(--accent)}
  button[aria-pressed="true"]{background:var(--accent-soft);border-color:var(--accent);color:var(--accent)}
  main{flex:1;position:relative;overflow:hidden}
  canvas{display:block;width:100%;height:100%;cursor:grab}
  canvas.dragging{cursor:grabbing}
  aside{position:absolute;left:20px;top:20px;background:var(--surface);border:1px solid var(--border);
    border-radius:3px;padding:12px 16px;max-width:360px}
  aside .stats{color:var(--muted);font-size:12px;margin-bottom:8px}
  aside .seal{font-size:12px}
  aside .seal.ok{color:var(--charge)}
  aside .seal.bad{color:var(--bad)}
  .legend{position:absolute;right:20px;bottom:20px;background:var(--surface);border:1px solid var(--border);
    border-radius:3px;padding:10px 14px;font-size:12px;color:var(--muted)}
  .legend span{display:inline-flex;align-items:center;gap:6px;margin-right:14px}
  .legend i{width:10px;height:10px;border-radius:2px;display:inline-block}
  .hint{position:absolute;left:20px;bottom:20px;color:var(--muted);font-size:12px}
</style>
</head>
<body>
<header>
  <h1><span class="name">炸带</span> 隔离带爆破方案</h1>
  <span class="meta">__SUMMARY__</span>
  <span class="spacer"></span>
  <div class="actions">
    <button id="fit" aria-pressed="false">适应世界</button>
    <button id="t-fence" aria-pressed="true">封带</button>
    <button id="t-charges" aria-pressed="true">雷管</button>
    <button id="t-order" aria-pressed="false">序号</button>
    <button id="t-sections" aria-pressed="false">分段框</button>
  </div>
</header>
<main>
  <canvas id="view"></canvas>
  <aside>
    <div class="stats">__STATS__</div>
    <div class="seal __SEALCLASS__">泛洪复核：__SEAL__</div>
  </aside>
  <div class="legend">
    <span><i style="background:#dfe3e8"></i>可感染物块</span>
    <span><i style="background:#8f5ac8"></i>腐化/猩红</span>
    <span><i style="background:#d474b8"></i>神圣</span>
    <span><i style="background:#4078c8"></i>封带</span>
    <span><i style="background:#269660"></i>雷管</span>
    <span><i style="background:#c64040"></i>危险</span>
  </div>
  <div class="hint">拖动平移 · 滚轮缩放 · 文件 __WORLDPATH__</div>
</main>
<script>
const DATA = __DATA__;
const bits = atob(DATA.cells);
const cells = new Uint8Array(bits.length);
for (let i = 0; i < bits.length; i++) cells[i] = bits.charCodeAt(i);

const canvas = document.getElementById('view');
const ctx = canvas.getContext('2d');
let scale = 1, offsetX = 0, offsetY = 0;
const view = { fence: true, charges: true, order: false, sections: false };

function resize() {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = canvas.clientWidth * dpr;
  canvas.height = canvas.clientHeight * dpr;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  draw();
}

function fit() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  scale = Math.min(w / DATA.width, h / DATA.height) * 0.94;
  offsetX = (w - DATA.width * scale) / 2;
  offsetY = (h - DATA.height * scale) / 2;
  draw();
}

function sx(x) { return offsetX + x * scale; }
function sy(y) { return offsetY + y * scale; }

function draw() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  ctx.fillStyle = '#fafafb';
  ctx.fillRect(0, 0, w, h);
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(sx(0), sy(0), DATA.width * scale, DATA.height * scale);
  ctx.strokeStyle = '#d6dae0';
  ctx.lineWidth = 1;
  ctx.strokeRect(sx(0) + .5, sy(0) + .5, DATA.width * scale, DATA.height * scale);

  const size = DATA.cell * scale;
  for (let row = 0; row < DATA.rows; row++) {
    for (let col = 0; col < DATA.cols; col++) {
      const value = cells[row * DATA.cols + col];
      if (!value) continue;
      let colour = '#dfe3e8';
      if (value & 2) colour = '#8f5ac8';
      else if (value & 4) colour = '#d474b8';
      const isFence = (value & 8) !== 0;
      if (isFence) {
        if (!view.fence) continue;
        ctx.fillStyle = '#4078c8';
        ctx.globalAlpha = 0.85;
      } else if (colour) {
        ctx.fillStyle = colour;
        ctx.globalAlpha = 0.7;
      } else continue;
      ctx.fillRect(sx(col * DATA.cell), sy(row * DATA.cell), Math.max(1, size), Math.max(1, size));
      ctx.globalAlpha = 1;
    }
  }

  if (view.sections) {
    ctx.strokeStyle = 'rgba(64,120,200,.55)';
    ctx.setLineDash([5, 4]);
    for (const s of DATA.sections) {
      ctx.strokeRect(sx(s[1]), sy(s[2]), (s[3] - s[1]) * scale, (s[4] - s[2]) * scale);
    }
    ctx.setLineDash([]);
  }

  if (view.charges) {
    for (const c of DATA.charges) {
      const danger = c[5] !== 0;
      ctx.fillStyle = danger ? '#c64040' : '#269660';
      const r = Math.max(2, 7 * scale);
      ctx.beginPath();
      ctx.arc(sx(c[1] + 0.5), sy(c[2] + 0.5), r, 0, Math.PI * 2);
      ctx.globalAlpha = c[4] ? 0.85 : 0.4;
      ctx.fill();
      ctx.globalAlpha = 1;
    }
    if (view.order && scale > 0.25) {
      ctx.fillStyle = '#20242c';
      ctx.font = '10px "Microsoft YaHei UI",sans-serif';
      for (const c of DATA.charges) {
        if (c[0] % 5 === 0) ctx.fillText(String(c[0]), sx(c[1]) + 4, sy(c[2]) - 4);
      }
    }
  }
}

canvas.addEventListener('mousedown', (e) => {
  canvas.classList.add('dragging');
  const startX = e.clientX, startY = e.clientY;
  const ox = offsetX, oy = offsetY;
  const move = (m) => { offsetX = ox + (m.clientX - startX); offsetY = oy + (m.clientY - startY); draw(); };
  const up = () => {
    canvas.classList.remove('dragging');
    window.removeEventListener('mousemove', move);
    window.removeEventListener('mouseup', up);
  };
  window.addEventListener('mousemove', move);
  window.addEventListener('mouseup', up);
});

canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  const rect = canvas.getBoundingClientRect();
  const mx = e.clientX - rect.left, my = e.clientY - rect.top;
  const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
  const next = Math.min(24, Math.max(0.05, scale * factor));
  offsetX = mx - (mx - offsetX) * (next / scale);
  offsetY = my - (my - offsetY) * (next / scale);
  scale = next;
  draw();
});

function toggle(id, key) {
  const button = document.getElementById(id);
  button.addEventListener('click', () => {
    view[key] = !view[key];
    button.setAttribute('aria-pressed', String(view[key]));
    draw();
  });
}

document.getElementById('fit').addEventListener('click', fit);
toggle('t-fence', 'fence');
toggle('t-charges', 'charges');
toggle('t-order', 'order');
toggle('t-sections', 'sections');
window.addEventListener('resize', resize);
resize();
fit();
</script>
</body>
</html>
""";
}

"""Generate (and optionally serve) the human review viewer for one skill-creator iteration.

Usage:
    python eval-viewer/generate_review.py <iteration-dir> --skill-name "my-skill" \
        --benchmark <iteration-dir>/benchmark.json [--previous-workspace <prev-iteration-dir>] \
        [--static <output.html>]

Default (no --static): starts a local HTTP server, opens a browser, and writes feedback.json into the
iteration dir when the user clicks "Submit All Reviews". Background it from the caller (nohup ... &) and
capture the PID so you can kill it later.

--static <path>: writes a standalone HTML file instead of serving. "Submit All Reviews" then downloads
a feedback.json the user drops into the iteration dir.
"""
from __future__ import annotations

import argparse
import base64
import json
import mimetypes
import webbrowser
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

TEXT_EXT = {".txt", ".md", ".json", ".csv", ".tsv", ".py", ".js", ".ts", ".html", ".xml",
            ".yaml", ".yml", ".log", ".sh", ".sql", ".toml", ".ini"}
IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"}
MAX_TEXT = 200_000
MAX_IMG = 2_000_000


def _read_outputs(run_dir: Path):
    base = run_dir / "outputs"
    root = base if base.is_dir() else run_dir
    out = []
    if not root.is_dir():
        return out
    for p in sorted(root.rglob("*")):
        if not p.is_file() or "__pycache__" in p.parts:
            continue
        ext = p.suffix.lower()
        rel = str(p.relative_to(root))
        try:
            size = p.stat().st_size
            if ext in IMG_EXT and size <= MAX_IMG:
                mime = mimetypes.guess_type(p.name)[0] or "image/png"
                data = base64.b64encode(p.read_bytes()).decode()
                out.append({"name": rel, "kind": "image", "content": f"data:{mime};base64,{data}"})
            elif ext in TEXT_EXT and size <= MAX_TEXT:
                out.append({"name": rel, "kind": "text", "content": p.read_text(encoding="utf-8", errors="replace")})
            else:
                out.append({"name": rel, "kind": "binary", "content": f"{size} bytes"})
        except OSError:
            continue
    return out


def _grades(run_dir: Path):
    g = run_dir / "grading.json"
    if not g.exists():
        return []
    try:
        return json.loads(g.read_text(encoding="utf-8")).get("expectations", [])
    except (json.JSONDecodeError, OSError):
        return []


def _prev_feedback(prev_dir: Path | None):
    if not prev_dir:
        return {}
    fb = prev_dir / "feedback.json"
    if not fb.exists():
        return {}
    try:
        return {r["run_id"]: r.get("feedback", "")
                for r in json.loads(fb.read_text(encoding="utf-8")).get("reviews", [])}
    except (json.JSONDecodeError, OSError):
        return {}


def collect(iteration_dir: Path, prev_dir: Path | None):
    prev_fb = _prev_feedback(prev_dir)
    evals = []
    for ed in sorted(d for d in iteration_dir.iterdir()
                     if d.is_dir() and (d / "eval_metadata.json").exists()):
        try:
            meta = json.loads((ed / "eval_metadata.json").read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            meta = {}
        name = meta.get("eval_name", ed.name)
        run_id = f"{name}-with_skill"
        prev_out = []
        if prev_dir and (prev_dir / ed.name / "with_skill").is_dir():
            prev_out = _read_outputs(prev_dir / ed.name / "with_skill")
        evals.append({
            "eval_name": name,
            "run_id": run_id,
            "prompt": meta.get("prompt", ""),
            "outputs": _read_outputs(ed / "with_skill"),
            "grades": _grades(ed / "with_skill"),
            "previous_outputs": prev_out,
            "previous_feedback": prev_fb.get(run_id, ""),
        })
    return evals


PAGE = r"""<!doctype html><html><head><meta charset="utf-8">
<title>Skill review — __SKILL__</title>
<style>
:root{--b:#dcdcdc;--fg:#1a1a1a;--mut:#666;--ok:#2a8a2e;--no:#c0392b;--bg:#fafafa}
*{box-sizing:border-box}body{font:14px/1.55 system-ui,sans-serif;margin:0;color:var(--fg);background:var(--bg)}
header{display:flex;gap:.6rem;align-items:center;padding:.6rem 1rem;border-bottom:1px solid var(--b);background:#fff;position:sticky;top:0;z-index:2}
header h1{font-size:15px;margin:0;flex:1}
.tab{padding:.35rem .9rem;border:1px solid var(--b);border-radius:6px;background:#fff;cursor:pointer}
.tab.on{background:#1a1a1a;color:#fff;border-color:#1a1a1a}
main{max-width:62rem;margin:0 auto;padding:1rem}
.card{background:#fff;border:1px solid var(--b);border-radius:8px;padding:1rem;margin-bottom:1rem}
.lbl{font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:var(--mut);margin-bottom:.3rem}
pre{white-space:pre-wrap;word-break:break-word;background:#f6f6f6;padding:.6rem;border-radius:6px;overflow:auto;max-height:34rem;margin:.3rem 0}
img{max-width:100%;border:1px solid var(--b);border-radius:6px}
textarea{width:100%;min-height:5rem;font:inherit;padding:.5rem;border:1px solid var(--b);border-radius:6px}
details{margin:.5rem 0}summary{cursor:pointer;color:var(--mut)}
.nav{display:flex;gap:.5rem;align-items:center;justify-content:space-between;margin-bottom:1rem}
button{font:inherit;padding:.4rem .8rem;border:1px solid var(--b);border-radius:6px;background:#fff;cursor:pointer}
.primary{background:#1a1a1a;color:#fff;border-color:#1a1a1a}
table{border-collapse:collapse;width:100%}td,th{border:1px solid var(--b);padding:.4rem .6rem;text-align:center}
.pass{color:var(--ok);font-weight:600}.fail{color:var(--no);font-weight:600}
.file{margin:.6rem 0}.file .lbl{display:flex;justify-content:space-between;gap:1rem}
</style></head><body>
<header><h1>Skill review — __SKILL__</h1>
<div class="tab on" data-tab="outputs">Outputs</div>
<div class="tab" data-tab="benchmark">Benchmark</div>
<button class="primary" id="submit">Submit All Reviews</button></header>
<main><div id="outputs"></div><div id="benchmark" style="display:none"></div></main>
<script>
const EVALS=/*EVALS*/, BENCH=/*BENCH*/, SERVER=/*SERVER*/;
const fb={}; let idx=0;
const esc=s=>(s==null?'':String(s)).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
function renderFile(f){
  if(f.kind==='image') return `<div class="file"><div class="lbl">${esc(f.name)}</div><img src="${f.content}"></div>`;
  if(f.kind==='binary') return `<div class="file"><div class="lbl"><span>${esc(f.name)}</span><span>${esc(f.content)}</span></div></div>`;
  return `<div class="file"><div class="lbl">${esc(f.name)}</div><pre>${esc(f.content)}</pre></div>`;
}
function save(){const t=document.getElementById('fb');if(t&&EVALS[idx])fb[EVALS[idx].run_id]=t.value;}
function renderOutputs(){
  const host=document.getElementById('outputs');
  if(!EVALS.length){host.innerHTML='<div class="card">No evals found in this iteration.</div>';return;}
  const e=EVALS[idx]; if(fb[e.run_id]==null)fb[e.run_id]='';
  const grades=e.grades.length?`<details><summary>Formal grades (${e.grades.filter(g=>g.passed).length}/${e.grades.length} passed)</summary>`+
    e.grades.map(g=>`<div style="margin:.3rem 0"><span class="${g.passed?'pass':'fail'}">${g.passed?'PASS':'FAIL'}</span> ${esc(g.text)}<br><small style="color:var(--mut)">${esc(g.evidence)}</small></div>`).join('')+`</details>`:'';
  const prevOut=(e.previous_outputs&&e.previous_outputs.length)?`<details><summary>Previous output</summary>${e.previous_outputs.map(renderFile).join('')}</details>`:'';
  const prevFb=e.previous_feedback?`<div class="lbl" style="margin-top:.6rem">Previous feedback</div><pre>${esc(e.previous_feedback)}</pre>`:'';
  host.innerHTML=`
   <div class="nav"><button id="prev">&larr; Prev</button><div>${idx+1} / ${EVALS.length} &middot; <b>${esc(e.eval_name)}</b></div><button id="next">Next &rarr;</button></div>
   <div class="card"><div class="lbl">Prompt</div><pre>${esc(e.prompt)}</pre></div>
   <div class="card"><div class="lbl">Output</div>${e.outputs.length?e.outputs.map(renderFile).join(''):'<i>no output files were saved</i>'}${prevOut}${grades}</div>
   <div class="card"><div class="lbl">Feedback</div><textarea id="fb" placeholder="What would you change? Leave blank if it looks good.">${esc(fb[e.run_id])}</textarea>${prevFb}</div>`;
  document.getElementById('prev').onclick=()=>{save();idx=(idx-1+EVALS.length)%EVALS.length;renderOutputs();};
  document.getElementById('next').onclick=()=>{save();idx=(idx+1)%EVALS.length;renderOutputs();};
  document.getElementById('fb').oninput=ev=>{fb[EVALS[idx].run_id]=ev.target.value;};
}
function renderBench(){
  const el=document.getElementById('benchmark');
  if(!BENCH||!BENCH.configurations){el.innerHTML='<div class="card">No benchmark.json provided.</div>';return;}
  const f=v=>(v&&v.mean!=null)?`${v.mean} &plusmn; ${v.stddev}`:'&mdash;';
  let h=`<div class="card"><div class="lbl">Summary</div><table><tr><th>Config</th><th>Pass rate</th><th>Time (s)</th><th>Tokens</th><th>Evals</th></tr>`;
  for(const c of BENCH.configurations)h+=`<tr><td>${esc(c.name)}</td><td>${f(c.pass_rate)}</td><td>${f(c.time_seconds)}</td><td>${f(c.total_tokens)}</td><td>${c.n_evals}</td></tr>`;
  h+='</table>';
  if(BENCH.delta&&BENCH.delta.baseline)h+=`<p>Delta (with_skill &minus; ${esc(BENCH.delta.baseline)}): pass ${BENCH.delta.pass_rate}, time ${BENCH.delta.time_seconds}s, tokens ${BENCH.delta.total_tokens}</p>`;
  h+='</div>';
  if((BENCH.observations||[]).length)h+=`<div class="card"><div class="lbl">Observations</div><ul>${BENCH.observations.map(o=>`<li>${esc(o)}</li>`).join('')}</ul></div>`;
  el.innerHTML=h;
}
document.querySelectorAll('.tab').forEach(t=>t.onclick=()=>{
  document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('on',x===t));
  const o=t.dataset.tab==='outputs';
  document.getElementById('outputs').style.display=o?'':'none';
  document.getElementById('benchmark').style.display=o?'none':'';
});
document.onkeydown=ev=>{if((document.activeElement||{}).tagName==='TEXTAREA')return;
  if(ev.key==='ArrowLeft'){const p=document.getElementById('prev');if(p)p.click();}
  if(ev.key==='ArrowRight'){const n=document.getElementById('next');if(n)n.click();}};
document.getElementById('submit').onclick=async()=>{
  save();
  const payload={reviews:EVALS.map(e=>({run_id:e.run_id,feedback:fb[e.run_id]||'',timestamp:new Date().toISOString()})),status:'complete'};
  if(SERVER){try{await fetch('/feedback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});alert('Saved feedback.json — you can return to the chat.');}catch(e){alert('Save failed: '+e);}}
  else{const b=new Blob([JSON.stringify(payload,null,2)],{type:'application/json'});const a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='feedback.json';a.click();alert('Downloaded feedback.json — drop it into the iteration folder.');}
};
renderOutputs();renderBench();
</script></body></html>"""


def build_html(iteration_dir, skill_name, benchmark, prev_dir, server_mode) -> str:
    evals = collect(iteration_dir, prev_dir)
    bench = {}
    if benchmark and Path(benchmark).exists():
        try:
            bench = json.loads(Path(benchmark).read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            bench = {}
    # `</` is escaped so a `</script>` inside any embedded output can't break out of the <script> tag.
    return (PAGE
            .replace("__SKILL__", (skill_name or "skill").replace("<", "&lt;"))
            .replace("/*EVALS*/", json.dumps(evals).replace("</", "<\\/"))
            .replace("/*BENCH*/", json.dumps(bench).replace("</", "<\\/"))
            .replace("/*SERVER*/", "true" if server_mode else "false"))


def serve(html: str, iteration_dir: Path, port: int = 0):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(html.encode("utf-8"))

        def do_POST(self):
            if self.path != "/feedback":
                self.send_response(404)
                self.end_headers()
                return
            n = int(self.headers.get("Content-Length", 0))
            (iteration_dir / "feedback.json").write_bytes(self.rfile.read(n))
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"ok")

    srv = HTTPServer(("127.0.0.1", port), Handler)
    url = f"http://127.0.0.1:{srv.server_port}/"
    print(f"Review viewer at {url}  (feedback.json will be written to {iteration_dir})")
    try:
        webbrowser.open(url)
    except Exception:
        pass
    srv.serve_forever()


def main():
    ap = argparse.ArgumentParser(description="Generate or serve the skill review viewer.")
    ap.add_argument("iteration_dir", type=Path)
    ap.add_argument("--skill-name", default="skill")
    ap.add_argument("--benchmark", type=Path, default=None)
    ap.add_argument("--previous-workspace", type=Path, default=None)
    ap.add_argument("--static", type=Path, default=None)
    ap.add_argument("--port", type=int, default=0)
    args = ap.parse_args()

    if not args.iteration_dir.is_dir():
        raise SystemExit(f"Not a directory: {args.iteration_dir}")

    html = build_html(args.iteration_dir, args.skill_name, args.benchmark,
                      args.previous_workspace, server_mode=args.static is None)
    if args.static:
        args.static.write_text(html, encoding="utf-8")
        print(f"Wrote standalone viewer to {args.static}")
    else:
        serve(html, args.iteration_dir, args.port)


if __name__ == "__main__":
    main()

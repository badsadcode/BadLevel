"""Build the BadLevel devlog: the story so far, one "map" per post.

    python scripts/build_devlog.py site/devlog

Posts are HTML fragments in devlog/posts/NN-slug.html, each starting with a comment header:

    <!--
    map: E1M3
    title: Noor's verdict
    date: 2026-11-12
    time: Day 1, evening          (the intermission card at the end of the post)
    secrets: ...
    kills: ...
    secret: true                  (optional: the secret level, styled apart and listed last)
    -->

Images live in devlog/img/ (copied along); showcase pictures are linked from ../showcase/,
which the site already publishes. Static, no dependencies, no cost.
"""
from __future__ import annotations

import html
import os
import re
import shutil
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "devlog")


def read_posts(root: str = ROOT):
    posts = []
    for name in sorted(os.listdir(os.path.join(root, "posts"))):
        if not name.endswith(".html"):
            continue
        text = open(os.path.join(root, "posts", name), encoding="utf-8").read()
        m = re.match(r"\s*<!--(.*?)-->(.*)", text, re.S)
        if not m:
            raise SystemExit(f"{name}: no header comment")
        meta = {}
        for line in m.group(1).strip().splitlines():
            k, _, v = line.partition(":")
            meta[k.strip()] = v.strip()
        for k in ("map", "title", "date"):
            if k not in meta:
                raise SystemExit(f"{name}: header needs '{k}'")
        meta["slug"] = re.sub(r"[^a-z0-9]+", "", meta.get("map", "").lower()) or \
            re.sub(r"[^a-z0-9]+", "-", name[:-5].lower()).strip("-")     # #e1m7: a link to a map
        meta["body"] = m.group(2).strip()
        meta["secret"] = meta.get("secret", "").lower() == "true"
        posts.append(meta)
    return posts


def intermission(p) -> str:
    rows = [(k.capitalize(), p[k]) for k in ("time", "secrets", "kills") if p.get(k)]
    if not rows:
        return ""
    cells = "".join(f'<dt>{html.escape(k)}</dt><dd>{html.escape(v)}</dd>' for k, v in rows)
    return f'<aside class="inter"><div class="plate">Completed</div><dl>{cells}</dl></aside>'


def build(out: str) -> str:
    posts = read_posts()
    os.makedirs(out, exist_ok=True)
    img = os.path.join(ROOT, "img")
    if os.path.isdir(img):
        shutil.copytree(img, os.path.join(out, "img"), dirs_exist_ok=True)
    story = [p for p in posts if not p["secret"]]
    secret = [p for p in posts if p["secret"]]
    nav = "".join(f'<a href="#{p["slug"]}"><b>{html.escape(p["map"])}</b> {html.escape(p["title"])}</a>'
                  for p in story)
    arts = []
    for p in story + secret:
        cls = "post secret" if p["secret"] else "post"
        arts.append(f'''<article class="{cls}" id="{p["slug"]}">
<header><span class="badge">{html.escape(p["map"])}</span><div><h2>{html.escape(p["title"])}</h2>
<time datetime="{html.escape(p["date"])}">{html.escape(p["date"])}</time></div></header>
<div class="body">{p["body"]}</div>
{intermission(p)}
</article>''')
    latest = story[-1] if story else None
    page = PAGE.replace("__NAV__", nav).replace("__POSTS__", "\n".join(arts)) \
        .replace("__LATEST__", f'<a class="latest" href="#{latest["slug"]}">Latest: {html.escape(latest["map"])} '
                               f'{html.escape(latest["title"])} &darr;</a>' if latest else "") \
        .replace("__SECRET__", f'<a class="found" href="#{secret[0]["slug"]}">A secret area is nearby&hellip;</a>'
                               if secret else "")
    path = os.path.join(out, "index.html")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(page)
    print(f"devlog -> {path} ({len(story)} posts{', 1 secret' if secret else ''})")
    return path


PAGE = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BadLevel Devlog</title>
<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Metamorphous&family=Inter:wght@400;600&family=IBM+Plex+Mono:wght@400;600&display=swap" rel="stylesheet">
<style>
:root { --bg:#120d0a; --panel:#1d1612; --panel2:#251b15; --line:#3d2b1f; --rust:#c0622d; --rust2:#e08a4a;
  --bone:#eadcc2; --muted:#a8957a; --blood:#8e2a1c; --gold:#d9a441; }
* { box-sizing:border-box; }
html { scroll-behavior:smooth; }
body { margin:0; background:var(--bg); color:var(--bone); font:16px/1.65 Inter, system-ui, sans-serif;
  background-image: radial-gradient(ellipse at top, #2a1a10 0%, transparent 60%),
    repeating-linear-gradient(0deg, rgba(255,255,255,.015) 0 1px, transparent 1px 32px),
    repeating-linear-gradient(90deg, rgba(255,255,255,.015) 0 1px, transparent 1px 32px); }
a { color:var(--rust2); }
.wrap { max-width:860px; margin:0 auto; padding:0 16px 80px; }
.top { text-align:center; padding:56px 0 24px; }
.top h1 { font:400 clamp(44px, 9vw, 84px)/1 Metamorphous, serif; margin:0; letter-spacing:2px;
  color:var(--bone); text-shadow:0 2px 0 #000, 0 0 28px rgba(192,98,45,.35); }
.top h1 span { color:var(--rust); }
.top p { color:var(--muted); margin:10px 0 0; }
.top .home { font-size:14px; }
.latest, .found { display:inline-block; margin-top:16px; font:600 13px 'IBM Plex Mono', monospace; text-decoration:none;
  border:1px solid var(--line); padding:6px 12px; border-radius:4px; background:var(--panel); }
.found { color:var(--muted); border-style:dashed; }
nav.episodes { display:grid; grid-template-columns:repeat(auto-fill, minmax(230px, 1fr)); gap:6px; margin:28px 0 8px; }
nav.episodes a { display:block; text-decoration:none; color:var(--bone); background:var(--panel); border:1px solid var(--line);
  padding:8px 10px; border-radius:4px; font-size:14px; }
nav.episodes a b { color:var(--rust); font:600 13px 'IBM Plex Mono', monospace; margin-right:6px; }
nav.episodes a:hover { border-color:var(--rust); }
.post { background:linear-gradient(180deg, var(--panel2), var(--panel)); border:1px solid var(--line); border-radius:6px;
  margin:34px 0; padding:22px 24px; box-shadow:0 10px 30px rgba(0,0,0,.45), inset 0 1px 0 rgba(255,255,255,.04);
  position:relative; }
.post::before, .post::after { content:""; position:absolute; top:10px; width:7px; height:7px; border-radius:50%;
  background:radial-gradient(circle at 35% 35%, #7a6450, #2a1d15); }
.post::before { left:10px; } .post::after { right:10px; }
.post header { display:flex; gap:16px; align-items:center; margin-bottom:8px; }
.badge { font:600 15px 'IBM Plex Mono', monospace; color:#1a0f08; background:linear-gradient(180deg, var(--rust2), var(--rust));
  padding:8px 10px; border-radius:3px; box-shadow:inset 0 -2px 0 rgba(0,0,0,.3); white-space:nowrap; }
.post h2 { font:400 28px/1.15 Metamorphous, serif; margin:0; color:var(--bone); }
.post time { color:var(--muted); font:13px 'IBM Plex Mono', monospace; }
.body p, .body ul { margin:12px 0; }
.body li { margin:4px 0; }
.body b { color:#fff2da; }
.body code, .body pre { font:14px 'IBM Plex Mono', monospace; background:#0d0907; border:1px solid var(--line); border-radius:3px; }
.body code { padding:1px 5px; }
.body pre { padding:10px 12px; overflow-x:auto; color:var(--gold); }
blockquote { margin:18px 0; padding:14px 18px; border-left:3px solid var(--rust); background:#0f0a08; font-size:19px;
  font-style:italic; color:#fff2da; }
blockquote cite { display:block; margin-top:6px; font:600 13px 'IBM Plex Mono', monospace; color:var(--rust2); font-style:normal; }
blockquote cite::before { content:"— "; }
figure { margin:16px 0; }
figure img { width:100%; height:auto; display:block; border-radius:4px; border:1px solid var(--line); background:#000; }
figure.small img { width:auto; max-width:100%; margin:0 auto; }
figure.small { text-align:center; }
figcaption { font-size:13.5px; color:var(--muted); margin-top:6px; }
.pair { display:grid; grid-template-columns:1fr 1fr; gap:12px; }
.pair figure { margin:12px 0; }
.inter { margin:22px auto 4px; max-width:420px; background:#0d0907; border:1px solid var(--line); border-radius:4px;
  padding:14px 18px 10px; }
.plate { font:400 22px Metamorphous, serif; text-align:center; color:#2b1609; letter-spacing:2px;
  background:linear-gradient(180deg, #a5643b, #7a4426); border:1px solid #4b2a17; border-radius:3px; padding:4px 0;
  margin:-30px auto 12px; width:200px; box-shadow:0 3px 0 #000; text-shadow:0 1px 0 rgba(255,255,255,.2); }
.inter dl { display:grid; grid-template-columns:auto 1fr; gap:4px 16px; margin:0; }
.inter dt { font:400 17px Metamorphous, serif; color:var(--rust2); }
.inter dd { margin:0; font:14px 'IBM Plex Mono', monospace; color:var(--bone); align-self:center; }
.secret { border-color:#5a1c12; background:linear-gradient(180deg, #24100c, #160a08); box-shadow:0 0 40px rgba(142,42,28,.25); }
.secret .badge { background:linear-gradient(180deg, #b53a26, var(--blood)); color:#fff1e6; }
footer { text-align:center; color:var(--muted); font-size:13px; margin-top:40px; }
@media (max-width:620px) { .pair { grid-template-columns:1fr; } .post { padding:18px 16px; } .post h2 { font-size:23px; } }
</style></head><body><div class="wrap">
<div class="top">
<h1>Bad<span>Level</span></h1>
<p>Devlog: how a procedural FPS level generator learned architecture, one map at a time.</p>
<p class="home"><a href="../index.html">&larr; back to the live site</a> (levels rebuilt from the newest code)</p>
__LATEST__
</div>
<nav class="episodes">__NAV__</nav>
__POSTS__
<div style="text-align:center">__SECRET__</div>
<footer>Made by BadSad, Noor and Sage. Grey boxes forever.</footer>
</div></body></html>
"""


if __name__ == "__main__":
    build(sys.argv[1] if len(sys.argv) > 1 else "site/devlog")

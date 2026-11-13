// Screenshots for the architectural showcase: an overview and rule-chosen player-height views.
//   node scripts/showcase_shots.js showcase
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

(async () => {
  const dir = path.resolve(process.argv[2] || 'showcase');
  const index = JSON.parse(fs.readFileSync(path.join(dir, 'showcase.json'), 'utf8'));
  const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  const p = await b.newPage({ viewport: { width: 1280, height: 720 } });
  for (const e of index) {
    await p.goto('file://' + path.join(dir, e.stem + '.html'));
    await p.waitForFunction(() => window.__ready === true, null, { timeout: 60000 });
    await p.addStyleTag({ content: 'aside,.hud,.help,.tip{display:none!important} .wrap{grid-template-columns:1fr!important}' });
    await p.evaluate(() => { window.dispatchEvent(new Event('resize')); if (window.__analysis) window.__analysis.setMode('off'); });
    await p.waitForTimeout(500);
    // overview: the preview's own framing, ceilings off
    await p.screenshot({ path: path.join(dir, e.stem + '_overview.jpg'), type: 'jpeg', quality: 82 });
    // player-height views: ceilings on, wider lens like a game camera
    await p.evaluate(() => { const v = window.__view; v.camera.fov = 75; v.camera.updateProjectionMatrix();
      v.root.traverse(o => { if (o.isMesh && /bl_(ceiling|sky)/.test(o.material.name)) o.visible = true; }); });
    for (const s of e.shots) {
      if (process.env.ONLY && !process.env.ONLY.split(',').includes(s.name)) continue;
      // a cutaway shot hides the ceilings (and the sky) for that one picture
      await p.evaluate((cut) => { window.__view.root.traverse(o => {
        if (o.isMesh && /bl_(ceiling|sky)/.test(o.material.name)) o.visible = !cut; }); }, !!s.cutaway);
      await p.evaluate(([eye, target]) => {
        const v = window.__view; v.controls.target.set(...target); v.camera.position.set(...eye); v.controls.update();
      }, [s.eye, s.target]);
      await p.waitForTimeout(400);
      await p.screenshot({ path: path.join(dir, `${e.stem}_${s.name}.jpg`), type: 'jpeg', quality: 82 });
    }
    console.log(e.stem, 'shots:', e.shots.length + 1);
  }
  await b.close();
})();

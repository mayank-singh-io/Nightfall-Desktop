// Live illustrated scenes for the new tab page (the same scenes and animations as the Moon Clock).
const NightScene = (() => {
  const KEYS = ["river", "galaxy", "city", "raincity", "snowcabin", "ocean", "desert", "rooftop", "fireflies", "aurora", "train", "village", "room", "space", "porch", "meadow", "falls", "lakedawn"];
  const NAMES = ["River Night", "Galaxy Peaks", "City Below", "Rain over the City", "Snowy Cabin", "Ocean Shore", "Desert Night", "Rooftop Rain", "Firefly Lake", "Northern Lights", "Steam Train", "Snowy Village", "Cozy Room", "Earth from Space", "Porch in the Forest", "Moonlit Meadow", "Wide Falls", "Mountain Lake at Dawn"];

  // ---- soft glows, cached by colour
  const glowCache = new Map();
  function glowSprite(r, g, b) {
    const key = (r << 16) | (g << 8) | b;
    let c = glowCache.get(key);
    if (!c) {
      c = document.createElement("canvas"); c.width = c.height = 64;
      const x = c.getContext("2d"), gr = x.createRadialGradient(32, 32, 0, 32, 32, 32);
      for (let i = 0; i <= 10; i++) { const d = i / 10; gr.addColorStop(d, `rgba(${r | 0},${g | 0},${b | 0},${Math.pow(1 - d, 2.2).toFixed(4)})`); }
      x.fillStyle = gr; x.fillRect(0, 0, 64, 64); glowCache.set(key, c);
    }
    return c;
  }
  function glow(x, y, radius, r, g, b, strength) {
    if (!(strength > 0.01) || !(radius >= 0.5)) return;
    ctx.globalAlpha = Math.min(1, strength); ctx.drawImage(glowSprite(r, g, b), x - radius, y - radius, radius * 2, radius * 2); ctx.globalAlpha = 1;
  }

  // ---- sunrise and sunset from date and location (no internet needed)
  function sunTimes(lat, lon, day) {
    const start = new Date(day.getFullYear(), 0, 0), n = Math.floor((day - start) / 86400000), rad = Math.PI / 180;
    const decl = -23.44 * Math.cos((2 * Math.PI / 365) * (n + 10)) * rad;
    let cosH = (Math.cos(90.833 * rad) - Math.sin(lat * rad) * Math.sin(decl)) / (Math.cos(lat * rad) * Math.cos(decl));
    cosH = Math.max(-1, Math.min(1, cosH));
    const h = Math.acos(cosH) / rad / 15, b = (2 * Math.PI * (n - 81)) / 364;
    const eot = (9.87 * Math.sin(2 * b) - 7.53 * Math.cos(b) - 1.5 * Math.sin(b)) / 60, tz = -day.getTimezoneOffset() / 60;
    const noon = 12 - lon / 15 - eot + tz, mid = new Date(day); mid.setHours(0, 0, 0, 0);
    return { rise: new Date(+mid + (noon - h) * 3600000), set: new Date(+mid + (noon + h) * 3600000) };
  }
  const bump = (m, a, b, c, d) => { if (m <= a || m >= d) return 0; const f = m < b ? (m - a) / (b - a) : m <= c ? 1 : (d - m) / (d - c); return f * f * (3 - 2 * f); };
  function dawnAmount(now = new Date()) {
    const mode = opts.timeOfDay || "sunrise";
    if (mode === "night") return 0; if (mode === "dawn") return 1;
    const lat = typeof opts.lat === "number" ? opts.lat : 23, lon = typeof opts.lon === "number" ? opts.lon : (-now.getTimezoneOffset() / 60) * 15;
    const { rise, set } = sunTimes(lat, lon, now);
    let f = bump((now - rise) / 60000, -80, -20, 45, 110);
    if (mode === "both") f = Math.max(f, bump((now - set) / 60000, -100, -35, 20, 70));
    return f;
  }
  let dawnAmt = 0, dawnTarget = 0, lastDawnCheck = -1e9;   // colours ease toward the target over about 8 seconds
  let opts = { trainGap: 180, roomView: "random", weather: "clear", stars: true, lights: true, water: true, parallax: true };
  let fallStreaks = [], nextPlane = 0;
  let train = null, glass = [], movers = [], nextSat = 0, nextDebris = 0, nextAstro = 0, parX = 0, parY = 0, parTX = 0, parTY = 0;
  let lightOff = [], windowOff = [];

  function resolve(key) {
    if (key !== "room") return key;
    let v = opts.roomView;
    if (v === "weather") { if (/rain|storm/.test(opts.weather)) return "room-city"; if (/snow/.test(opts.weather)) return "room-snow"; v = "random"; }
    if (v === "random") { const all = ["room-city", "room-lake", "room-snow", "room-sea"]; return all[Math.floor(Math.random() * all.length)]; }
    return "room-" + v;
  }

  let canvas, ctx, d = null, imgs = {}, active = false;
  let sc = 1, ox = 0, oy = 0, w = 0, h = 0;
  let tw = [], phase = [], parts = [], streaks = [], drops = [], toggles = [], birds = [], flies = [];
  let nextShot = 0, lastSmoke = 0, lastSand = 0, lastPuddle = 0;
  const R = Math.random;

  const loadImg = (src) => new Promise((ok, fail) => { const i = new Image(); i.onload = () => ok(i); i.onerror = fail; i.src = src; });

  function parse(text) {
    const s = { rain: "none", snow: "none", noshoot: false, lifeunder: false, sprites: [], spritesmoke: [], spriteglow: [], spritewheel: [], pulse: [], glassdrops: [], water: [], curtain: [], satellite: 0, debris: 0, astronaut: 0, skyline: [], layers: [], reflects: [], fades: [], falls: [], sways: [], plane: 0, shots: 0, nodawn: false, dawnFile: null, dawnFront: null, front: null,
      star: [], light: [], shimmer: [], ripple: [], window: [], smoke: [], waves: [], bird: [], bob: [], sand: [], toggle: [], beacon: [], puddle: [], fireflies: [] };
    for (const raw of text.split("\n")) {
      const p = raw.trim().split(" ");
      if (!p[0]) continue;
      const nums = () => p.slice(1).map(Number);
      switch (p[0]) {
        case "rain": s.rain = p[1]; break;
        case "snow": s.snow = p[1]; break;
        case "noshoot": s.noshoot = true; break;
        case "clock": break;
        case "lifeunder": s.lifeunder = true; break;
        case "sprite": s.sprites.push({ file: p[2], y: +p[3], speed: +p[4], gap: +p[5] }); break;
        case "spritesmoke": case "spriteglow": case "spritewheel": s[p[0]].push(p.slice(2).map(Number)); break;
        case "satellite": case "debris": case "astronaut": case "plane": case "shots": s[p[0]] = +p[1]; break;
        case "nodawn": s.nodawn = true; break;
        case "dawn": s.dawnFile = p[1]; break;
        case "dawnfront": s.dawnFront = p[1]; break;
        case "fall": s.falls.push(p.slice(1).map(Number)); break;
        case "sway": s.sways.push({ file: p[1], y: +p[2], amp: +p[3] }); break;
        case "skyline": s.skyline = p.slice(1).map((xy) => xy.split(",").map(Number)); break;
        case "layer": s.layers.push({ file: p[1], y: +p[2], speed: +p[3] }); break;
        case "fade": case "fadeafter": s.fades.push({ files: p[1].split(","), y: +p[2], period: +p[3], after: p[0] === "fadeafter" }); break;
        case "reflect": s.reflects.push({ file: p[1], y: +p[2], speed: +p[3] }); break;
        case "front": s.front = p[1]; break;
        default: if (s[p[0]]) s[p[0]].push(nums());
      }
    }
    return s;
  }

  const M = (x, y) => [ox + x * sc, oy + y * sc];

  async function start(key, cvs, options) {
    canvas = cvs; ctx = canvas.getContext("2d");
    if (options) Object.assign(opts, options);
    key = resolve(key);
    // load everything for the new scene first; the old one keeps playing until it's ready
    const text = await (await fetch(`scenes/${key}.txt`)).text();
    const nd = parse(text);
    const ni = { bg: await loadImg(`scenes/${key}.jpg`) };
    for (const L of [...nd.layers, ...nd.reflects]) L.img = await loadImg(`scenes/${L.file}`);
    for (const F of nd.fades) F.imgs = await Promise.all(F.files.map((f) => loadImg(`scenes/${f}`)));
    if (nd.front) ni.front = await loadImg(`scenes/${nd.front}`);
    if (nd.dawnFile && !nd.nodawn) ni.dawn = await loadImg(`scenes/${nd.dawnFile}`).catch(() => null);
    if (nd.dawnFront) ni.dawnFront = await loadImg(`scenes/${nd.dawnFront}`).catch(() => null);
    for (const S of nd.sways) S.img = await loadImg(`scenes/${S.file}`);
    let nt = null;
    for (const sp of nd.sprites) nt = { img: await loadImg(`scenes/${sp.file}`), y: sp.y, speed: sp.speed, gap: sp.gap, x: 0, active: false, next: performance.now() + 1500 };
    active = false;
    d = nd; imgs = ni; train = nt; parts = []; streaks = []; movers = [];
    resize();
    active = true;
    return true;
  }

  function resize() {
    if (!d) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    w = innerWidth; h = innerHeight;
    canvas.width = w * dpr; canvas.height = h * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    sc = Math.max(w / 1920, h / 1080) * 1.02; ox = (w - 1920 * sc) / 2; oy = (h - 1080 * sc) / 2;
    lightOff = d.light.map(() => false); windowOff = d.window.map(() => false);
    d.pulse.forEach((pl) => { pl.on = pl.on === undefined ? true : pl.on; });
    glass = []; for (const g of d.glassdrops) for (let i = 0; i < g[4]; i++) { const [x1, y1] = M(g[0], g[1]), [x2, y2] = M(g[2], g[3]); glass.push({ box: [x1, y1, x2, y2], x: x1 + R() * (x2 - x1), y: y1 + R() * (y2 - y1), v: 0, ph: R() * 6 }); }
    fallStreaks = []; d.falls.forEach((fl, fi) => { for (let i = 0; i < Math.max(6, fl[4] * 0.35); i++) fallStreaks.push({ f: fi, t: R(), off: R() - 0.5, sp: 0.45 + R() * 0.35, len: 0.12 + R() * 0.18 }); });
    lastDawnCheck = -1e9; dawnTarget = dawnAmt = imgs.dawn ? dawnAmount() : 0;
    const now = performance.now(); nextPlane = now + 5000 + R() * 8000; nextSat = now + 3000 + R() * 4000; nextDebris = now + 12000 + R() * 10000; nextAstro = now + 20000 + R() * 20000;
    tw = d.star.map((s) => { const [x, y] = M(s[0], s[1]); return { x, y, r: s[2], ph: R() * 6.28, sp: 0.6 + R() * 1.2 }; });
    phase = Array.from({ length: 512 }, () => R() * 6.28);
    toggles = d.toggle.map((t) => ({ on: t[10] > 0, next: 2000 + R() * 18000 }));
    birds = []; for (const b of d.bird) for (let i = 0; i < b[4]; i++) { const [x1, y1] = M(b[0], b[1]), [x2, y2] = M(b[2], b[3]); birds.push({ box: [x1, y1, x2, y2], x: x1 + R() * (x2 - x1), y: y1 + R() * (y2 - y1), v: (18 + R() * 22) * (R() < 0.5 ? -1 : 1), ph: R() * 6 }); }
    flies = []; for (const f of d.fireflies) for (let i = 0; i < f[4]; i++) { const [x1, y1] = M(f[0], f[1]), [x2, y2] = M(f[2], f[3]); flies.push({ box: [x1, y1, x2, y2], x: x1 + R() * (x2 - x1), y: y1 + R() * (y2 - y1), vx: 0, vy: 0, ph: R() * 6 }); }
    const area = w * h, kind = d.rain !== "none" ? 1 : d.snow !== "none" ? 2 : 0;
    const n = kind === 1 ? area / (d.rain === "heavy" ? 6000 : 12000) : kind === 2 ? area / (d.snow === "heavy" ? 3500 : 6000) : 0;
    drops = Array.from({ length: Math.round(n) }, () => ({ k: kind, x: R() * w, y: R() * h, len: 10 + R() * 10, sp: kind === 1 ? 650 + R() * 350 : 22 + R() * 40, ph: R() * 6, r: 1 + R() * 1.8 }));
    nextShot = performance.now() + 4000 + R() * 6000;
  }

  function addStreak(start, shower) {
    if (d.noshoot) return;
    let x = w * (0.35 + R() * 0.6), y = h * (0.04 + R() * 0.13), vx = -(240 + R() * 180), vy = 90 + R() * 70;
    if (shower) { const a = 0.3 + R() * 2.5, s = 240 + R() * 200; vx = Math.cos(a) * s; vy = Math.abs(Math.sin(a)) * s; }
    streaks.push({ x, y, vx, vy, start, dur: 800 + R() * 400 });
  }
  function shower(t) { for (let i = 0; i < 12; i++) addStreak(t + R() * 3000, true); }

  function step(t, dt, allowShots) {
    for (const p of drops) {
      p.y += p.sp * dt;
      if (p.k === 1) p.x -= p.sp * dt * 0.12; else p.x += Math.sin(t / 1200 + p.ph) * 14 * dt;
      if (p.y > h) { p.y = -p.len; p.x = R() * w * 1.1; }
    }
    if (d.smoke.length && t - lastSmoke > 240) {
      lastSmoke = t;
      for (const s of d.smoke) { const [x, y] = M(s[0], s[1]); parts.push({ k: 0, x: x + (R() - 0.5) * 4, y, vx: s[3] * (0.6 + R() * 0.8), vy: -(16 + R() * 12), size: s[2] * sc * (0.8 + R() * 0.4), age: 0, life: 5 + R() * 2, seed: R() * 6 }); }
    }
    if (d.sand.length && t - lastSand > 90) {
      lastSand = t; const s = d.sand[Math.floor(R() * d.sand.length)], [x, y] = M(s[0], s[1]);
      for (let i = 0; i < 3; i++) parts.push({ k: 1, x: x + R() * 30, y: y - R() * 3, vx: 22 + R() * 26, vy: -(3 + R() * 8), size: 0.8 + R() * 1.2, age: 0, life: 2 + R() * 2 });
    }
    if (d.puddle.length && t - lastPuddle > 260) {
      lastPuddle = t; const s = d.puddle[Math.floor(R() * d.puddle.length)], [x, y] = M(s[0] + (R() - 0.5) * s[2], s[1]);
      parts.push({ k: 2, x, y, vx: 0, vy: 0, size: s[2] * sc * 0.35, age: 0, life: 1.1 });
    }
    parts = parts.filter((q) => (q.age += dt) < q.life);   // click ripples start slightly delayed (negative age)
    for (const q of parts) { q.x += q.vx * dt + (q.k === 0 ? Math.sin(q.age * 1.3 + q.seed) * 5 * dt : 0); q.y += q.vy * dt; }
    toggles.forEach((tg) => { if (t > tg.next) { tg.on = !tg.on; tg.next = t + 4000 + R() * 22000; } });
    for (const b of birds) {
      b.x += b.v * dt; b.y += Math.sin(t / 1500 + b.ph) * 4 * dt;
      if (b.x < b.box[0] - 40) b.x = b.box[2] + 30; if (b.x > b.box[2] + 40) b.x = b.box[0] - 30;
    }
    for (const f of flies) {
      f.vx = Math.max(-18, Math.min(18, f.vx + (R() - 0.5) * 40 * dt)); f.vy = Math.max(-18, Math.min(18, f.vy + (R() - 0.5) * 40 * dt));
      f.x += f.vx * dt; f.y += f.vy * dt;
      if (f.x < f.box[0] || f.x > f.box[2]) f.vx = -f.vx; if (f.y < f.box[1] || f.y > f.box[3]) f.vy = -f.vy;
      f.x = Math.max(f.box[0], Math.min(f.box[2], f.x)); f.y = Math.max(f.box[1], Math.min(f.box[3], f.y));
    }
    if (train) {
      const gap = opts.trainGap >= 0 ? opts.trainGap : train.gap, tw = train.img.width * sc;
      if (!train.active && t >= train.next) { train.active = true; train.x = train.speed < 0 ? w + 20 : -tw - 20; }
      if (train.active) {
        train.x += train.speed * sc * dt;
        if ((train.speed < 0 && train.x < -tw - 40) || (train.speed > 0 && train.x > w + 40)) { train.active = false; train.next = t + gap * 1000; }
        if (t - lastSmoke > 120) for (const sm of d.spritesmoke) parts.push({ k: 0, x: train.x + sm[0] * sc, y: oy + (train.y + sm[1]) * sc, vx: (R() - 0.5) * 10, vy: -(28 + R() * 20), size: sm[2] * sc * (0.8 + R() * 0.5), age: 0, life: 3.5 + R() * 1.5, seed: R() * 6 });
      }
    }
    for (const g of glass) {
      if (g.v <= 0 && R() < 0.01) g.v = 20 + R() * 60;
      g.y += g.v * dt; g.x += Math.sin(t / 300 + g.ph) * 6 * dt;
      if (g.v > 0 && R() < 0.02) g.v = 0;
      if (g.y > g.box[3]) { g.y = g.box[1] + R() * (g.box[3] - g.box[1]) * 0.3; g.x = g.box[0] + R() * (g.box[2] - g.box[0]); g.v = 0; }
    }
    const addMover = (kind, speedMin, speedVar, yMin, yVar) => { const ltr = R() < 0.5; movers.push({ kind, x: ltr ? -80 : w + 80, y: h * (yMin + R() * yVar), vx: (ltr ? 1 : -1) * (speedMin + R() * speedVar), vy: (R() - 0.5) * 8, spin: R() * 0.6 - 0.3 }); };
    if (t - lastDawnCheck > 1000) { lastDawnCheck = t; dawnTarget = imgs.dawn ? dawnAmount() : 0; }
    if (dawnAmt !== dawnTarget) { const by = dt / 8; dawnAmt = Math.abs(dawnTarget - dawnAmt) <= by ? dawnTarget : dawnAmt + Math.sign(dawnTarget - dawnAmt) * by; }
    for (const st of fallStreaks) { st.t += st.sp * dt; if (st.t > 1 + st.len) { st.t = -st.len * R(); st.off = R() - 0.5; } }
    if (d.plane && t > nextPlane) { nextPlane = t + d.plane * (0.6 + R() * 0.8); const ltr = R() < 0.5; movers.push({ kind: 3, x: ltr ? -20 : w + 20, y: h * (0.06 + R() * 0.16), vx: (ltr ? 1 : -1) * (22 + R() * 14), vy: (R() - 0.5) * 3, spin: 0 }); }
    if (d.satellite && t > nextSat) { nextSat = t + d.satellite * (0.6 + R() * 0.8); addMover(0, 40, 40, 0.05, 0.35); }
    if (d.debris && t > nextDebris) { nextDebris = t + d.debris * (0.6 + R() * 0.8); addMover(1, 25, 25, 0.1, 0.4); }
    if (d.astronaut && t > nextAstro) { nextAstro = t + d.astronaut * (0.7 + R() * 0.6); addMover(2, 16, 10, 0.2, 0.3); }
    movers = movers.filter((m) => { m.x += m.vx * dt; m.y += m.vy * dt; return m.x > -200 && m.x < w + 200; });
    parX += (parTX - parX) * Math.min(1, dt * 3); parY += (parTY - parY) * Math.min(1, dt * 3);
    if (allowShots && !d.noshoot && dawnAmt < 0.4 && t > nextShot) { addStreak(t, false); nextShot = t + (d.shots ? d.shots * (0.6 + R() * 0.8) : 7000 + R() * 9000); }
    streaks = streaks.filter((s) => t < s.start + s.dur);
  }

  function skyClip() {
    ctx.beginPath(); ctx.moveTo(0, 0);
    for (const [x, y] of d.skyline) { const p = M(x, y); ctx.lineTo(p[0], p[1]); }
    ctx.lineTo(w, d.skyline.length ? M(0, d.skyline[d.skyline.length - 1][1])[1] : h / 2); ctx.lineTo(w, 0); ctx.closePath(); ctx.clip();
  }

  function fade(F, t) {
    const n = F.imgs.length, pos = (t / 1000 / Math.max(1, F.period)) % n, i = Math.floor(pos), j = (i + 1) % n;
    let a = pos - i; a = a * a * (3 - 2 * a);   // ease in and out
    const bw = F.imgs[0].width * sc, bh = F.imgs[0].height * sc, y = oy + F.y * sc;
    ctx.globalAlpha = 1 - a; ctx.drawImage(F.imgs[i], ox, y, bw, bh);
    ctx.globalAlpha = a; ctx.drawImage(F.imgs[j], ox, y, bw, bh);
    ctx.globalAlpha = 1;
  }

  function drift(L, t) {
    const bw = L.img.width * sc, bh = L.img.height * sc, off = ((t / 1000) * L.speed) % bw, y = oy + L.y * sc;
    ctx.drawImage(L.img, ox - off, y, bw, bh);
    if (ox - off + bw < w) ctx.drawImage(L.img, ox - off + bw, y, bw, bh);
  }

  function draw(t, shimmerStart) {
    ctx.globalAlpha = 1;
    ctx.save(); ctx.translate(parX, parY);
    ctx.drawImage(imgs.bg, ox, oy, 1920 * sc, 1080 * sc);
    if (imgs.dawn && dawnAmt > 0.01) { ctx.globalAlpha = dawnAmt; ctx.drawImage(imgs.dawn, ox, oy, 1920 * sc, 1080 * sc); ctx.globalAlpha = 1; }
    ctx.save(); skyClip();
    const since = t - shimmerStart;
    for (const s of tw) {
      const boost = since >= 0 && since < 7000 ? Math.sin((Math.PI * since) / 7000) * (0.5 + 0.5 * Math.sin(t / 160 + s.ph * 3)) : 0;
      ctx.globalAlpha = Math.max(0, (1 - dawnAmt) * Math.min(1, 0.55 + 0.45 * Math.sin((t / 1000) * s.sp + s.ph) + boost * 0.6));
      ctx.fillStyle = "#ece4f4"; ctx.beginPath(); ctx.arc(s.x, s.y, s.r, 0, 6.29); ctx.fill();
    }
    ctx.restore(); ctx.globalAlpha = 1;
    for (const L of d.layers) drift(L, t);
    if (dawnAmt < 0.7) for (const F of d.fades) if (!F.after) fade(F, t);
    ctx.save(); skyClip();
    for (const s of streaks) {
      if (t < s.start) continue;
      const k = (t - s.start) / s.dur, hx = s.x + s.vx * k, hy = s.y + s.vy * k, tx = hx - s.vx * 0.28, ty = hy - s.vy * 0.28;
      const g = ctx.createLinearGradient(hx, hy, tx, ty); g.addColorStop(0, "rgba(255,255,255,0.95)"); g.addColorStop(1, "rgba(236,228,244,0)");
      ctx.globalAlpha = Math.sin(k * Math.PI); ctx.strokeStyle = g; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.moveTo(hx, hy); ctx.lineTo(tx, ty); ctx.stroke();
    }
    ctx.restore(); ctx.globalAlpha = 1;
    drawTrain(t); drawMovers(t);
    if (d.lifeunder) { life(t, 0); drawDrops(); }
    drawFalls(t); drawSways(t);
    if (imgs.front) {
      ctx.drawImage(imgs.front, ox, oy, 1920 * sc, 1080 * sc);
      if (imgs.dawnFront && dawnAmt > 0.01) { ctx.globalAlpha = dawnAmt; ctx.drawImage(imgs.dawnFront, ox, oy, 1920 * sc, 1080 * sc); ctx.globalAlpha = 1; }
    }
    for (const L of d.reflects) drift(L, t);
    if (dawnAmt < 0.7) for (const F of d.fades) if (F.after) fade(F, t);
    if (d.lifeunder) life(t, 1); else { life(t, 2); drawDrops(); }
    ctx.restore(); ctx.globalAlpha = 1;
  }

  function drawDrops() {
    if (drops.length) {
      if (drops[0].k === 1) { ctx.strokeStyle = "rgba(205,198,228,0.32)"; ctx.lineWidth = 1; ctx.beginPath(); for (const p of drops) { ctx.moveTo(p.x, p.y); ctx.lineTo(p.x + p.len * 0.12, p.y - p.len); } ctx.stroke(); }
      else { ctx.fillStyle = "rgba(242,238,250,0.82)"; ctx.beginPath(); for (const p of drops) { ctx.moveTo(p.x + p.r, p.y); ctx.arc(p.x, p.y, p.r, 0, 6.29); } ctx.fill(); }
    }
  }

  function drawFalls(t) {
    for (const st of fallStreaks) {
      const fl = d.falls[st.f], t0 = Math.max(0, st.t - st.len), t1 = Math.min(1, st.t);
      if (t1 <= t0) continue;
      const width = fl[4] * sc, [ax, ay] = M(fl[0] + (fl[2] - fl[0]) * t0, fl[1] + (fl[3] - fl[1]) * t0), [bx, by] = M(fl[0] + (fl[2] - fl[0]) * t1, fl[1] + (fl[3] - fl[1]) * t1);
      const off = st.off * width * (0.8 + 0.25 * t1), al = Math.sin(Math.PI * Math.min(1, Math.max(0, (t0 + t1) / 2))) * (1 - Math.abs(st.off) * 1.2);
      if (al <= 0.02) continue;
      const gr = ctx.createLinearGradient(ax + off, ay, bx + off, by); gr.addColorStop(0, "rgba(240,242,255,0)"); gr.addColorStop(1, `rgba(240,242,255,${(0.59 * al).toFixed(3)})`);
      ctx.strokeStyle = gr; ctx.lineWidth = Math.max(1, 1.6 * sc); ctx.beginPath(); ctx.moveTo(ax + off, ay); ctx.lineTo(bx + off, by); ctx.stroke();
    }
    d.falls.forEach((fl, i) => {
      const [bx, by] = M(fl[2], fl[3]);
      for (let k = 0; k < 3; k++) glow(bx + Math.sin(t / 230 + i * 3 + k * 2.1) * fl[4] * sc * 0.4, by + Math.sin(t / 310 + k) * 3 * sc, fl[4] * sc * 0.55, 238, 240, 252, 0.35 + 0.15 * Math.sin(t / 400 + i + k));
    });
  }

  function drawSways(t) {
    for (const S of d.sways) {
      const iw = S.img.width, ih = S.img.height, rows = 72, rh = Math.max(1, Math.floor(ih / rows)), y0 = oy + S.y * sc;
      for (let i = 0; i * rh < ih; i++) {
        const up = 1 - i / rows, dx = (Math.sin(t / 1500 + i * 0.09) * 0.6 + Math.sin(t / 2300 + i * 0.05 + 1.3) * 0.4) * S.amp * sc * (0.25 + 0.75 * up), hh = Math.min(rh, ih - i * rh);
        ctx.drawImage(S.img, 0, i * rh, iw, hh, ox + dx, y0 + i * rh * sc, iw * sc, hh * sc + 0.5);
      }
    }
  }

  function drawTrain(t) {
    if (!train || !train.active) return;
    const tw = train.img.width * sc, th = train.img.height * sc, ty = oy + train.y * sc;
    ctx.drawImage(train.img, train.x, ty, tw, th);
    for (const wl of d.spritewheel) {
      const cx = train.x + wl[0] * sc, cy = ty + wl[1] * sc, r = wl[2] * sc, ang = -train.x / Math.max(1, r);
      ctx.strokeStyle = "rgba(70,56,60,0.8)"; ctx.lineWidth = Math.max(1, 2 * sc);
      for (let k = 0; k < 3; k++) { const a = ang + k * 1.047; ctx.beginPath(); ctx.moveTo(cx - Math.cos(a) * r * 0.85, cy - Math.sin(a) * r * 0.85); ctx.lineTo(cx + Math.cos(a) * r * 0.85, cy + Math.sin(a) * r * 0.85); ctx.stroke(); }
    }
    for (const gl of d.spriteglow) {
      const cx = train.x + gl[0] * sc, cy = ty + gl[1] * sc, r = gl[5] * sc, f = 0.7 + 0.2 * Math.sin(t / 90 + gl[0]) + 0.1 * Math.sin(t / 37);
      const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r); g.addColorStop(0, rgba(gl[2], gl[3], gl[4], (150 * f) / 255)); g.addColorStop(1, rgba(gl[2], gl[3], gl[4], 0));
      ctx.fillStyle = g; ctx.fillRect(cx - r, cy - r, r * 2, r * 2);
    }
  }

  function drawMovers(t) {
    for (const m of movers) {
      if (m.kind === 3) { glow(m.x, m.y, 7, 255, 250, 240, 0.7); if (t % 1300 < 160) glow(m.x - 6, m.y + 1, 9, 255, 70, 70, 0.9); continue; }
      if (m.kind === 0) { const a = 0.6 + 0.4 * Math.pow(Math.max(0, Math.sin(t / 2500 + m.x / 300)), 8); ctx.fillStyle = rgba(236, 236, 250, a); ctx.beginPath(); ctx.arc(m.x, m.y, 1.6, 0, 6.29); ctx.fill(); }
      else if (m.kind === 1) { const a = (t / 1000) * m.spin * 2; ctx.fillStyle = "rgba(150,150,170,0.67)"; ctx.beginPath(); ctx.moveTo(m.x + Math.cos(a) * 4, m.y + Math.sin(a) * 4); ctx.lineTo(m.x + Math.cos(a + 2.3) * 2.4, m.y + Math.sin(a + 2.3) * 2.4); ctx.lineTo(m.x + Math.cos(a + 3.9) * 3.2, m.y + Math.sin(a + 3.9) * 3.2); ctx.fill(); }
      else {
        ctx.save(); ctx.translate(m.x, m.y); ctx.rotate(((t / 1000) * m.spin * 20 * Math.PI) / 180); const u = 1.6;
        ctx.fillStyle = "#d2d4de"; ctx.fillRect(-9 * u, -6 * u, 9 * u, 16 * u);
        ctx.fillStyle = "#ececf4"; ctx.beginPath(); ctx.ellipse(5 * u, -9 * u, 7 * u, 7 * u, 0, 0, 6.29); ctx.fill();
        ctx.fillStyle = "#c8963c"; ctx.beginPath(); ctx.ellipse(6.5 * u, -9 * u, 4.5 * u, 4 * u, 0, 0, 6.29); ctx.fill();
        ctx.fillStyle = "#ececf4"; ctx.fillRect(-1 * u, -3 * u, 13 * u, 16 * u); ctx.fillRect(10 * u, -1 * u, 9 * u, 4 * u); ctx.fillRect(-1 * u, 13 * u, 5 * u, 11 * u); ctx.fillRect(7 * u, 13 * u, 5 * u, 10 * u);
        ctx.restore();
      }
    }
  }

  function inside(t) {
    for (const pl of d.pulse) {
      if (!pl.on) continue;
      const [x, y] = M(pl[0], pl[1]), r = 140 * sc, f = 0.8 + 0.2 * Math.sin(t / 1800 + pl[0]);
      const g = ctx.createRadialGradient(x, y, 0, x, y, r); g.addColorStop(0, rgba(pl[2], pl[3], pl[4], ((pl[5] || 80) * f) / 255)); g.addColorStop(1, rgba(pl[2], pl[3], pl[4], 0));
      ctx.fillStyle = g; ctx.fillRect(x - r, y - r, r * 2, r * 2);
    }
    if (d.lifeunder) for (const q of parts) {
      if (q.k !== 0) continue;
      const lf = q.age / q.life, s = q.size * (1 + q.age * 0.55);
      ctx.fillStyle = rgba(214, 204, 226, ((55 * (1 - lf)) / 255) * Math.min(1, q.age * 2)); ctx.beginPath(); ctx.ellipse(q.x, q.y, s, s * 0.8, 0, 0, 6.29); ctx.fill();
    }
    for (const g of glass) {
      ctx.fillStyle = "rgba(224,216,244,0.43)"; ctx.beginPath(); ctx.ellipse(g.x, g.y, 2.6, 3.4, 0, 0, 6.29); ctx.fill();
      if (g.v > 0) { ctx.strokeStyle = "rgba(224,216,244,0.2)"; ctx.lineWidth = 1.2; ctx.beginPath(); ctx.moveTo(g.x, g.y - 2.6); ctx.lineTo(g.x - Math.sin(t / 300 + g.ph) * 2, g.y - 26); ctx.stroke(); }
    }
  }

  const rgba = (r, g, b, a) => `rgba(${r | 0},${g | 0},${b | 0},${Math.max(0, Math.min(1, a))})`;

  function life(t, sel) {
    if (sel !== 0) inside(t);
    if (sel === 1) return;
    let k = 0;
    for (let li = 0; li < d.light.length; li++) {
      const L = d.light[li];
      if (lightOff[li]) { k++; continue; }
      const ph = phase[k++ % 512], f = 0.5 + 0.5 * Math.sin(t / (700 + ph * 90) + ph), [x, y] = M(L[0], L[1]), r = Math.max(1, L[2] * sc) * (1.2 + 0.4 * f);
      glow(x, y, r * 4.5, L[3], L[4], L[5], 0.25 + 0.3 * f); glow(x, y, r * 1.2, 255, 240, 220, 0.6 + 0.4 * f);
    }
    ctx.lineWidth = Math.max(1, sc);
    for (const S of d.shimmer) {
      const ph = phase[k++ % 512], f = Math.pow(Math.max(0, Math.sin(t / 650 + ph)), 3);
      if (f < 0.03) continue;
      const [ax, ay] = M(S[0], S[2]), [cx] = M(S[1], S[2]), dr = Math.sin(t / 1300 + ph) * 6 * sc;
      const mid = (ax + cx) / 2 + dr, half = ((cx - ax) / 2) * (0.55 + 0.45 * f);
      if (half < 2) continue;
      const g = ctx.createLinearGradient(mid - half, ay, mid + half, ay), c = rgba(232, 222, 245, (70 * f) / 255);
      g.addColorStop(0, rgba(232, 222, 245, 0)); g.addColorStop(0.5, c); g.addColorStop(1, rgba(232, 222, 245, 0));
      ctx.fillStyle = g; ctx.fillRect(mid - half, ay - 0.6 * sc, half * 2, Math.max(1, 1.2 * sc));
    }
    for (const Rp of d.ripple) {
      const ph = phase[k++ % 512], f = 0.5 + 0.5 * Math.sin(t / 520 + ph), [x, y] = M(Rp[0], Rp[1]), wob = Math.sin(t / 380 + ph * 2) * 2.5 * sc, len = Rp[2] * sc;
      const gr = ctx.createLinearGradient(x, y, x, y + len); gr.addColorStop(0, rgba(Rp[3], Rp[4], Rp[5], 0)); gr.addColorStop(0.45, rgba(Rp[3], Rp[4], Rp[5], (18 + 34 * f) / 255)); gr.addColorStop(1, rgba(Rp[3], Rp[4], Rp[5], 0));
      ctx.fillStyle = gr; ctx.fillRect(x + wob - 1.2 * sc, y, 2.4 * sc, len);
    }
    for (let wi = 0; wi < d.window.length; wi++) {
      const Wn = d.window[wi];
      if (windowOff[wi]) { const [x, y] = M(Wn[0], Wn[1]); ctx.fillStyle = "rgb(44,30,36)"; ctx.fillRect(x, y, Wn[2] * sc, Wn[3] * sc); k++; continue; }
      const ph = phase[k++ % 512], f = 0.6 + 0.25 * Math.sin(t / 230 + ph) + 0.15 * Math.sin(t / 97 + ph * 3), [x, y] = M(Wn[0], Wn[1]), ww = Wn[2] * sc, hh = Wn[3] * sc;
      const g = ctx.createRadialGradient(x + ww / 2, y + hh / 2, 0, x + ww / 2, y + hh / 2, Math.max(ww, hh) * 3);
      g.addColorStop(0, rgba(255, 170, 90, (70 * f) / 255)); g.addColorStop(1, rgba(255, 170, 90, 0));
      ctx.fillStyle = g; ctx.fillRect(x - ww * 3, y - hh * 3, ww * 7, hh * 7);
      ctx.fillStyle = rgba(255, 196, 115, (150 + 105 * Math.min(1, f)) / 255); ctx.fillRect(x, y, ww, hh);
    }
    d.toggle.forEach((T, i) => { const [x, y] = M(T[0], T[1]); ctx.fillStyle = toggles[i].on ? rgba(T[4], T[5], T[6], 1) : rgba(T[7], T[8], T[9], 1); ctx.fillRect(x, y, T[2] * sc, T[3] * sc); });
    for (const B of d.beacon) {
      const cyc = (t + B[0] * 7) % 1800; if (cyc > 260) continue;
      const a = Math.sin((Math.PI * cyc) / 260), [x, y] = M(B[0], B[1]), r = 6 * sc;
      glow(x, y, r * 6, 255, 60, 60, 0.45 * a); glow(x, y, r * 1.6, 255, 120, 110, a);
    }
    for (const q of parts) {
      const lf = q.age / q.life;
      if (q.k === 0 && d.lifeunder) continue;
      if (q.k === 3) { if (q.age < 0) continue; const s = q.size * (0.2 + lf * 1.4); ctx.strokeStyle = rgba(230, 222, 245, (150 * (1 - lf)) / 255); ctx.lineWidth = 1.5; ctx.beginPath(); ctx.ellipse(q.x, q.y, s, s * 0.3, 0, 0, 6.29); ctx.stroke(); continue; }
      if (q.k === 0) { const s = q.size * (1 + q.age * 0.55); ctx.fillStyle = rgba(196, 186, 214, ((55 * (1 - lf)) / 255) * Math.min(1, q.age * 2)); ctx.beginPath(); ctx.ellipse(q.x, q.y, s, s * 0.8, 0, 0, 6.29); ctx.fill(); }
      else if (q.k === 1) { ctx.fillStyle = rgba(214, 170, 170, (120 * (1 - lf)) / 255); ctx.beginPath(); ctx.arc(q.x, q.y, q.size, 0, 6.29); ctx.fill(); }
      else { if (q.age < 0) continue; const s = q.size * (0.3 + lf); ctx.strokeStyle = rgba(255, 190, 150, (120 * (1 - lf)) / 255); ctx.beginPath(); ctx.ellipse(q.x, q.y, s, s * 0.22, 0, 0, 6.29); ctx.stroke(); }
    }
    for (const f of flies) {
      const a = Math.max(0, Math.sin(t / 700 + f.ph * 3)); if (a < 0.05) continue;
      glow(f.x, f.y, 15, 200, 255, 120, 0.4 * a); glow(f.x, f.y, 3.5, 240, 255, 190, a);
    }
    ctx.strokeStyle = "rgb(20,12,24)"; ctx.lineWidth = 2;
    for (const b of birds) {
      const s = 11, fl = Math.sin(t / 160 + b.ph) * s * 0.35;
      ctx.beginPath(); ctx.moveTo(b.x - s, b.y - fl); ctx.quadraticCurveTo(b.x - s * 0.4, b.y - s * 0.05, b.x, b.y + s * 0.15); ctx.quadraticCurveTo(b.x + s * 0.4, b.y - s * 0.05, b.x + s, b.y - fl); ctx.stroke();
    }
    for (const B of d.bob) {
      const [x, y0] = M(B[0], B[1]), y = y0 + Math.sin(t / 900) * 1.5 * sc;
      glow(x, y, 13, B[2], B[3], B[4], 0.35); glow(x, y, 3.3, 255, 236, 200, 0.95);
      for (let j = 0; j < 7; j++) glow(x + Math.sin(t / 400 + j) * 2.5, y + 7 + j * 6, 3.2 - j * 0.25, B[2], B[3], B[4], (0.45 - j * 0.05) * (0.6 + 0.4 * Math.sin(t / 300 + j)));
    }
    d.curtain.forEach((C, ci) => {
      const [x, y] = M(C[0], C[1]), f = 0.5 + 0.5 * Math.sin(t / 900 + ci * 0.35) * Math.sin(t / 2300 + ci * 0.11), hh = C[2] * sc * (0.75 + 0.35 * f);
      const g = ctx.createLinearGradient(x, y, x, y - hh); g.addColorStop(0, rgba(C[3], C[4], C[5], (150 * f) / 255)); g.addColorStop(1, rgba(C[3], C[4], C[5], 0));
      ctx.strokeStyle = g; ctx.lineWidth = 5 * sc; ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + Math.sin(t / 1700 + ci) * 6 * sc, y - hh); ctx.stroke();
    });
    for (const Wv of d.waves) {
      const x1 = M(Wv[0], 0)[0], x2 = M(Wv[1], 0)[0], y = M(0, Wv[2])[1], amp = Wv[3] * sc, count = Wv[4];
      for (let line = 0; line < count; line++) {
        const cyc = (t / 6200 + line / count) % 1, prog = 1 - Math.pow(1 - cyc, 2);
        const yy = y - line * 12 * sc - amp * 1.2 + prog * amp * 3.6, life = Math.sin(cyc * Math.PI) * (1 - line * 0.22);
        if (life < 0.04) continue;
        const N = 48, edge = [];
        for (let i = 0; i < N; i++) { const xx = x1 + ((x2 - x1) * i) / (N - 1); edge.push([xx, yy + (Math.sin(xx / (150 * sc) + line * 1.3 + t / 3400) * 6 + Math.sin(xx / (53 * sc) + line) * 2.5) * sc]); }
        ctx.fillStyle = rgba(200, 196, 230, (28 * life) / 255); ctx.beginPath();
        edge.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
        for (let i = N - 1; i >= 0; i--) ctx.lineTo(edge[i][0], edge[i][1] - amp * 1.4);
        ctx.fill();
        for (let i = 0; i < N - 1; i++) {
          const n = 0.5 + 0.5 * Math.sin(i * 1.7 + line * 4.1 + Math.floor(cyc * 3) * 2.3), a = life * n;
          if (a < 0.12) continue;
          ctx.strokeStyle = rgba(240, 236, 252, (170 * a) / 255); ctx.lineWidth = Math.max(1, 2.2 - line * 0.4);
          ctx.beginPath(); ctx.moveTo(edge[i][0], edge[i][1]); ctx.lineTo(edge[i + 1][0], edge[i + 1][1]); ctx.stroke();
          glow((edge[i][0] + edge[i + 1][0]) / 2, (edge[i][1] + edge[i + 1][1]) / 2 + 2 * sc, 16 * sc, 236, 232, 250, 0.18 * a);
        }
      }
    }
  }

  function pointer(nx, ny) { if (opts.parallax) { parTX = -nx * 12; parTY = -ny * 7; } else { parTX = parTY = 0; } }

  function inSky(x, y) {
    const pts = [[0, 0], ...d.skyline.map(([a, b]) => M(a, b)), [w, M(0, d.skyline.length ? d.skyline[d.skyline.length - 1][1] : 540)[1]], [w, 0]];
    let c = false;
    for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) { const [xi, yi] = pts[i], [xj, yj] = pts[j]; if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) c = !c; }
    return c;
  }

  function click(x, y) {
    if (!d) return false;
    const t = performance.now(); x -= parX; y -= parY;
    if (opts.lights) {
      for (let i = 0; i < d.toggle.length; i++) { const T = d.toggle[i], [px, py] = M(T[0], T[1]); if (x > px - 6 && x < px + T[2] * sc + 6 && y > py - 6 && y < py + T[3] * sc + 6) { toggles[i].on = !toggles[i].on; toggles[i].next = t + 30000 + R() * 30000; return true; } }
      for (let i = 0; i < d.window.length; i++) { const Wn = d.window[i], [px, py] = M(Wn[0], Wn[1]); if (x > px - 10 && x < px + Wn[2] * sc + 10 && y > py - 10 && y < py + Wn[3] * sc + 10) { windowOff[i] = !windowOff[i]; return true; } }
      for (let i = 0; i < d.light.length; i++) { const [px, py] = M(d.light[i][0], d.light[i][1]); if (Math.abs(px - x) < 18 && Math.abs(py - y) < 18) { lightOff[i] = !lightOff[i]; return true; } }
      for (const pl of d.pulse) { const [px, py] = M(pl[0], pl[1]); if (Math.abs(px - x) < 60 && Math.abs(py - y) < 60) { pl.on = !pl.on; return true; } }
    }
    if (opts.water) for (const pd of d.puddle) {   // puddles ripple too
      const [cx, cy] = M(pd[0], pd[1]), rw = pd[2] * sc;
      if (Math.abs(x - cx) < rw && Math.abs(y - cy) < rw * 0.3) { for (let k = 0; k < 2; k++) parts.push({ k: 2, x, y, vx: 0, vy: 0, size: rw * (0.5 + k * 0.3), age: -k * 0.2, life: 1.2 }); return true; }
    }
    if (opts.water) for (const wr of d.water) {
      const [x1, y1] = M(wr[0], wr[1]), [x2, y2] = M(wr[2], wr[3]);
      if (x > x1 && x < x2 && y > y1 && y < y2) { for (let k = 0; k < 3; k++) parts.push({ k: 3, x, y, vx: 0, vy: 0, size: 40 + k * 25, age: -k * 0.25, life: 1.6 + k * 0.4 }); return true; }
    }
    if (opts.stars && !d.noshoot && inSky(x, y)) { streaks.push({ x, y, vx: -(260 + R() * 160), vy: 100 + R() * 60, start: t, dur: 900 }); return true; }
    return false;
  }

  return {
    KEYS, NAMES, start, resize, step, draw, shower, click, pointer,
    setOptions(o) { Object.assign(opts, o); if (!opts.parallax) parTX = parTY = 0; lastDawnCheck = -1e9; },
    get active() { return active; },
    get dawnShown() { return dawnAmt; },
    stop() { active = false; d = null; if (ctx) ctx.clearRect(0, 0, canvas.width, canvas.height); }
  };
})();

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
  let opts = { trainGap: 180, roomView: "random", weather: "clear", stars: true, lights: true, water: true, parallax: true,
    hFireflies: true, hStars: true, hCat: true, hWater: true, hGrassSnow: true, hBirds: true, hLamps: true,
    trailStyle: "dust", trailLength: "linger", realWeather: "clear", moonPhase: -1, festival: null, cMeteorHold: true, cMoon: true, cTrain: true, cCatMug: true, cCatch: true, cDraw: true, cWeather: true, surprises: true, moonInfo: null, surpriseEvery: 0 };
  let hx = 0, hy = 0, hoverOn = false, pDown = false, showerFired = false, pdx = 0, pdy = 0, pDownAt = 0, lastClickT = -1e9, lastClickX = 0, lastClickY = 0;
  let fest = null, horizonY = 0, bursts = [], lanterns = [], diyas = null, holiPuffs = null, holiSpecks = null, nextBurst = 0, nextLantern = 0;
  const FEST_COLS = [[255, 200, 130], [205, 180, 255], [255, 170, 215], [170, 220, 255], [255, 222, 170]], HOLI_COLS = [[255, 80, 160], [255, 200, 60], [80, 190, 255], [130, 230, 100], [190, 110, 255]];
  let rw = [], rwKind = 0, rwFor = "", clouds = [], cloudImg = null, nextFlash = 0, flashAt = -1e9, moonShade = null;
  let ignoreClicksUntil = -1e9, moonCardAt = -1e9, moonCard = null, wishAt = -1e9, wishX = 0, wishY = 0, catReactAt = -1e9, weatherUntil = -1e9, weatherFrom = 0, temp = [], tempKind = 0;
  let nextSurprise = 0, nextHoverRipple = 0, drawing = [], drawings = [], sceneKey = "";
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
    const s = { rain: "none", snow: "none", noshoot: false, lifeunder: false, sprites: [], spritesmoke: [], spriteglow: [], spritewheel: [], pulse: [], glassdrops: [], water: [], curtain: [], satellite: 0, debris: 0, astronaut: 0, skyline: [], layers: [], reflects: [], fades: [], falls: [], sways: [], cat: [], moon: [], surprises: [], plane: 0, shots: 0, nodawn: false, dawnFile: null, dawnFront: null, front: null,
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
        case "surprise": s.surprises.push(p.slice(1)); break;
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
    key = resolve(key); sceneKey = key;
    // load everything for the new scene first; the old one keeps playing until it's ready
    const text = await (await fetch(`${opts.base || ""}scenes/${key}.txt`)).text();
    const nd = parse(text);
    const ni = { bg: await loadImg(`${opts.base || ""}scenes/${key}.jpg`) };
    for (const L of [...nd.layers, ...nd.reflects]) L.img = await loadImg(`${opts.base || ""}scenes/${L.file}`);
    for (const F of nd.fades) F.imgs = await Promise.all(F.files.map((f) => loadImg(`${opts.base || ""}scenes/${f}`)));
    if (nd.front) ni.front = await loadImg(`${opts.base || ""}scenes/${nd.front}`);
    if (nd.dawnFile && !nd.nodawn) ni.dawn = await loadImg(`${opts.base || ""}scenes/${nd.dawnFile}`).catch(() => null);
    if (nd.dawnFront) ni.dawnFront = await loadImg(`${opts.base || ""}scenes/${nd.dawnFront}`).catch(() => null);
    for (const S of nd.sways) S.img = await loadImg(`${opts.base || ""}scenes/${S.file}`);
    let nt = null;
    for (const sp of nd.sprites) nt = { img: await loadImg(`${opts.base || ""}scenes/${sp.file}`), y: sp.y, speed: sp.speed, gap: sp.gap, x: 0, active: false, next: performance.now() + 1500 };
    active = false;
    d = nd; imgs = ni; train = nt; parts = []; streaks = []; movers = [];
    resize();
    active = true;
    return true;
  }

  function resize(fixedW, fixedH) {   // a fixed size is only used for saving a picture
    if (!d) return;
    if (typeof fixedW !== "number") fixedW = fixedH = 0;
    const dpr = fixedW ? 1 : Math.min(window.devicePixelRatio || 1, 2);
    w = fixedW || innerWidth; h = fixedH || innerHeight;
    canvas.width = w * dpr; canvas.height = h * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    sc = Math.max(w / 1920, h / 1080) * 1.02; ox = (w - 1920 * sc) / 2; oy = (h - 1080 * sc) / 2;
    lightOff = d.light.map(() => false); windowOff = d.window.map(() => false);
    d.pulse.forEach((pl) => { pl.on = pl.on === undefined ? true : pl.on; });
    glass = []; for (const g of d.glassdrops) for (let i = 0; i < g[4]; i++) { const [x1, y1] = M(g[0], g[1]), [x2, y2] = M(g[2], g[3]); glass.push({ box: [x1, y1, x2, y2], x: x1 + R() * (x2 - x1), y: y1 + R() * (y2 - y1), v: 0, ph: R() * 6 }); }
    fallStreaks = []; d.falls.forEach((fl, fi) => { for (let i = 0; i < Math.max(6, fl[4] * 0.35); i++) fallStreaks.push({ f: fi, t: R(), off: R() - 0.5, sp: 0.45 + R() * 0.35, len: 0.12 + R() * 0.18 }); });
    lastDawnCheck = -1e9; dawnTarget = dawnAmt = imgs.dawn ? dawnAmount() : 0;
    nextSurprise = performance.now() + (opts.surpriseEvery || (3 + R() * 6) * 60000); drawings = []; drawing = []; temp = []; rwFor = ""; moonShade = null; fest = undefined;
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
    for (const q of parts) { if (q.k === 8) q.vy += 70 * dt; q.x += q.vx * dt + (q.k === 0 ? Math.sin(q.age * 1.3 + q.seed) * 5 * dt : 0); q.y += q.vy * dt; }
    toggles.forEach((tg) => { if (t > tg.next) { tg.on = !tg.on; tg.next = t + 4000 + R() * 22000; } });
    for (const b of birds) {
      b.x += b.v * dt; b.y += Math.sin(t / 1500 + b.ph) * 4 * dt;
      if (b.x < b.box[0] - 40) b.x = b.box[2] + 30; if (b.x > b.box[2] + 40) b.x = b.box[0] - 30;
    }
    for (const f of flies) {
      const lim = hoverOn && opts.hFireflies ? 60 : 18;
      f.vx = Math.max(-lim, Math.min(lim, f.vx + (R() - 0.5) * 40 * dt)); f.vy = Math.max(-lim, Math.min(lim, f.vy + (R() - 0.5) * 40 * dt));
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
    movers = movers.filter((m) => { m.x += m.vx * dt; m.y += m.vy * dt; return m.x > -300 && m.x < w + 300 && (!m.life || t - m.born < m.life); });
    parX += (parTX - parX) * Math.min(1, dt * 3); parY += (parTY - parY) * Math.min(1, dt * 3);
    const ownWeather = d.rain !== "none" || d.snow !== "none" || sceneKey === "space" || d.lifeunder, want = ownWeather ? "clear" : opts.realWeather || "clear";
    if (want !== rwFor) {
      rwFor = want; rwKind = want === "rain" || want === "storm" ? 1 : want === "snow" ? 2 : 0;
      const n = rwKind ? Math.round((w * h) / (rwKind === 2 ? 6000 : want === "storm" ? 5000 : 8500)) : 0;
      rw = Array.from({ length: n }, () => ({ x: R() * w, y: R() * h, len: 10 + R() * 10, sp: rwKind === 1 ? 650 + R() * 350 : 24 + R() * 36, ph: R() * 6, r: 1 + R() * 1.6 }));
      clouds = ["cloudy", "rain", "storm"].includes(want) ? Array.from({ length: 7 }, () => ({ x: R() * w, y: h * (0.02 + R() * 0.3), s: 1.1 + R() * 1.1, v: 5 + R() * 7 })) : [];
    }
    for (const p of rw) { p.y += p.sp * dt; if (rwKind === 1) p.x -= p.sp * dt * 0.12; else p.x += Math.sin(t / 1200 + p.ph) * 14 * dt; if (p.y > h) { p.y = -p.len; p.x = R() * w * 1.1; }
      if (hoverOn && opts.hGrassSnow && rwKind === 2) { const dx = p.x - hx, dy = p.y - hy, dd = Math.hypot(dx, dy); if (dd < 150 && dd > 1) { const f = (1 - dd / 150) * 140 * dt; p.x += (-dy / dd + (dx / dd) * 0.5) * f; p.y += (dx / dd + (dy / dd) * 0.5) * f; } } }
    for (const c of clouds) { c.x += c.v * dt; if (c.x > w + 300) c.x = -640 * c.s; }
    if (rwFor === "storm" && t > nextFlash) { if (nextFlash > 0) flashAt = t; nextFlash = t + 8000 + R() * 14000; }
    if ((opts.festival || null) !== fest) {   // set up a special day
      fest = opts.festival || null; bursts = []; lanterns = []; diyas = null; holiPuffs = null; holiSpecks = null; nextBurst = 0; nextLantern = 0;
      let deepest = 0; for (const [, y] of d.skyline) deepest = Math.max(deepest, y);
      horizonY = Math.max(h * 0.3, Math.min(h * 0.8, (oy + deepest * sc) * 0.88));
      if (fest === "diwali" && d.water.length) {
        diyas = Array.from({ length: 24 }, () => { const wr = d.water[Math.floor(R() * d.water.length)], [ax, ay] = M(wr[0], wr[1]), [cx, cy] = M(wr[2], wr[3]); return { x: ax + R() * (cx - ax), y: ay + Math.pow(R(), 0.8) * (cy - ay) * 0.85 + 6, ph: R() * 6, dr: (R() - 0.5) * 5 }; }).sort((a, b) => a.y - b.y);
      }
      if (fest === "holi") {
        holiPuffs = []; for (let c = 0; c < 9; c++) { const cx = R() * w, cy = h * (0.08 + R() * 0.34), v = 6 + R() * 8, col = Math.floor(R() * 5); for (let j = 0; j < 12; j++) holiPuffs.push({ x: cx + (R() - 0.5) * 140, y: cy + (R() - 0.5) * 40, r: 26 + R() * 30, v, col }); }
        holiSpecks = Array.from({ length: 140 }, () => ({ x: R() * w, y: R() * h, sp: 10 + R() * 18, ph: R() * 6, col: Math.floor(R() * 5), r: 1.2 + R() * 1.4 }));
      }
    }
    if (fest === "newyear" && t > nextBurst && bursts.length < 6) {
      nextBurst = t + 1400 + R() * 2600; const pick = R(), type = pick < 0.4 ? 0 : pick < 0.75 ? 1 : 2, n = type === 1 ? 26 : type === 2 ? 20 : 28;
      bursts.push({ type, start: t, x: w * (0.12 + R() * 0.76), y: type === 2 ? horizonY - (20 + R() * 50) : h * (0.1 + R() * 0.25), r: type === 2 ? 12 + R() * 10 : type === 1 ? 50 + R() * 20 : 30 + R() * 16,
        col: type === 1 ? [255, 205, 120] : FEST_COLS[Math.floor(R() * 5)], ang: Array.from({ length: n }, (_, i) => (i * Math.PI * 2) / n + (R() - 0.5) * 0.08) });
    }
    bursts = bursts.filter((b) => t - b.start < 4200);
    if ((fest === "diwali" || fest === "birthday") && t > nextLantern && lanterns.length < 40) {
      nextLantern = t + (fest === "diwali" ? 700 : 1300) * (0.6 + R() * 0.8);
      lanterns.push({ x: w * (0.04 + R() * 0.92), y: horizonY + R() * 30, vy: -(10 + R() * 12), s: 9 + R() * 7, ph: R() * 6 });
    }
    lanterns = lanterns.filter((l) => { l.y += l.vy * dt; l.x += Math.sin(t / 1800 + l.ph) * 6 * dt; return l.y > -40; });
    if (holiPuffs) for (const p of holiPuffs) { p.x += p.v * dt; if (p.x > w + p.r * 2) p.x = -p.r * 2; }
    if (holiSpecks) for (const p of holiSpecks) { p.y += p.sp * dt; p.x += Math.sin(t / 900 + p.ph) * 10 * dt; if (p.y > h) { p.y = -5; p.x = R() * w; } }
    if (pDown && opts.cMeteorHold && !showerFired && t - pDownAt > 700 && drawing.length < 2 && Math.abs(hx - pdx) < 14 && Math.abs(hy - pdy) < 14 && inSky(pdx, pdy)) { showerFired = true; showerAt(pdx, pdy, t); }
    if (hoverOn) {
      if (opts.hFireflies) for (const f of flies) { const dx = hx - f.x, dy = hy - f.y, dd = Math.hypot(dx, dy); if (dd < 560 && dd > 8) { const pull = (1 - dd / 560) * 150 + 30; f.vx += (dx / dd) * pull * dt; f.vy += (dy / dd) * pull * dt; } }
      if (opts.hBirds) for (const b of birds) if (Math.abs(b.x - hx) < 190 && Math.abs(b.y - hy) < 150) { b.v = (b.x >= hx ? 1 : -1) * Math.max(Math.abs(b.v), 110); b.y -= 50 * dt; }
      if (opts.hGrassSnow) { const pool = drops.length && drops[0].k === 2 ? drops : tempKind === 2 && t < weatherUntil ? temp : null;
        if (pool) for (const p of pool) { const dx = p.x - hx, dy = p.y - hy, dd = Math.hypot(dx, dy); if (dd < 150 && dd > 1) { const f = (1 - dd / 150) * 140 * dt; p.x += (-dy / dd + (dx / dd) * 0.5) * f; p.y += (dx / dd + (dy / dd) * 0.5) * f; } } }
      if (opts.hWater && t > nextHoverRipple && overWater(hx, hy)) { nextHoverRipple = t + 170; parts.push({ k: 3, x: hx, y: hy, vx: 0, vy: 0, size: 26, age: 0, life: 1.4 }); }
    }
    for (const b of birds) if (Math.abs(b.v) > 30) b.v *= Math.pow(0.6, dt);
    if (t < weatherUntil) for (const p of temp) { p.y += p.sp * dt; if (tempKind === 1) p.x -= p.sp * dt * 0.12; else p.x += Math.sin(t / 1200 + p.ph) * 14 * dt; if (p.y > h) { p.y = -p.len; p.x = R() * w * 1.1; } }
    if (opts.surprises && d.surprises.length && t > nextSurprise) { spawnSurprise(t); nextSurprise = t + (opts.surpriseEvery || (15 + R() * 25) * 60000); }
    if (allowShots && !d.noshoot && dawnAmt < 0.4 && t > nextShot) { addStreak(t, false); nextShot = t + (d.shots ? d.shots * (0.6 + R() * 0.8) : 7000 + R() * 9000); }
    if (opts.trailStyle !== "none") {   // sparkling trails behind shooting stars
      const life = opts.trailLength === "brief" ? 1 : opts.trailLength === "long" ? 6 : 3, style = opts.trailStyle, rate = style === "ribbon" ? 150 : 60;
      let count = 0; for (const q of parts) if (q.k >= 6 && q.k <= 8) count++;
      for (const st of streaks) {
        if (t < st.start || t > st.start + st.dur || count > 1200) continue;
        const k = (t - st.start) / st.dur, hx2 = st.x + st.vx * k, hy2 = st.y + st.vy * k, want = rate * dt, n = Math.floor(want) + (R() < want % 1 ? 1 : 0);
        for (let j = 0; j < n; j++, count++) {
          const back = R() * 0.03, q = { col: Math.floor(R() * 5), seed: R() * 6, age: 0, x: hx2 - st.vx * back + (R() - 0.5) * 6, y: hy2 - st.vy * back + (R() - 0.5) * 6, vx: 0, vy: 0 };
          if (style === "dust") Object.assign(q, { k: 6, vx: st.vx * 0.015, vy: (5 + R() * 10) * (life / 3), size: 1.2 + R() * 1.4, life: life * (0.6 + R() * 0.4) });
          else if (style === "ribbon") Object.assign(q, { k: 7, size: 6 + R() * 3, life: life * 0.7 });
          else { const a = R() * 6.283, sp = 30 + R() * 60; Object.assign(q, { k: 8, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 15, size: 1.1 + R() * 0.9, life: Math.min(life, 0.7 + R() * life * 0.5) }); }
          parts.push(q);
        }
      }
    }
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
    const glow = 0.72 + 0.22 * (0.5 + 0.5 * Math.sin(t / 2600)) + 0.06 * Math.sin(t / 700);   // slow breathing with a faint flutter
    ctx.globalAlpha = (1 - a) * glow; ctx.drawImage(F.imgs[i], ox, y, bw, bh);
    ctx.globalAlpha = a * glow; ctx.drawImage(F.imgs[j], ox, y, bw, bh);
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
    if (sceneKey !== "space") drawMoonPhase();
    ctx.save(); skyClip();
    const since = t - shimmerStart;
    for (const s of tw) {
      const boost = since >= 0 && since < 7000 ? Math.sin((Math.PI * since) / 7000) * (0.5 + 0.5 * Math.sin(t / 160 + s.ph * 3)) : 0;
      ctx.globalAlpha = Math.max(0, (rwFor === "clear" || !rwFor ? 1 : rwFor === "cloudy" || rwFor === "snow" ? 0.6 : 0.4) * (1 - dawnAmt) * Math.min(1, 0.55 + 0.45 * Math.sin((t / 1000) * s.sp + s.ph) + boost * 0.6));
      ctx.fillStyle = "#ece4f4"; ctx.beginPath(); ctx.arc(s.x, s.y, s.r, 0, 6.29); ctx.fill();
      if (hoverOn && opts.hStars) { const sd = Math.hypot(s.x - hx, s.y - hy); if (sd < 140) { ctx.globalAlpha = 1; glow(s.x, s.y, s.r * 7 + 4, 240, 232, 255, (1 - sd / 140) * 0.9); } }
    }
    ctx.restore(); ctx.globalAlpha = 1;
    for (const L of d.layers) drift(L, t);
    if (dawnAmt < 0.7) for (const F of d.fades) if (!F.after) fade(F, t);
    ctx.save(); skyClip();
    drawTrails();
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
    if (sceneKey === "space") drawMoonPhase();
    for (const L of d.reflects) drift(L, t);
    if (dawnAmt < 0.7) for (const F of d.fades) if (F.after) fade(F, t);
    if (d.lifeunder) life(t, 1); else { life(t, 2); drawDrops(); }
    drawCats(t); drawFestival(t); drawRealWeather(t); drawPlay(t);
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
        const rowY = y0 + i * rh * sc, Rr = 120;
        if (hoverOn && opts.hGrassSnow && Math.abs(rowY - hy) < Rr * 1.4) {
          const push = (1 - Math.abs(rowY - hy) / (Rr * 1.4)) * 26, cut = Math.max(0, Math.min(iw, (hx - ox) / sc));
          if (cut > 0) ctx.drawImage(S.img, 0, i * rh, cut, hh, ox + dx - push, rowY, cut * sc, hh * sc + 0.5);
          if (cut < iw) ctx.drawImage(S.img, cut, i * rh, iw - cut, hh, ox + dx + cut * sc + push, rowY, (iw - cut) * sc, hh * sc + 0.5);
          continue;
        }
        ctx.drawImage(S.img, 0, i * rh, iw, hh, ox + dx, rowY, iw * sc, hh * sc + 0.5);
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
      if (m.kind >= 10) { drawSurprise(m, t); continue; }
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
      const near = hoverOn && opts.hLamps ? Math.max(0, 1 - Math.hypot(x - hx, y - hy) / 260) : 0;
      glow(x, y, r * (4.5 + near * 4), L[3], L[4], L[5], 0.25 + 0.3 * f + near * 0.4); glow(x, y, r * 1.2, 255, 240, 220, 0.6 + 0.4 * f);
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
      if (q.k >= 6 && q.k <= 8) continue;
      if (q.k === 4) { if (q.age >= 0) glow(q.x, q.y, q.size * 4, 255, 244, 214, (1 - q.age / q.life) * 0.8); continue; }
      if (q.k === 5) { if (q.age < 0) continue; const hs = q.size, al = 1 - q.age / q.life; ctx.fillStyle = rgba(255, 150, 190, 0.86 * al); ctx.beginPath(); ctx.arc(q.x - hs / 2, q.y, hs / 2, 0, 6.29); ctx.arc(q.x + hs / 2, q.y, hs / 2, 0, 6.29); ctx.moveTo(q.x - hs * 0.95, q.y + hs * 0.05); ctx.lineTo(q.x + hs * 0.95, q.y + hs * 0.05); ctx.lineTo(q.x, q.y + hs * 1.1); ctx.fill(); continue; }
      if (q.k === 3 && q.age < 0) continue;
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

  function overWater(x, y) { for (const wr of d.water) { const [x1, y1] = M(wr[0], wr[1]), [x2, y2] = M(wr[2], wr[3]); if (x > x1 && x < x2 && y > y1 && y < y2) return true; } return false; }
  function showerAt(x, y, t) { for (let i = 0; i < 16; i++) { const a = R() * 6.283, sp = 220 + R() * 200; streaks.push({ x: x + Math.cos(a) * 20, y: y + Math.sin(a) * 20, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp, start: t + R() * 2500, dur: 700 + R() * 400 }); } }
  function weatherBurst(t) {
    const snowy = d.snow !== "none" || ["snowcabin", "village", "room-snow"].includes(sceneKey);
    tempKind = snowy ? 2 : 1; const n = Math.round((w * h) / (snowy ? 5000 : 7000));
    temp = Array.from({ length: n }, () => ({ x: R() * w, y: R() * h, len: 10 + R() * 10, sp: snowy ? 26 + R() * 40 : 650 + R() * 350, ph: R() * 6, r: 1 + R() * 1.8 }));
    weatherFrom = t; weatherUntil = t + 60000;
  }
  function spawnSurprise(t) {
    const sp = d.surprises[Math.floor(R() * d.surprises.length)], ltr = R() < 0.5;
    if (sp[0] === "comet" && dawnAmt < 0.5 && !d.noshoot) movers.push({ kind: 10, x: ltr ? -60 : w + 60, y: h * (0.06 + R() * 0.16), vx: ((ltr ? 1 : -1) * w) / 45, vy: h * 0.002 * (R() - 0.3), born: t, life: 60000 });
    else if (sp[0] === "owl") { const [ax, ay] = M(+sp[1], +sp[2]), [cx, cy] = M(+sp[3], +sp[4]); movers.push({ kind: 11, x: ltr ? ax - 60 : cx + 60, y: ay + R() * (cy - ay), vx: ((ltr ? 1 : -1) * (cx - ax)) / 7, vy: (R() - 0.5) * 10, born: t, life: 12000, ph: R() * 6 }); }
    else if (sp[0] === "fox") { const [ax, ay] = M(+sp[1], +sp[3]), [cx] = M(+sp[2], +sp[3]); movers.push({ kind: 12, x: ltr ? ax - 80 : cx + 80, y: ay, vx: ((ltr ? 1 : -1) * (cx - ax)) / 18, vy: 0, born: t, life: 25000 }); }
    else if (sp[0] === "whale") { const [ax, ay] = M(+sp[1], +sp[2]); movers.push({ kind: 13, x: ax, y: ay, vx: 0, vy: 0, born: t, life: 7000, dir: ltr ? 1 : -1 }); }
  }
  function drawSurprise(m, t) {
    const life = (t - m.born) / m.life, a = Math.max(0, Math.min(1, life * 8, (1 - life) * 8)), sil = `rgba(16,10,20,${a})`;
    if (m.kind === 10) { const dir = m.vx > 0 ? -1 : 1; for (let i = 14; i >= 0; i--) glow(m.x + dir * i * 16, m.y - m.vy * i * 0.4, 10 + i * 2.2, 200, 220, 255, a * 0.5 * (1 - i / 15)); glow(m.x, m.y, 12, 255, 255, 255, a); return; }
    ctx.fillStyle = sil; ctx.strokeStyle = sil;
    if (m.kind === 11) { const s = 22, fl = Math.sin(t / 170 + m.ph) * s * 0.55, dir = m.vx > 0 ? 1 : -1; ctx.lineWidth = s * 0.22;
      ctx.beginPath(); ctx.ellipse(m.x, m.y, s * 0.35, s * 0.25, 0, 0, 6.29); ctx.fill(); ctx.beginPath(); ctx.arc(m.x + dir * s * 0.25, m.y - s * 0.22, s * 0.18, 0, 6.29); ctx.fill();
      ctx.beginPath(); ctx.moveTo(m.x - s * 1.2, m.y - fl); ctx.quadraticCurveTo(m.x - s * 0.5, m.y - s * 0.12, m.x, m.y); ctx.quadraticCurveTo(m.x + s * 0.5, m.y - s * 0.12, m.x + s * 1.2, m.y - fl); ctx.stroke(); return; }
    if (m.kind === 12) { const s = 30 * (0.6 + 0.6 * (m.y / h)), dir = m.vx > 0 ? 1 : -1, st = Math.sin(t / 120) * s * 0.18, hx2 = m.x + dir * s * 0.62; ctx.lineWidth = Math.max(1.5, s * 0.08);
      ctx.beginPath(); ctx.ellipse(m.x, m.y - s * 0.34, s * 0.6, s * 0.21, 0, 0, 6.29); ctx.fill(); ctx.beginPath(); ctx.ellipse(hx2, m.y - s * 0.59, s * 0.2, s * 0.16, 0, 0, 6.29); ctx.fill();
      ctx.beginPath(); ctx.moveTo(hx2 + dir * s * 0.15, m.y - s * 0.65); ctx.lineTo(hx2 + dir * s * 0.42, m.y - s * 0.55); ctx.lineTo(hx2 + dir * s * 0.12, m.y - s * 0.5); ctx.fill();
      ctx.beginPath(); ctx.moveTo(hx2 - s * 0.12, m.y - s * 0.7); ctx.lineTo(hx2 - s * 0.06, m.y - s * 0.98); ctx.lineTo(hx2 + s * 0.02, m.y - s * 0.72); ctx.fill();
      ctx.beginPath(); ctx.ellipse(m.x - dir * s * 0.85, m.y - s * 0.47, s * 0.4, s * 0.15, 0, 0, 6.29); ctx.fill();
      ctx.beginPath(); for (const [lx, sgn] of [[-0.35, 1], [0.35, -1], [-0.2, -1], [0.2, 1]]) { ctx.moveTo(m.x + lx * s, m.y - s * 0.2); ctx.lineTo(m.x + lx * s + sgn * st, m.y); } ctx.stroke(); return; }
    if (m.kind === 13) { const s = 70 * sc, k = life, rise = Math.sin(Math.min(1, k / 0.75) * Math.PI);
      if (rise > 0.02) { ctx.fillStyle = "rgb(14,10,22)"; const top = m.y - rise * s * 0.45; ctx.beginPath(); ctx.ellipse(m.x, top + rise * s * 0.45, s, rise * s * 0.45 + 1, 0, Math.PI, 0); ctx.fill();
        ctx.beginPath(); ctx.moveTo(m.x + m.dir * s * 0.2, top + s * 0.06); ctx.lineTo(m.x + m.dir * s * 0.32, top - s * 0.14 * rise); ctx.lineTo(m.x + m.dir * s * 0.42, top + s * 0.1); ctx.fill(); }
      if (k > 0.15 && k < 0.55) for (let i = 0; i < 6; i++) glow(m.x - m.dir * s * 0.3 + Math.sin(i * 2.1) * 6, m.y - s * 0.5 - i * 12 * sc * ((k - 0.15) / 0.4), 6 + i, 220, 226, 245, 0.45 * (1 - i / 6));
      if (k > 0.7) glow(m.x, m.y, (s * 0.9 * (k - 0.7)) / 0.3 + 4, 220, 226, 245, 0.5 * (1 - (k - 0.7) / 0.3)); }
  }
  const TRAIL_COLOURS = [[236, 236, 255], [255, 214, 140], [210, 180, 255], [255, 170, 215], [170, 220, 255]];
  function drawTrails() {
    if (!parts.some((q) => q.k >= 6 && q.k <= 8)) return;
    ctx.save(); skyClip();
    for (const q of parts) {
      if (q.k < 6 || q.k > 8 || q.age < 0) continue;
      const [r, g, b] = TRAIL_COLOURS[q.col], left = 1 - q.age / q.life;
      if (q.k === 6) { const tw = 0.45 + 0.55 * Math.abs(Math.sin(q.age * 7 + q.seed * 6)), a = Math.pow(left, 1.1); glow(q.x, q.y, q.size * 6, r, g, b, a * 0.7); glow(q.x, q.y, q.size * 1.8, 255, 255, 255, a * tw); }
      else if (q.k === 7) glow(q.x, q.y, q.size * 2.6, r, g, b, Math.pow(left, 1.4) * 0.3);
      else { glow(q.x, q.y, q.size * 5, r, g, b, left * 0.8); glow(q.x, q.y, q.size * 1.6, 255, 250, 235, left); }
    }
    ctx.restore();
  }
  function drawFestival(t) {
    if (!fest) return;
    if (diyas) for (const dy of diyas) {
      const x = dy.x + Math.sin(t / 2600 + dy.ph) * dy.dr, y = dy.y + Math.sin(t / 900 + dy.ph) * 1.5, s = 3.5 + ((y - horizonY) / (h - horizonY + 1)) * 6, fl = 0.75 + 0.25 * Math.sin(t / 120 + dy.ph * 5);
      for (let j = 0; j < 4; j++) glow(x + Math.sin(t / 400 + j), y + s * (1.2 + j * 0.9), s * (0.9 - j * 0.12), 255, 170, 80, (0.3 - j * 0.06) * fl);
      glow(x, y - s * 0.9, s * 3, 255, 170, 80, 0.45 * fl);
      ctx.fillStyle = "rgb(120,56,36)"; ctx.beginPath(); ctx.ellipse(x, y - s * 0.1, s * 1.4, s * 0.8, 0, 0, Math.PI); ctx.fill();
      glow(x, y - s * 0.95, s * 0.8, 255, 236, 170, fl);
    }
    ctx.save(); skyClip();
    if (holiPuffs) for (const p of holiPuffs) { const c = HOLI_COLS[p.col]; glow(p.x, p.y, p.r * 1.6, c[0], c[1], c[2], 0.11); }
    for (const l of lanterns) {
      const fade = Math.max(0, Math.min(1, (horizonY - l.y + 30) / 40)), fl = 0.85 + 0.15 * Math.sin(t / 160 + l.ph * 4);
      glow(l.x, l.y + l.s * 0.4, l.s * 3.2, 255, 150, 70, 0.45 * fade * fl);
      ctx.fillStyle = `rgba(255,196,120,${(0.92 * fade).toFixed(3)})`; ctx.beginPath(); ctx.moveTo(l.x - l.s * 0.7, l.y - l.s); ctx.lineTo(l.x + l.s * 0.7, l.y - l.s); ctx.lineTo(l.x + l.s * 0.5, l.y + l.s); ctx.lineTo(l.x - l.s * 0.5, l.y + l.s); ctx.fill();
    }
    if (fest === "birthday") {
      const hxc = d.moon.length && M(d.moon[0][0], 0)[0] > w / 2 ? w * 0.28 : w * 0.72, hyc = h * 0.17, k = 4.2;
      for (let i = 0; i < 24; i++) { const a = (i / 24) * Math.PI * 2, x = 16 * Math.sin(a) ** 3, y = -(13 * Math.cos(a) - 5 * Math.cos(2 * a) - 2 * Math.cos(3 * a) - Math.cos(4 * a)), tw = 0.75 + 0.25 * Math.sin(t / 700 + i);
        glow(hxc + x * k, hyc + y * k, 10, 255, 190, 215, 0.55 * tw); glow(hxc + x * k, hyc + y * k, 3.2, 255, 250, 252, tw); }
    }
    for (const b of bursts) {
      const age = (t - b.start) / 1000, life = b.type === 1 ? 3.6 : b.type === 2 ? 1.8 : 2.6; if (age > life) continue;
      const fade = age < 0.15 ? age / 0.15 : 1 - (age - 0.15) / (life - 0.15);
      if (age < 0.5) glow(b.x, b.y, b.r * 0.9, b.col[0], b.col[1], b.col[2], 0.5 * (1 - age / 0.5));
      for (const a of b.ang) for (let tr = 0; tr < (b.type === 1 ? 7 : 4); tr++) {
        const ta = Math.max(0, age - tr * (b.type === 1 ? 0.12 : 0.07)), rr = b.r * (1 - Math.exp(-ta * (b.type === 1 ? 2.2 : 2.6)));
        const x = b.x + Math.cos(a) * rr, y = b.y + Math.sin(a) * rr * (b.type === 1 ? 0.8 : 1) + (b.type === 1 ? 0.45 * b.r * ta * ta : 6 * ta * ta);
        const al = fade * (1 - tr * (b.type === 1 ? 0.18 : 0.3)), rad = (b.type === 2 ? 4 : 7) * (tr === 0 ? 1.3 : 0.85);
        glow(x, y, rad, b.col[0], b.col[1], b.col[2], Math.min(1, al * 1.1));
        if (tr === 0) { glow(x, y, rad * 2.2, b.col[0], b.col[1], b.col[2], al * 0.25); glow(x, y, rad * 0.4, 255, 252, 245, Math.min(1, al * 1.2)); }
      }
    }
    ctx.restore();
    if (holiSpecks) for (const p of holiSpecks) { const c = HOLI_COLS[p.col]; ctx.fillStyle = `rgba(${c[0]},${c[1]},${c[2]},0.86)`; ctx.beginPath(); ctx.arc(p.x, p.y, p.r, 0, 6.29); ctx.fill(); }
  }
  // a still picture of the scene at 3840 x 2160, drawn with the same engine
  async function snapshot() {
    const cssW = canvas.style.width, cssH = canvas.style.height;
    canvas.style.width = canvas.clientWidth + "px"; canvas.style.height = canvas.clientHeight + "px";   // keep the page looking the same meanwhile
    resize(3840, 2160);
    const t0 = performance.now(); for (let i = 0; i < 60; i++) step(t0 + i * 40, 0.04, false);
    draw(t0 + 2400);
    const blob = await new Promise((res) => canvas.toBlob(res, "image/png"));
    canvas.style.width = cssW; canvas.style.height = cssH; resize();
    return blob;
  }
  function drawMoonPhase() {
    const p = opts.moonPhase; if (p == null || p < 0 || dawnAmt > 0.95 || !d.moon.length) return;
    const [cx, cy] = M(d.moon[0][0], d.moon[0][1]), r = d.moon[0][2] * sc * 1.04;
    if (!moonShade) {   // the sky colour in a ring just outside the moon, sampled once from the painting
      const c = document.createElement("canvas"); c.width = c.height = 1; const g = c.getContext("2d"); let rr = 0, gg = 0, bb = 0, n = 0;
      const dist = d.moon[0][2] * (sceneKey === "space" ? 1.6 : 2.4);
      for (let i = 0; i < 16; i++) { const a = (i * Math.PI) / 8, px = d.moon[0][0] + Math.cos(a) * dist, py = d.moon[0][1] + Math.sin(a) * dist; if (px < 0 || py < 0 || px >= 1920 || py >= 1080) continue;
        g.clearRect(0, 0, 1, 1); g.drawImage(imgs.bg, px, py, 1, 1, 0, 0, 1, 1); const k = g.getImageData(0, 0, 1, 1).data; rr += k[0]; gg += k[1]; bb += k[2]; n++; }
      n = n || 1; moonShade = `rgb(${Math.round(rr / n) + 3},${Math.round(gg / n) + 2},${Math.round(bb / n) + 5})`;
    }
    const waxing = p < 0.5, k = Math.cos(2 * Math.PI * p), ex = Math.abs(k) * r;
    ctx.save(); ctx.globalAlpha = 1 - dawnAmt;
    if (sceneKey !== "space") { ctx.fillStyle = "rgb(236,230,244)"; ctx.beginPath(); ctx.arc(cx, cy, r, 0, 6.29); ctx.fill(); }
    // the unlit half, then the terminator: added for a crescent, cut away for a gibbous moon
    ctx.beginPath(); ctx.arc(cx, cy, r + 0.5, waxing ? Math.PI / 2 : -Math.PI / 2, waxing ? (3 * Math.PI) / 2 : Math.PI / 2);
    // crescent: the curve bulges out of the dark half; gibbous: it bites into it (drawn the other way round)
    ctx.ellipse(cx, cy, Math.max(0.01, ex), r + 0.5, 0, waxing ? -Math.PI / 2 : Math.PI / 2, waxing ? Math.PI / 2 : (3 * Math.PI) / 2, k < 0);
    ctx.closePath(); ctx.fillStyle = moonShade; ctx.fill();
    ctx.restore();
  }
  function drawRealWeather(t) {
    if (!rwFor || rwFor === "clear") return;
    ctx.save(); skyClip();
    if (clouds.length) {
      if (!cloudImg) { cloudImg = document.createElement("canvas"); cloudImg.width = 640; cloudImg.height = 180; const g = cloudImg.getContext("2d"); let seed = 7; const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
        for (let i = 0; i < 140; i++) { const u = rnd(), th = Math.sin(u * Math.PI), x = 70 + u * 500, y = 95 - th * 22 + (rnd() - 0.5) * 34 * th, rr = (26 + rnd() * 34) * (0.5 + 0.5 * th) * 1.9;
          const gr = g.createRadialGradient(x, y, 0, x, y, rr); gr.addColorStop(0, "rgba(92,80,112,0.085)"); gr.addColorStop(0.5, "rgba(92,80,112,0.04)"); gr.addColorStop(1, "rgba(92,80,112,0)"); g.fillStyle = gr; g.fillRect(x - rr, y - rr, rr * 2, rr * 2); } }
      ctx.globalAlpha = rwFor === "cloudy" ? 0.8 : 0.95; for (const c of clouds) ctx.drawImage(cloudImg, c.x, c.y, 640 * c.s, 180 * c.s); ctx.globalAlpha = 1;
    }
    if (rwFor === "storm" && t - flashAt < 900) { const e = t - flashAt, a = e < 120 ? 1 - e / 120 : e > 260 && e < 420 ? 0.6 * (1 - (e - 260) / 160) : 0; if (a > 0) { ctx.fillStyle = `rgba(220,214,255,${0.27 * a})`; ctx.fillRect(0, 0, w, h); } }
    ctx.restore();
    if (rwFor === "fog") for (let i = 0; i < 6; i++) { const x = (((t / 60) * (6 + i * 2) + i * 400) % (w + 800)) - 400, y = h * (0.55 + i * 0.06); glow(x, y, w * 0.35, 210, 200, 225, 0.12); glow(x + w * 0.5, y + 20, w * 0.3, 210, 200, 225, 0.1); }
    if (rwKind === 1) { ctx.strokeStyle = `rgba(205,198,228,${rwFor === "storm" ? 0.37 : 0.27})`; ctx.lineWidth = 1; ctx.beginPath(); for (const p of rw) { ctx.moveTo(p.x, p.y); ctx.lineTo(p.x + p.len * 0.12, p.y - p.len); } ctx.stroke(); }
    else if (rwKind === 2) { ctx.fillStyle = "rgba(242,238,250,0.78)"; ctx.beginPath(); for (const p of rw) { ctx.moveTo(p.x + p.r, p.y); ctx.arc(p.x, p.y, p.r, 0, 6.29); } ctx.fill(); }
  }
  function drawCats(t) {
    for (const ct of d.cat) {
      const [cx, cy] = M(ct[0], ct[1]), cs = ct[2] * sc, hanging = ct[3] > 0;
      const near = opts.hCat && hoverOn && Math.abs(hx - cx) < cs * 1.6 && Math.abs(hy - (cy - cs * 0.5)) < cs * 1.4, happy = t - catReactAt < 2500;
      const sw = Math.sin(t / (happy ? 160 : near ? 260 : 1500)) * (happy ? 0.55 : near ? 0.4 : 0.16);
      ctx.strokeStyle = "rgb(10,6,12)"; ctx.lineWidth = Math.max(2, cs * (hanging ? 0.08 : 0.09)); ctx.lineCap = "round"; ctx.beginPath();
      if (hanging) { ctx.moveTo(cx + cs * 0.3, cy); ctx.quadraticCurveTo(cx + cs * (0.5 + sw * 0.4), cy + cs * 0.3, cx + cs * (0.45 + sw), cy + cs * 0.6); }
      else { ctx.moveTo(cx + cs * 0.35, cy - cs * 0.05); ctx.quadraticCurveTo(cx + cs * 0.75, cy - cs * (0.02 + sw * 0.15), cx + cs * 0.9, cy - cs * (0.2 + sw * 0.5)); }
      ctx.stroke(); ctx.lineCap = "butt";
      if ((near || happy) && t % 1400 < 220) { const side = Math.floor(t / 1400) % 2 ? 1 : -1, ex = cx + side * cs * 0.2, ey = cy - cs * 1.2; ctx.fillStyle = "rgb(10,6,12)"; ctx.beginPath(); ctx.moveTo(ex - cs * 0.07, ey); ctx.lineTo(ex + side * cs * 0.05, ey - cs * 0.3); ctx.lineTo(ex + cs * 0.07, ey); ctx.fill(); }
    }
  }
  function drawConst(pts, a) {
    ctx.strokeStyle = rgba(210, 196, 240, (140 * a) / 255); ctx.lineWidth = 1.2; ctx.beginPath(); pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y))); ctx.stroke();
    for (const [x, y] of pts) glow(x, y, 7, 236, 228, 255, 0.8 * a);
  }
  function drawPlay(t) {
    if (t < weatherUntil && temp.length) {
      const k = Math.min(1, (t - weatherFrom) / 3000, (weatherUntil - t) / 4000);
      if (tempKind === 1) { ctx.strokeStyle = `rgba(205,198,228,${0.32 * k})`; ctx.lineWidth = 1; ctx.beginPath(); for (const p of temp) { ctx.moveTo(p.x, p.y); ctx.lineTo(p.x + p.len * 0.12, p.y - p.len); } ctx.stroke(); }
      else { ctx.fillStyle = `rgba(242,238,250,${0.82 * k})`; ctx.beginPath(); for (const p of temp) { ctx.moveTo(p.x + p.r, p.y); ctx.arc(p.x, p.y, p.r, 0, 6.29); } ctx.fill(); }
    }
    drawings = drawings.filter((dr) => t - dr.t < 120000);
    for (const dr of drawings) { const age = (t - dr.t) / 120000; drawConst(dr.pts, 1 - age * age); }
    if (drawing.length) drawConst(drawing, 1);
    if (t - moonCardAt < 7000 && moonCard && d.moon.length) {
      const a = Math.min(1, (t - moonCardAt) / 300, (7000 - (t - moonCardAt)) / 800), [cx, cy] = M(d.moon[0][0], d.moon[0][1]), r = d.moon[0][2] * sc;
      ctx.font = "600 14px Sora, system-ui, sans-serif"; let cw = ctx.measureText(moonCard[0]).width; ctx.font = "13px Sora, system-ui, sans-serif"; for (const l of moonCard.slice(1)) cw = Math.max(cw, ctx.measureText(l).width);
      const pad = 12, lineH = 20, bw = cw + pad * 2, bh = lineH * moonCard.length + pad * 2 - 4; let bx = cx + r * 1.6; if (bx + bw > w - 10) bx = cx - r * 1.6 - bw; const by = Math.max(8, Math.min(h - bh - 8, cy - bh / 2));
      ctx.fillStyle = rgba(22, 14, 30, 0.72 * a); ctx.strokeStyle = rgba(200, 180, 230, 0.35 * a); ctx.lineWidth = 1; ctx.beginPath(); ctx.roundRect ? ctx.roundRect(bx, by, bw, bh, 12) : ctx.rect(bx, by, bw, bh); ctx.fill(); ctx.stroke();
      moonCard.forEach((l, i) => { ctx.font = i ? "13px Sora, system-ui, sans-serif" : "600 14px Sora, system-ui, sans-serif"; ctx.fillStyle = rgba(236, 228, 244, (i ? 0.8 : 0.96) * a); ctx.fillText(l, bx + pad, by + pad + 12 + i * lineH); });
    }
    if (t - wishAt < 4000) {
      const a = Math.min(1, (t - wishAt) / 300, (4000 - (t - wishAt)) / 1200); ctx.font = "600 14px Sora, system-ui, sans-serif";
      const msg = "\u2726 make a wish", mw = ctx.measureText(msg).width; ctx.fillStyle = rgba(255, 240, 210, 0.94 * a);
      ctx.fillText(msg, Math.max(8, Math.min(w - mw - 8, wishX - mw / 2)), wishY - 30 - ((t - wishAt) / 4000) * 26);
    }
  }
  function setHover(x, y, active) {
    hx = x - parX; hy = y - parY; hoverOn = active;
    if (pDown && opts.cDraw && d && inSky(hx, hy)) for (const s of tw) { if (Math.abs(s.x - hx) > 22 || Math.abs(s.y - hy) > 22) continue; const last = drawing[drawing.length - 1]; if (!drawing.some((p) => p[0] === s.x && p[1] === s.y) && (!last || last[0] !== s.x)) drawing.push([s.x, s.y]); break; }
  }
  function pointerDown(x, y) { pDown = true; showerFired = false; pdx = x - parX; pdy = y - parY; pDownAt = performance.now(); drawing = []; }
  function pointerUp() { if (showerFired || drawing.length >= 2) ignoreClicksUntil = performance.now() + 400; pDown = false; showerFired = false; if (drawing.length >= 2) { drawings.push({ t: performance.now(), pts: drawing }); if (drawings.length > 12) drawings.shift(); } drawing = []; }

  function pointer(nx, ny) { if (opts.parallax) { parTX = -nx * 12; parTY = -ny * 7; } else { parTX = parTY = 0; } }

  function inSky(x, y) {
    const pts = [[0, 0], ...d.skyline.map(([a, b]) => M(a, b)), [w, M(0, d.skyline.length ? d.skyline[d.skyline.length - 1][1] : 540)[1]], [w, 0]];
    let c = false;
    for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) { const [xi, yi] = pts[i], [xj, yj] = pts[j]; if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) c = !c; }
    return c;
  }

  function click(x, y) {
    if (!d) return false;
    const t = performance.now();
    if ((pDown && (showerFired || drawing.length >= 2)) || t < ignoreClicksUntil) { ignoreClicksUntil = -1e9; return true; }
    x -= parX; y -= parY;
    const dbl = t - lastClickT < 450 && Math.abs(x - lastClickX) < 24 && Math.abs(y - lastClickY) < 24;
    lastClickT = dbl ? -1e9 : t; lastClickX = x; lastClickY = y;
    if (dbl && opts.cWeather && inSky(x, y)) { weatherBurst(t); return true; }
    if (opts.cCatch) for (let i = streaks.length - 1; i >= 0; i--) {
      const s = streaks[i]; if (t < s.start || t > s.start + s.dur) continue;
      const k = (t - s.start) / s.dur, sx = s.x + s.vx * k, sy = s.y + s.vy * k;
      if (Math.abs(sx - x) < 40 && Math.abs(sy - y) < 40) { streaks.splice(i, 1); for (let j = 0; j < 18; j++) { const a = R() * 6.283, sp = 30 + R() * 90; parts.push({ k: 4, x: sx, y: sy, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp, size: 1.5 + R() * 2, age: 0, life: 1.2 + R() * 0.8 }); } wishAt = t; wishX = sx; wishY = sy; return true; }
    }
    if (opts.cMoon) for (const mn of d.moon) { const [cx, cy] = M(mn[0], mn[1]), r = mn[2] * sc; if ((x - cx) ** 2 + (y - cy) ** 2 < r * r * 2.6) { moonCardAt = t; moonCard = opts.moonInfo ? opts.moonInfo() : ["The moon"]; return true; } }
    if (opts.cCatMug) {
      for (const ct of d.cat) { const [cx, cy] = M(ct[0], ct[1]), cs = ct[2] * sc; if (Math.abs(x - cx) < cs * 0.5 && y > cy - cs * 1.45 && y < cy + cs * 0.1) { catReactAt = t; for (let j = 0; j < 4; j++) parts.push({ k: 5, x: cx + (R() - 0.5) * cs * 0.4, y: cy - cs * 1.3, vx: (R() - 0.5) * 14, vy: -(22 + R() * 16), size: 5 + R() * 3, age: -j * 0.25, life: 2.2 }); return true; } }
      for (const sm of d.smoke) { const [cx, cy] = M(sm[0], sm[1]); if (Math.abs(x - cx) < 45 * sc && y > cy - 30 * sc && y < cy + 80 * sc) { for (let j = 0; j < 9; j++) parts.push({ k: 0, x: cx + (R() - 0.5) * 10, y: cy, vx: (R() - 0.5) * 24, vy: -(30 + R() * 25), size: sm[2] * sc * (0.9 + R() * 0.6), age: -j * 0.08, life: 3 + R() * 1.5, seed: R() * 6 }); return true; } }
    }
    if (opts.cTrain && train && !train.active) { const ty = oy + train.y * sc; if (y > ty - 20 && y < ty + train.img.height * sc + 30) { train.next = t; return true; } }
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
    KEYS, NAMES, start, resize, step, draw, shower, click, pointer, setHover, pointerDown, pointerUp, snapshot, spawnSurprise: () => d && spawnSurprise(performance.now()),
    setOptions(o) { Object.assign(opts, o); if (!opts.parallax) parTX = parTY = 0; lastDawnCheck = -1e9; },
    get active() { return active; },
    get dawnShown() { return dawnAmt; },
    stop() { active = false; d = null; if (ctx) ctx.clearRect(0, 0, canvas.width, canvas.height); }
  };
})();

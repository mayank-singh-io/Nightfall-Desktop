// ================= Settings =================
const DEFAULTS = {
  use24h: false,
  shootingStars: true,
  showThought: true,
  rotateMinute: true,
  showerFrequency: "sometimes",
  showerSize: "medium",
  skyMode: "auto",
  moonMode: "auto",
  weatherMode: "auto",
  city: null,
  notesStyle: "tiny",
  realConstellations: true,
  tabScene: "classic",
  tabSceneKey: "aurora",
  trainEvery: "few",
  timeOfDay: "sunrise",
  roomView: "random",
  clickStars: true, clickLights: true, clickWater: true, mouseShift: false,
  notesPosition: "pos-bl",
  links: [
    { name: "YouTube Music", url: "https://music.youtube.com" },
    { name: "WhatsApp", url: "https://web.whatsapp.com" },
    { name: "GitHub", url: "https://github.com" },
    { name: "Gmail", url: "https://mail.google.com" },
    { name: "Claude", url: "https://claude.ai" }
  ]
};

function readJSON(key, fallback) {
  try { const v = JSON.parse(localStorage.getItem(key) || "null"); return v ?? fallback; } catch { return fallback; }
}
function writeJSON(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch {}
}
let sceneStarting = false, sceneBase = null, roomWeatherSeen = null;
const sceneStill = matchMedia("(prefers-reduced-motion: reduce)").matches;   // respect "reduce motion"
let settings = { ...structuredClone(DEFAULTS), ...readJSON("nightfall", {}) };
function saveSettings() { writeJSON("nightfall", settings); }

const $ = (id) => document.getElementById(id);
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
function rand(min, max) { return min + Math.random() * (max - min); }
function todayKey(d = new Date()) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

// ================= Clock and greeting =================
let lastMinute = -1;
function tick() {
  const now = new Date();
  $("clock").textContent = now.toLocaleTimeString([], {
    hour: "numeric", minute: "2-digit", hour12: !settings.use24h
  }).replace(/\s?[AP]M$/i, "");
  const h = now.getHours();
  $("greeting").textContent =
    h < 5 ? "Good night" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : h < 21 ? "Good evening" : "Good night";
  if (now.getMinutes() !== lastMinute) {
    lastMinute = now.getMinutes();
    updateSky();
    updateMoonStatus();
  }
}

// ================= Search =================
$("search").addEventListener("submit", (e) => {
  e.preventDefault();
  const text = $("q").value.trim();
  if (!text) return;
  const looksLikeUrl = /^[^\s]+\.[a-z]{2,}(\/\S*)?$/i.test(text) || /^https?:\/\//i.test(text);
  if (looksLikeUrl) location.href = /^https?:\/\//i.test(text) ? text : "https://" + text;
  else if (globalThis.chrome?.search?.query) chrome.search.query({ text, disposition: "CURRENT_TAB" });
  else location.href = "https://search.brave.com/search?q=" + encodeURIComponent(text);
});

// ================= Sites =================
function faviconUrl(pageUrl) {
  const u = new URL(chrome.runtime.getURL("/_favicon/"));
  u.searchParams.set("pageUrl", pageUrl);
  u.searchParams.set("size", "64");
  return u.toString();
}
function renderLinks() {
  const nav = $("links");
  nav.replaceChildren();
  for (const link of settings.links) {
    const a = document.createElement("a");
    a.className = "site";
    a.href = link.url;
    const tile = document.createElement("div");
    tile.className = "tile";
    const img = document.createElement("img");
    img.alt = "";
    img.src = faviconUrl(link.url);
    img.onerror = () => {
      const letter = document.createElement("span");
      letter.className = "letter";
      letter.textContent = link.name.charAt(0).toUpperCase();
      img.replaceWith(letter);
    };
    tile.append(img);
    const label = document.createElement("span");
    label.textContent = link.name;
    a.append(tile, label);
    nav.append(a);
  }
  renderLinkList();
}
function renderLinkList() {
  const list = $("link-list");
  list.replaceChildren();
  settings.links.forEach((link, i) => {
    const li = document.createElement("li");
    const name = document.createElement("span");
    name.textContent = link.name;
    const up = document.createElement("button");
    up.type = "button"; up.textContent = "Move up"; up.disabled = i === 0;
    up.onclick = () => {
      [settings.links[i - 1], settings.links[i]] = [settings.links[i], settings.links[i - 1]];
      saveSettings(); renderLinks();
    };
    const remove = document.createElement("button");
    remove.type = "button"; remove.textContent = "Remove";
    remove.onclick = () => { settings.links.splice(i, 1); saveSettings(); renderLinks(); };
    li.append(name, up, remove);
    list.append(li);
  });
}
$("add-link").addEventListener("click", () => {
  const nameEl = $("new-name"), urlEl = $("new-url"), err = $("link-error");
  let url = urlEl.value.trim();
  const name = nameEl.value.trim();
  if (!name) { err.textContent = "Enter a name for the site."; nameEl.focus(); return; }
  if (!url) { err.textContent = "Enter the site's address."; urlEl.focus(); return; }
  if (!/^https?:\/\//i.test(url)) url = "https://" + url;
  let parsed = null;
  try { parsed = new URL(url); } catch {}
  if (!parsed || /\s/.test(urlEl.value.trim()) || !(parsed.hostname.includes(".") || parsed.hostname === "localhost")) {
    err.textContent = "That address doesn't look right. Try something like youtube.com.";
    urlEl.focus();
    return;
  }
  err.textContent = "";
  settings.links.push({ name, url });
  saveSettings(); renderLinks();
  nameEl.value = ""; urlEl.value = "";
});
for (const id of ["new-name", "new-url"]) $(id).addEventListener("input", () => { $("link-error").textContent = ""; });

// ================= Thoughts =================
const thoughtEl = $("thought");
let thoughtTimer = null;
function nextThought() {
  let state = readJSON("nightfall-thoughts", null);
  if (!state || !Array.isArray(state.order) || state.order.length !== THOUGHTS.length || state.pos >= state.order.length) {
    const order = THOUGHTS.map((_, i) => i);
    for (let i = order.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [order[i], order[j]] = [order[j], order[i]];
    }
    state = { order, pos: 0 };
  }
  const line = THOUGHTS[state.order[state.pos]];
  state.pos += 1;
  writeJSON("nightfall-thoughts", state);
  return line;
}
function fadeToNext() {
  thoughtEl.style.opacity = "0";
  setTimeout(() => { thoughtEl.textContent = nextThought(); thoughtEl.style.opacity = "1"; }, 1200);
}
function scheduleThoughts() {
  clearInterval(thoughtTimer);
  thoughtTimer = null;
  if (settings.showThought && settings.rotateMinute && document.visibilityState === "visible") {
    thoughtTimer = setInterval(fadeToNext, 60000);
  }
}
function showThought() {
  if (!settings.showThought) thoughtEl.textContent = "";
  else if (!thoughtEl.textContent) thoughtEl.textContent = nextThought();
  scheduleThoughts();
}
document.addEventListener("visibilitychange", scheduleThoughts);

// ================= Sky by time of day =================
const STAR_BRIGHTNESS = { night: 1, dusk: 0.7, dawn: 0.45, day: 0.12 };
let skyPhase = "night";
function minutesOf(date) { return date.getHours() * 60 + date.getMinutes(); }
function computeSkyPhase() {
  if (settings.skyMode !== "auto") return settings.skyMode;
  const now = new Date();
  let sunrise = 6 * 60, sunset = 18 * 60 + 30;
  const w = readJSON("nightfall-weather", null);
  if (w && w.sunrise && w.sunset && w.day === todayKey()) {
    sunrise = minutesOf(new Date(w.sunrise));
    sunset = minutesOf(new Date(w.sunset));
  }
  const m = minutesOf(now);
  if (m >= sunrise - 45 && m < sunrise + 45) return "dawn";
  if (m >= sunrise + 45 && m < sunset - 60) return "day";
  if (m >= sunset - 60 && m < sunset + 45) return "dusk";
  return "night";
}
function updateSky() {
  skyPhase = computeSkyPhase();
  document.body.dataset.sky = skyPhase;
}

// ================= Moon =================
const MOON_FIXED = { crescent: 0.13, half: 0.25, gibbous: 0.37, full: 0.5 };
const MOON_NAMES = ["New moon", "Waxing crescent", "First quarter", "Waxing gibbous", "Full moon", "Waning gibbous", "Last quarter", "Waning crescent"];
function realMoonPhase(date = new Date()) {
  const synodic = 29.530588853;
  const reference = Date.UTC(2000, 0, 6, 18, 14);
  const age = (((date - reference) / 86400000) % synodic + synodic) % synodic;
  return age / synodic; // 0 new, 0.5 full
}
function moonPhase() {
  if (settings.moonMode === "off") return null;
  if (settings.moonMode === "auto") return realMoonPhase();
  return MOON_FIXED[settings.moonMode] ?? realMoonPhase();
}
function moonName(p) { return MOON_NAMES[Math.round(p * 8) % 8]; }
function moonGeometry() {
  const r = Math.max(20, Math.min(40, Math.min(innerWidth, innerHeight) * 0.033));
  return { x: innerWidth * 0.82, y: innerHeight * 0.17, r };
}
function updateMoonStatus() {
  const p = realMoonPhase();
  const lit = Math.round((1 - Math.cos(2 * Math.PI * p)) / 2 * 100);
  $("moon-status").textContent = `Tonight: ${moonName(p).toLowerCase()}, ${lit}% lit.`;
}
function drawMoon(ctx) {
  const p = moonPhase();
  if (p === null) return;
  const { x, y, r } = moonGeometry();
  const dim = (weatherKind === "rain" || weatherKind === "storm" || weatherKind === "fog") ? 0.4 : skyPhase === "day" ? 0.6 : 1;

  const glow = ctx.createRadialGradient(x, y, r * 0.8, x, y, r * 3.2);
  glow.addColorStop(0, `rgba(236, 228, 244, ${0.12 * dim * (1 - Math.abs(Math.cos(Math.PI * p * 2)) * 0.5)})`);
  glow.addColorStop(1, "rgba(236, 228, 244, 0)");
  ctx.fillStyle = glow;
  ctx.beginPath(); ctx.arc(x, y, r * 3.2, 0, Math.PI * 2); ctx.fill();

  ctx.globalAlpha = 0.12 * dim;               // faint earthshine on the dark part
  ctx.fillStyle = "#cfc6dc";
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill();

  const k = Math.cos(2 * Math.PI * p);
  const rx = Math.abs(k) * r;
  ctx.globalAlpha = 0.95 * dim;
  ctx.fillStyle = "#ece6f2";
  ctx.beginPath();
  if (p <= 0.5) {
    ctx.arc(x, y, r, -Math.PI / 2, Math.PI / 2, false);
    ctx.ellipse(x, y, rx, r, 0, Math.PI / 2, -Math.PI / 2, k > 0);
  } else {
    ctx.arc(x, y, r, -Math.PI / 2, Math.PI / 2, true);
    ctx.ellipse(x, y, rx, r, 0, Math.PI / 2, -Math.PI / 2, k < 0);
  }
  ctx.fill();
  ctx.globalAlpha = 1;
}

// ================= Weather =================
let weatherKind = "clear";
function kindFromCode(code) {
  if (code === 45 || code === 48) return "fog";
  if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return "rain";
  if ((code >= 71 && code <= 77) || code === 85 || code === 86) return "snow";
  if (code >= 95) return "storm";
  return "clear";
}
const WEATHER_WORDS = { clear: "clear skies", rain: "rain", storm: "a storm", snow: "snow", fog: "fog" };

function currentWeatherKind() {
  if (settings.weatherMode !== "auto") return settings.weatherMode;
  const w = readJSON("nightfall-weather", null);
  return w && typeof w.code === "number" ? kindFromCode(w.code) : "clear";
}

async function refreshWeather(force = false) {
  updateWeatherStatus();
  if (settings.weatherMode !== "auto" || !settings.city) return;
  const cached = readJSON("nightfall-weather", null);
  const fresh = cached && cached.lat === settings.city.lat && Date.now() - cached.fetched < 30 * 60000;
  if (fresh && !force) return;
  try {
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${settings.city.lat}&longitude=${settings.city.lon}` +
      `&current=weather_code&daily=sunrise,sunset&timezone=auto&forecast_days=1`;
    const res = await fetch(url);
    if (!res.ok) throw new Error("bad response");
    const data = await res.json();
    writeJSON("nightfall-weather", {
      code: data.current.weather_code,
      sunrise: data.daily.sunrise[0],
      sunset: data.daily.sunset[0],
      day: todayKey(),
      lat: settings.city.lat,
      fetched: Date.now()
    });
    setWeather(currentWeatherKind());
    updateSky();
    updateWeatherStatus();
  } catch {
    $("weather-status").textContent = "Couldn't reach the weather service. It'll try again in a bit.";
  }
}

function updateWeatherStatus() {
  const el = $("weather-status");
  if (settings.weatherMode !== "auto") { el.textContent = "Weather is locked to one style."; return; }
  if (!settings.city) { el.textContent = "Add your city to match the real weather. Until then, the sky stays clear."; return; }
  const w = readJSON("nightfall-weather", null);
  el.textContent = w && typeof w.code === "number"
    ? `Showing ${WEATHER_WORDS[kindFromCode(w.code)]} for ${settings.city.name}.`
    : `Checking the weather for ${settings.city.name}…`;
}

$("save-city").addEventListener("click", async () => {
  const input = $("city");
  const name = input.value.trim();
  const status = $("weather-status");
  if (!name) { status.textContent = "Enter a city name first."; input.focus(); return; }
  status.textContent = "Looking up your city…";
  try {
    const res = await fetch(`https://geocoding-api.open-meteo.com/v1/search?name=${encodeURIComponent(name)}&count=1&language=en&format=json`);
    const data = await res.json();
    const hit = data.results && data.results[0];
    if (!hit) { status.textContent = `Couldn't find "${name}". Try a nearby larger city.`; return; }
    settings.city = { name: `${hit.name}${hit.country ? ", " + hit.country : ""}`, lat: hit.latitude, lon: hit.longitude };
    saveSettings();
    input.value = settings.city.name;
    refreshWeather(true);
  } catch {
    status.textContent = "Couldn't reach the weather service. Check your connection and try again.";
  }
});
$("city").addEventListener("input", () => updateWeatherStatus());

// particles
const wcanvas = $("weather");
const wctx = wcanvas.getContext("2d");
let drops = [];
let fogTexture = null;
let fogOffset = 0;
let flash = 0, nextFlash = 0;

function buildFogTexture() {
  const c = document.createElement("canvas");
  c.width = Math.ceil(innerWidth * 1.5);
  c.height = Math.ceil(innerHeight * 0.6);
  const g = c.getContext("2d");
  for (let i = 0; i < 26; i++) {
    const x = Math.random() * c.width, y = rand(0.3, 0.9) * c.height, r = rand(120, 320);
    const grad = g.createRadialGradient(x, y, 0, x, y, r);
    grad.addColorStop(0, "rgba(205, 195, 222, 0.08)");
    grad.addColorStop(1, "rgba(205, 195, 222, 0)");
    g.fillStyle = grad;
    g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
  }
  return c;
}

function setWeather(kind) {
  weatherKind = kind;
  if (NightScene.active && settings.tabScene === "fixed" && sceneBase === "room" && settings.roomView === "weather" && roomWeatherSeen !== kind) {
    roomWeatherSeen = kind; applyTabScene();
  }
  drops = [];
  fogTexture = null;
  const area = innerWidth * innerHeight;
  if (kind === "rain" || kind === "storm") {
    const n = Math.round(area / (kind === "storm" ? 5500 : 9000));
    for (let i = 0; i < n; i++) drops.push({ x: Math.random() * innerWidth, y: Math.random() * innerHeight, len: rand(10, 20), speed: rand(650, 1000) });
  } else if (kind === "snow") {
    const n = Math.round(area / 14000);
    for (let i = 0; i < n; i++) drops.push({ x: Math.random() * innerWidth, y: Math.random() * innerHeight, r: rand(1, 2.6), speed: rand(25, 60), phase: Math.random() * 6 });
  } else if (kind === "fog") {
    fogTexture = buildFogTexture();
  }
  nextFlash = performance.now() + rand(6000, 15000);
}

function drawWeather(t, dt) {
  wctx.clearRect(0, 0, innerWidth, innerHeight);
  if (weatherKind === "rain" || weatherKind === "storm") {
    wctx.strokeStyle = "rgba(205, 198, 228, 0.32)";
    wctx.lineWidth = 1;
    wctx.beginPath();
    for (const d of drops) {
      d.y += d.speed * dt; d.x -= d.speed * dt * 0.12;
      if (d.y > innerHeight) { d.y = -d.len; d.x = Math.random() * innerWidth * 1.1; }
      wctx.moveTo(d.x, d.y);
      wctx.lineTo(d.x + d.len * 0.12, d.y - d.len);
    }
    wctx.stroke();
    if (weatherKind === "storm") {
      if (t > nextFlash) { flash = 1; nextFlash = t + rand(8000, 20000); }
      if (flash > 0) {
        wctx.fillStyle = `rgba(230, 225, 255, ${0.14 * flash})`;
        wctx.fillRect(0, 0, innerWidth, innerHeight);
        flash = Math.max(0, flash - dt * 3);
      }
    }
  } else if (weatherKind === "snow") {
    wctx.fillStyle = "rgba(240, 236, 248, 0.8)";
    wctx.beginPath();
    for (const d of drops) {
      d.y += d.speed * dt;
      d.x += Math.sin(t / 1200 + d.phase) * 12 * dt;
      if (d.y > innerHeight) { d.y = -4; d.x = Math.random() * innerWidth; }
      wctx.moveTo(d.x + d.r, d.y);
      wctx.arc(d.x, d.y, d.r, 0, Math.PI * 2);
    }
    wctx.fill();
  } else if (weatherKind === "fog" && fogTexture) {
    fogOffset = (fogOffset + dt * 12) % (fogTexture.width / 3);
    const y = innerHeight * 0.45;
    wctx.drawImage(fogTexture, -fogOffset, y);
    wctx.globalAlpha = 0.7;
    wctx.drawImage(fogTexture, -fogTexture.width / 3 + fogOffset * 0.6, y + 30);
    wctx.globalAlpha = 1;
  }
}

// ================= Stars (same sky every time, so constellations stay put) =================
const canvas = $("sky");
const ctx = canvas.getContext("2d");
function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
let seed = Number(localStorage.getItem("nightfall-seed"));
if (!seed) { seed = Math.floor(Math.random() * 1e9) + 1; try { localStorage.setItem("nightfall-seed", String(seed)); } catch {} }
const rng = mulberry32(seed);
const STAR_POOL = Array.from({ length: 700 }, () => ({
  u: rng(),
  v: Math.pow(rng(), 1.35) * 0.74,
  r: rng() < 0.08 ? 1.3 + rng() * 0.6 : 0.4 + rng() * 0.8,
  phase: rng() * Math.PI * 2,
  speed: 0.6 + rng() * 1.2
}));
let starCount = 0;
let shooter = null;
let nextShot = performance.now() + 5000;
let meteors = [];
let hoverStar = -1;

function starPos(i) { const s = STAR_POOL[i]; return { x: s.u * innerWidth, y: s.v * innerHeight }; }

function resize() {
  if (NightScene.active) NightScene.resize();
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  for (const c of [canvas, wcanvas]) { c.width = innerWidth * dpr; c.height = innerHeight * dpr; }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  wctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  starCount = Math.min(STAR_POOL.length, Math.round((innerWidth * innerHeight) / 5200));
  setWeather(weatherKind);
  if (reduceMotion) draw(0);
}

// ================= Constellations =================
let constellations = readJSON("nightfall-constellations", []);
let building = [];
function saveConstellations() { writeJSON("nightfall-constellations", constellations); }
function finishConstellation() {
  if (building.length >= 2) { constellations.push(building); saveConstellations(); }
  building = [];
}
function nearestStar(x, y, maxDist = 14) {
  let best = -1, bestD = maxDist * maxDist;
  for (let i = 0; i < starCount; i++) {
    const p = starPos(i);
    const d = (p.x - x) ** 2 + (p.y - y) ** 2;
    if (d < bestD) { bestD = d; best = i; }
  }
  return best;
}
function drawConstellations() {
  const lines = (list, alpha) => {
    ctx.strokeStyle = `rgba(214, 200, 238, ${alpha})`;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let i = 1; i < list.length; i++) {
      if (list[i - 1] >= starCount || list[i] >= starCount) continue;
      const a = starPos(list[i - 1]), b = starPos(list[i]);
      ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y);
    }
    ctx.stroke();
  };
  for (const c of constellations) lines(c, 0.35);
  if (building.length) {
    lines(building, 0.65);
    ctx.strokeStyle = "rgba(236, 228, 244, 0.7)";
    for (const i of building) { if (i < starCount) { const p = starPos(i); ctx.beginPath(); ctx.arc(p.x, p.y, 5, 0, Math.PI * 2); ctx.stroke(); } }
  }
  if (hoverStar >= 0) {
    const p = starPos(hoverStar);
    ctx.strokeStyle = "rgba(236, 228, 244, 0.55)";
    ctx.beginPath(); ctx.arc(p.x, p.y, 7, 0, Math.PI * 2); ctx.stroke();
  }
}

// ================= Meteor showers =================
const SHOWER_EVERY = { rare: [50, 70], sometimes: [10, 15], often: [3, 5] };
const SHOWER_SIZE = { gentle: [5, 8], medium: [10, 15], dramatic: [25, 35] };
function scheduleShower() {
  const range = SHOWER_EVERY[settings.showerFrequency];
  try {
    if (!range) { localStorage.removeItem("nightfall-next-shower"); return; }
    localStorage.setItem("nightfall-next-shower", String(Date.now() + rand(range[0], range[1]) * 60000));
  } catch {}
}
function nextShowerAt() {
  const v = Number(localStorage.getItem("nightfall-next-shower"));
  if (!v && SHOWER_EVERY[settings.showerFrequency]) { scheduleShower(); return Number(localStorage.getItem("nightfall-next-shower")); }
  return v;
}
function startShower(now) {
  const [lo, hi] = SHOWER_SIZE[settings.showerSize] || SHOWER_SIZE.medium;
  const count = Math.round(rand(lo, hi));
  const spread = Math.min(7000, 1200 + count * 150);
  const rx = innerWidth * rand(0.25, 0.75), ry = innerHeight * rand(0.04, 0.16);
  for (let i = 0; i < count; i++) {
    const angle = rand(0.2, Math.PI - 0.2);
    const dist = rand(30, 140), travel = rand(260, 520);
    meteors.push({
      x: rx + Math.cos(angle) * dist, y: ry + Math.sin(angle) * dist,
      vx: Math.cos(angle) * travel, vy: Math.sin(angle) * travel,
      tail: rand(0.35, 0.55), width: rand(1.2, 2.2),
      start: now + Math.random() * spread, duration: rand(700, 1200)
    });
  }
}
function checkShower(now) {
  if (reduceMotion || !SHOWER_EVERY[settings.showerFrequency] || document.visibilityState !== "visible") return;
  const at = nextShowerAt();
  if (at && Date.now() >= at) { scheduleShower(); if (NightScene.active) NightScene.shower(now); else startShower(now); }
}
function meteorHead(m, t) {
  const k = (t - m.start) / m.duration;
  return { x: m.x + m.vx * k, y: m.y + m.vy * k, k };
}
function drawStreak(hx, hy, tx, ty, alpha, width) {
  const grad = ctx.createLinearGradient(hx, hy, tx, ty);
  grad.addColorStop(0, "rgba(255,255,255,0.95)");
  grad.addColorStop(1, "rgba(236,228,244,0)");
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = grad;
  ctx.lineWidth = width;
  ctx.beginPath(); ctx.moveTo(hx, hy); ctx.lineTo(tx, ty); ctx.stroke();
  ctx.globalAlpha = 1;
}
function drawMeteors(t) {
  if (!meteors.length) return;
  meteors = meteors.filter((m) => t < m.start + m.duration);
  for (const m of meteors) {
    if (t < m.start) continue;
    const h = meteorHead(m, t);
    drawStreak(h.x, h.y, h.x - m.vx * m.tail, h.y - m.vy * m.tail, Math.sin(h.k * Math.PI), m.width);
  }
}
function drawShooter(t) {
  if (!shooter) return;
  const k = (t - shooter.start) / shooter.duration;
  if (k >= 1) { shooter = null; return; }
  const x = shooter.x - k * shooter.dx, y = shooter.y + k * shooter.dy;
  drawStreak(x, y, x + shooter.dx * 0.25, y - shooter.dy * 0.25, Math.sin(k * Math.PI), 1.6);
}

// ================= Sky clicks and hover =================
function isSkyTarget(el) {
  return !el.closest("a, button, input, textarea, select, label, form, #settings, #notes-wrap, #clock, #greeting, #thought");
}
document.addEventListener("click", (e) => {
  if (NightScene.active) return;
  if (!isSkyTarget(e.target)) return;
  const t = performance.now();
  const i = nearestStar(e.clientX, e.clientY);
  if (i >= 0) {
    if (building[building.length - 1] === i) finishConstellation();
    else building.push(i);
  } else if (building.length) {
    finishConstellation();
  }
  if (reduceMotion) draw(t);
});
let hoverPending = false, lastMouse = null;
document.addEventListener("mousemove", (e) => {
  if (NightScene.active) return;
  lastMouse = e;
  if (hoverPending) return;
  hoverPending = true;
  requestAnimationFrame(() => {
    hoverPending = false;
    const ev = lastMouse;
    const sky = isSkyTarget(ev.target);
    hoverStar = sky ? nearestStar(ev.clientX, ev.clientY) : -1;
    document.body.style.cursor = hoverStar >= 0 ? "pointer" : "";
    const label = $("moon-label");
    const p = moonPhase();
    const m = moonGeometry();
    if (sky && p !== null && (ev.clientX - m.x) ** 2 + (ev.clientY - m.y) ** 2 < (m.r * 1.3) ** 2) {
      label.textContent = settings.moonMode === "auto" ? moonName(p) : moonName(p) + " (locked)";
      label.style.left = m.x + "px";
      label.style.top = m.y + m.r + 10 + "px";
      label.hidden = false;
    } else {
      label.hidden = true;
    }
  });
});

// ================= Real constellations (now and then) =================
const REAL_CONSTELLATIONS = [
  { name: "Orion", pts: [0.5,0, 0.2,0.15, 0.75,0.2, 0.38,0.5, 0.5,0.47, 0.62,0.44, 0.3,0.88, 0.82,0.82], lines: [0,1, 0,2, 1,3, 2,5, 3,4, 4,5, 3,6, 5,7] },
  { name: "Big Dipper", pts: [0.95,0.25, 0.92,0.55, 0.66,0.62, 0.63,0.36, 0.44,0.3, 0.26,0.22, 0.02,0.34], lines: [0,1, 1,2, 2,3, 3,0, 3,4, 4,5, 5,6] },
  { name: "Little Dipper", pts: [0.02,0.1, 0.22,0.2, 0.4,0.32, 0.56,0.46, 0.82,0.4, 0.88,0.7, 0.62,0.76], lines: [0,1, 1,2, 2,3, 3,4, 4,5, 5,6, 6,3] },
  { name: "Cassiopeia", pts: [0,0.3, 0.25,0.75, 0.5,0.45, 0.75,0.8, 1,0.22], lines: [0,1, 1,2, 2,3, 3,4] },
  { name: "Cygnus", pts: [0.5,0, 0.5,0.4, 0.52,1, 0.08,0.34, 0.92,0.3], lines: [0,1, 1,2, 3,1, 1,4] },
  { name: "Lyra", pts: [0.5,0, 0.4,0.32, 0.62,0.36, 0.34,0.86, 0.58,0.92], lines: [0,1, 0,2, 1,2, 1,3, 2,4, 3,4] },
  { name: "Leo", pts: [0.1,0.82, 0.15,0.56, 0.26,0.36, 0.22,0.18, 0.12,0.05, 0.95,0.7, 0.66,0.46, 0.68,0.74], lines: [0,1, 1,2, 2,3, 3,4, 2,6, 6,5, 5,7, 7,0] },
  { name: "Scorpius", pts: [0,0.05, 0.06,0.2, 0.03,0.36, 0.2,0.24, 0.32,0.34, 0.42,0.5, 0.5,0.66, 0.56,0.82, 0.66,0.95, 0.82,0.98, 0.95,0.88, 0.92,0.74], lines: [0,3, 1,3, 2,3, 3,4, 4,5, 5,6, 6,7, 7,8, 8,9, 9,10, 10,11] },
  { name: "Southern Cross", pts: [0.5,0, 0.5,1, 0.15,0.45, 0.82,0.55], lines: [0,1, 2,3] },
  { name: "Pleiades", pts: [0.2,0.3, 0.38,0.22, 0.5,0.4, 0.66,0.3, 0.58,0.58, 0.4,0.62, 0.8,0.5], lines: [] }
];
const RC_IN = 3000, RC_HOLD = 22000, RC_OUT = 4000;
let realC = null;
let nextRealC = performance.now() + 25000 + Math.random() * 25000;
let lastRealC = -1;

function updateRealConstellation(t) {
  if (!settings.realConstellations) { realC = null; return; }
  if (realC) {
    if (t - realC.start > RC_IN + RC_HOLD + RC_OUT) { realC = null; nextRealC = t + 90000 + Math.random() * 150000; }
    return;
  }
  if (t < nextRealC) return;
  let pick;
  do { pick = Math.floor(Math.random() * REAL_CONSTELLATIONS.length); } while (pick === lastRealC);
  lastRealC = pick;
  const shape = REAL_CONSTELLATIONS[pick];
  // open sky on the left, or on the right below the moon, away from the clock and search bar
  let size = Math.min(innerWidth, innerHeight) * (0.2 + Math.random() * 0.07), cx, cy;
  if (Math.random() < 0.6) { cx = innerWidth * (0.08 + Math.random() * 0.13); cy = innerHeight * (0.14 + Math.random() * 0.24); }
  else { size *= 0.85; cx = innerWidth * (0.78 + Math.random() * 0.08); cy = innerHeight * (0.36 + Math.random() * 0.1); }
  const rot = (Math.random() - 0.5) * 0.6;
  const pts = [];
  let maxY = -Infinity;
  for (let i = 0; i < shape.pts.length; i += 2) {
    const px = (shape.pts[i] - 0.5) * size, py = (shape.pts[i + 1] - 0.5) * size;
    const x = cx + px * Math.cos(rot) - py * Math.sin(rot), y = cy + px * Math.sin(rot) + py * Math.cos(rot);
    pts.push([x, y]);
    maxY = Math.max(maxY, y);
  }
  realC = { shape, pts, label: [cx, maxY + 22], start: t };
}

function drawRealConstellation(t) {
  if (!realC) return;
  const age = t - realC.start;
  const weatherDim = weatherKind === "rain" || weatherKind === "storm" ? 0.5 : weatherKind === "fog" ? 0.65 : 1;
  const a = Math.max(0, Math.min(1, age / RC_IN, (RC_IN + RC_HOLD + RC_OUT - age) / RC_OUT)) * weatherDim * Math.max(0.4, STAR_BRIGHTNESS[skyPhase] ?? 1);
  if (a <= 0.01) return;
  const { shape, pts } = realC;
  const lineCount = shape.lines.length / 2;
  const drawn = Math.max(0, Math.min(lineCount, (age - 1500) / 4000 * lineCount));
  ctx.strokeStyle = `rgba(214, 200, 238, ${0.5 * a})`;
  ctx.lineWidth = 1.1;
  ctx.beginPath();
  for (let i = 0; i < lineCount; i++) {
    const part = drawn - i;
    if (part <= 0) break;
    const p1 = pts[shape.lines[i * 2]], p2 = pts[shape.lines[i * 2 + 1]];
    const f = Math.min(1, part);
    ctx.moveTo(p1[0], p1[1]);
    ctx.lineTo(p1[0] + (p2[0] - p1[0]) * f, p1[1] + (p2[1] - p1[1]) * f);
  }
  ctx.stroke();
  for (const [x, y] of pts) {
    ctx.globalAlpha = 0.18 * a;
    ctx.fillStyle = "#ece4f4";
    ctx.beginPath(); ctx.arc(x, y, 6, 0, Math.PI * 2); ctx.fill();
    ctx.globalAlpha = a;
    ctx.fillStyle = "#f8f4ff";
    ctx.beginPath(); ctx.arc(x, y, 2, 0, Math.PI * 2); ctx.fill();
  }
  ctx.globalAlpha = 0.7 * a;
  ctx.fillStyle = "#cbbfd6";
  ctx.font = "300 13px Sora, 'Segoe UI', sans-serif";
  ctx.textAlign = "center";
  ctx.fillText(shape.name, realC.label[0], realC.label[1]);
  ctx.globalAlpha = 1;
  ctx.textAlign = "start";
}

// ================= Drawing loop =================
function draw(t) {
  ctx.clearRect(0, 0, innerWidth, innerHeight);
  const weatherDim = weatherKind === "rain" || weatherKind === "storm" ? 0.3 : weatherKind === "fog" ? 0.5 : weatherKind === "snow" ? 0.6 : 1;
  const bright = (STAR_BRIGHTNESS[skyPhase] ?? 1) * weatherDim;
  ctx.fillStyle = "#ece4f4";
  for (let i = 0; i < starCount; i++) {
    const s = STAR_POOL[i];
    const tw = reduceMotion ? 0.8 : 0.55 + 0.45 * Math.sin(t / 1000 * s.speed + s.phase);
    ctx.globalAlpha = tw * bright;
    ctx.beginPath(); ctx.arc(s.u * innerWidth, s.v * innerHeight, s.r, 0, Math.PI * 2); ctx.fill();
  }
  ctx.globalAlpha = 1;
  drawMoon(ctx);
  drawConstellations();
  drawRealConstellation(t);
  drawShooter(t);
  drawMeteors(t);
}

let lastFrame = 0, lastShowerCheck = 0;
function loop(t) {
  const elapsed = t - lastFrame;
  if (NightScene.active && sceneStill) {
    if (elapsed >= 1000) { NightScene.draw(t, -1e9); lastFrame = t; }
  } else if (elapsed >= 33 && NightScene.active) {
    if (t - lastShowerCheck > 1000) { checkShower(t); lastShowerCheck = t; }
    NightScene.step(t, Math.min(elapsed, 100) / 1000, settings.shootingStars);
    NightScene.draw(t, -1e9);
    lastFrame = t;
  } else if (elapsed >= 33) {
    if (settings.shootingStars && !shooter && t > nextShot) {
      shooter = {
        x: innerWidth * (0.35 + Math.random() * 0.6), y: innerHeight * (0.05 + Math.random() * 0.25),
        dx: 260 + Math.random() * 160, dy: 110 + Math.random() * 60, start: t, duration: 900
      };
      nextShot = t + 7000 + Math.random() * 9000;
    }
    if (t - lastShowerCheck > 1000) { checkShower(t); lastShowerCheck = t; }
    updateRealConstellation(t);
    draw(t);
    drawWeather(t, Math.min(elapsed, 100) / 1000);
    lastFrame = t;
  }
  requestAnimationFrame(loop);
}

// ================= Daily notes =================
function loadNotes() {
  const data = readJSON("nightfall-notes", { todos: [], notes: {} });
  const today = todayKey();
  data.todos = (data.todos || []).filter((t) => !t.done || t.doneDate === today);
  const keys = Object.keys(data.notes || {}).sort().slice(-14);
  data.notes = Object.fromEntries(keys.map((k) => [k, data.notes[k]]));
  return data;
}
let notesData = loadNotes();
function saveNotes() { writeJSON("nightfall-notes", notesData); }

function placeNotes() {
  const wrap = $("notes-wrap");
  wrap.className = settings.notesPosition;
  if (settings.notesPosition === "pos-us") document.querySelector("main").append(wrap);
  else document.body.append(wrap);
}

function renderMini() {
  const mini = $("notes-mini");
  const open = notesData.todos.filter((t) => !t.done);
  const note = (notesData.notes[todayKey()] || "").trim();
  mini.replaceChildren();
  mini.classList.toggle("count", settings.notesStyle === "count");
  if (settings.notesStyle === "count") {
    mini.textContent = open.length
      ? `${open.length} ${open.length === 1 ? "task" : "tasks"}`
      : note ? "1 note" : "Notes";
    return;
  }
  if (!open.length && !note) { mini.textContent = "+ add a task"; return; }
  open.slice(0, 5).forEach((t) => {
    const line = document.createElement("div");
    line.textContent = "○ " + t.text;
    mini.append(line);
  });
  if (open.length > 5) {
    const more = document.createElement("div");
    more.textContent = `+${open.length - 5} more`;
    mini.append(more);
  }
  if (note) {
    const line = document.createElement("div");
    line.className = "note-line";
    line.textContent = note.split("\n")[0].slice(0, 60);
    mini.append(line);
  }
}

function renderTodos() {
  const list = $("todo-list");
  list.replaceChildren();
  for (const item of notesData.todos) {
    const li = document.createElement("li");
    if (item.done) li.className = "done";
    const box = document.createElement("input");
    box.type = "checkbox"; box.checked = item.done;
    box.setAttribute("aria-label", "Mark done: " + item.text);
    box.onchange = () => { item.done = box.checked; item.doneDate = box.checked ? todayKey() : null; saveNotes(); renderTodos(); };
    const span = document.createElement("span");
    span.textContent = item.text;
    const remove = document.createElement("button");
    remove.type = "button"; remove.className = "x"; remove.textContent = "×";
    remove.setAttribute("aria-label", "Remove " + item.text);
    remove.onclick = () => { notesData.todos = notesData.todos.filter((x) => x !== item); saveNotes(); renderTodos(); };
    li.append(box, span, remove);
    list.append(li);
  }
}

function openNotes() {
  notesData = loadNotes();
  $("notes-text").value = notesData.notes[todayKey()] || "";
  renderTodos();
  $("notes-mini").hidden = true;
  $("notes").hidden = false;
  $("settings").hidden = true;
  $("todo-text").focus();
}
function closeNotes() {
  notesData.notes[todayKey()] = $("notes-text").value;
  saveNotes();
  $("notes").hidden = true;
  $("notes-mini").hidden = false;
  renderMini();
  $("notes-mini").focus();
}

$("notes-mini").addEventListener("click", openNotes);
$("close-notes").addEventListener("click", closeNotes);
$("todo-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const input = $("todo-text");
  const text = input.value.trim();
  if (!text) { $("todo-error").textContent = "Type a task first."; input.focus(); return; }
  notesData.todos.push({ text, done: false, doneDate: null });
  saveNotes(); renderTodos();
  input.value = "";
});
$("todo-text").addEventListener("input", () => { $("todo-error").textContent = ""; });
let notesSaveTimer;
$("notes-text").addEventListener("input", () => {
  clearTimeout(notesSaveTimer);
  notesSaveTimer = setTimeout(() => { notesData.notes[todayKey()] = $("notes-text").value; saveNotes(); }, 400);
});

// ================= Settings panel =================
const panel = $("settings");
const openBtn = $("open-settings");
function syncOptions() {
  $("opt-24h").checked = settings.use24h;
  $("opt-shooting").checked = settings.shootingStars;
  $("opt-thought").checked = settings.showThought;
  $("opt-rotate").checked = settings.rotateMinute;
  $("opt-shower-freq").value = settings.showerFrequency;
  $("opt-shower-size").value = settings.showerSize;
  $("opt-sky").value = settings.skyMode;
  $("opt-moon").value = settings.moonMode;
  $("opt-weather").value = settings.weatherMode;
  $("city").value = settings.city ? settings.city.name : "";
  $("opt-notes-style").value = settings.notesStyle;
  $("opt-notes-pos").value = settings.notesPosition;
  $("opt-real-constellations").checked = settings.realConstellations;
  $("opt-tab-scene").value = settings.tabScene;
  $("opt-tab-scene-key").value = settings.tabSceneKey;
  $("opt-train").value = settings.trainEvery; $("opt-room").value = settings.roomView; $("opt-time").value = settings.timeOfDay;
  $("opt-click-stars").checked = settings.clickStars; $("opt-click-lights").checked = settings.clickLights;
  $("opt-click-water").checked = settings.clickWater; $("opt-parallax").checked = settings.mouseShift;
  syncSceneControls();
  updateWeatherStatus();
  updateMoonStatus();
}
openBtn.addEventListener("click", () => {
  panel.hidden = !panel.hidden;
  if (!panel.hidden) { if (!$("notes").hidden) closeNotes(); syncOptions(); $("new-name").focus(); }
});
$("close-settings").addEventListener("click", () => { panel.hidden = true; openBtn.focus(); });
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    if (!panel.hidden) { panel.hidden = true; openBtn.focus(); return; }
    if (!$("notes").hidden) { closeNotes(); return; }
    if (building.length) finishConstellation();
    return;
  }
  const typing = e.target.closest("input, textarea, select");
  if (!typing && !e.ctrlKey && !e.metaKey && !e.altKey && e.key.toLowerCase() === "m") userShower();
});
$("opt-24h").addEventListener("change", (e) => { settings.use24h = e.target.checked; saveSettings(); tick(); });
$("opt-shooting").addEventListener("change", (e) => { settings.shootingStars = e.target.checked; saveSettings(); });
$("opt-thought").addEventListener("change", (e) => { settings.showThought = e.target.checked; saveSettings(); showThought(); });
$("opt-rotate").addEventListener("change", (e) => { settings.rotateMinute = e.target.checked; saveSettings(); scheduleThoughts(); });
$("opt-shower-freq").addEventListener("change", (e) => { settings.showerFrequency = e.target.value; saveSettings(); scheduleShower(); });
$("opt-shower-size").addEventListener("change", (e) => { settings.showerSize = e.target.value; saveSettings(); });
$("opt-sky").addEventListener("change", (e) => { settings.skyMode = e.target.value; saveSettings(); updateSky(); });
$("opt-moon").addEventListener("change", (e) => { settings.moonMode = e.target.value; saveSettings(); });
$("opt-weather").addEventListener("change", (e) => {
  settings.weatherMode = e.target.value; saveSettings();
  setWeather(currentWeatherKind()); refreshWeather();
});
$("opt-notes-style").addEventListener("change", (e) => { settings.notesStyle = e.target.value; saveSettings(); renderMini(); });
$("opt-notes-pos").addEventListener("change", (e) => { settings.notesPosition = e.target.value; saveSettings(); placeNotes(); });
$("opt-real-constellations").addEventListener("change", (e) => { settings.realConstellations = e.target.checked; saveSettings(); });
$("opt-tab-scene").addEventListener("change", (e) => { settings.tabScene = e.target.value; saveSettings(); syncSceneControls(); applyTabScene(); });
$("opt-tab-scene-key").addEventListener("change", (e) => { settings.tabSceneKey = e.target.value; saveSettings(); applyTabScene(); });
$("clear-constellations").addEventListener("click", () => { constellations = []; building = []; saveConstellations(); });

function userShower() {
  if (NightScene.active) { NightScene.shower(performance.now()); return; }
  if (reduceMotion || meteors.length > 60) return;
  startShower(performance.now());
}
$("make-meteors").addEventListener("click", userShower);
$("preview-shower").addEventListener("click", () => { panel.hidden = true; userShower(); });

// ================= Start =================
tick();
setInterval(tick, 1000);
updateSky();
weatherKind = currentWeatherKind();
addEventListener("resize", resize);
resize();
renderLinks();
showThought();
placeNotes();
renderMini();
refreshWeather();
setInterval(() => { if (document.visibilityState === "visible") refreshWeather(); }, 10 * 60000);
if (!reduceMotion) requestAnimationFrame(loop);

// ================= Live scenes =================
(function fillSceneList() {
  const sel = $("opt-tab-scene-key");
  NightScene.KEYS.forEach((k, i) => { const o = document.createElement("option"); o.value = k; o.textContent = NightScene.NAMES[i]; sel.append(o); });
})();
function sceneOptions() {
  return {
    trainGap: settings.trainEvery === "loop" ? 0 : settings.trainEvery === "ten" ? 600 : 180,
    roomView: settings.roomView, weather: typeof weatherKind === "string" ? weatherKind : "clear",
    timeOfDay: settings.timeOfDay, lat: settings.city ? settings.city.lat : undefined, lon: settings.city ? settings.city.lon : undefined,
    stars: settings.clickStars, lights: settings.clickLights, water: settings.clickWater, parallax: settings.mouseShift
  };
}
// clicks on the scene itself (not on the search box, sites, notes or settings)
document.addEventListener("click", (e) => {
  if (!NightScene.active) return;
  if (e.target.closest("input, button, a, select, textarea, label, #settings, #notes, #notes-mini, form, .tile")) return;
  NightScene.click(e.clientX, e.clientY);
});
document.addEventListener("mousemove", (e) => { if (NightScene.active) NightScene.pointer((e.clientX / innerWidth) * 2 - 1, (e.clientY / innerHeight) * 2 - 1); });
$("opt-time").addEventListener("change", (e) => { settings.timeOfDay = e.target.value; saveSettings(); NightScene.setOptions(sceneOptions()); });
for (const [id, key] of [["opt-train", "trainEvery"], ["opt-room", "roomView"]]) $(id).addEventListener("change", (e) => { settings[key] = e.target.value; saveSettings(); applyTabScene(); });
for (const [id, key] of [["opt-click-stars", "clickStars"], ["opt-click-lights", "clickLights"], ["opt-click-water", "clickWater"], ["opt-parallax", "mouseShift"]])
  $(id).addEventListener("change", (e) => { settings[key] = e.target.checked; saveSettings(); NightScene.setOptions(sceneOptions()); });
function syncSceneControls() {
  $("opt-tab-scene-key").disabled = settings.tabScene !== "fixed";
  const grid = $("scene-grid"); if (grid) grid.classList.toggle("off", settings.tabScene !== "fixed");
}
async function applyTabScene() {
  if (settings.tabScene === "classic") { NightScene.stop(); document.body.classList.remove("scene-on"); return; }
  const key = settings.tabScene === "random" ? NightScene.KEYS[Math.floor(Math.random() * NightScene.KEYS.length)] : settings.tabSceneKey;
  sceneBase = key;
  if (sceneStarting) return;
  sceneStarting = true;
  try {
    await NightScene.start(key, $("scene"), sceneOptions());
    document.body.classList.add("scene-on");
  } catch {
    NightScene.stop(); document.body.classList.remove("scene-on");   // if a scene can't load, the classic sky stays
  } finally { sceneStarting = false; }
}
applyTabScene();

// ================= Settings tabs =================
(function settingsTabs() {
  const tabs = [...document.querySelectorAll(".settings-tabs [role=tab]")];
  const show = (name) => {
    tabs.forEach((t) => t.setAttribute("aria-selected", String(t.dataset.show === name)));
    document.querySelectorAll("#settings fieldset[data-tab]").forEach((f) => { f.hidden = f.dataset.tab !== name; });
    try { sessionStorage.setItem("nightfall-tab", name); } catch {}
  };
  tabs.forEach((t, i) => {
    t.addEventListener("click", () => show(t.dataset.show));
    t.addEventListener("keydown", (e) => {   // arrow keys move between tabs
      if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
      const next = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
      next.focus(); show(next.dataset.show);
    });
  });
  let start = "sites";
  try { start = sessionStorage.getItem("nightfall-tab") || "sites"; } catch {}
  show(start);
})();

// ================= Scene picker =================
function buildSceneGrid() {
  const grid = $("scene-grid"), sel = $("opt-tab-scene-key");
  grid.textContent = "";
  NightScene.KEYS.forEach((k, i) => {
    const b = document.createElement("button");
    b.type = "button"; b.setAttribute("role", "radio"); b.dataset.key = k;
    b.setAttribute("aria-checked", String(settings.tabSceneKey === k));
    const img = document.createElement("img"); img.src = `scenes/thumbs/${k}.jpg`; img.alt = ""; img.loading = "lazy";
    const label = document.createElement("span"); label.textContent = NightScene.NAMES[i];
    b.append(img, label);
    b.addEventListener("click", () => {
      sel.value = k; sel.dispatchEvent(new Event("change"));
      grid.querySelectorAll("button").forEach((x) => x.setAttribute("aria-checked", String(x === b)));
    });
    grid.append(b);
  });
}
buildSceneGrid();

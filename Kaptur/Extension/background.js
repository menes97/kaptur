// Kaptur — arka plan servisi (Chrome / Edge / Brave, MV3)
// Görevleri: Kaptur'un yerel köprüsüne indirme göndermek (tarayıcı çerezleriyle), uygulama kapalıysa
// native messaging ile başlatmak, sağ tık menüsü ve araç çubuğu rozetinde ilerleme.
'use strict';

const BASE = 'http://127.0.0.1:48214';
const NATIVE_HOST = 'com.kaptur.host';
// Sıradan web sayfaları bu başlığı CORS ön kontrolü olmadan gönderemez; köprü onsuz isteği reddeder.
const BRIDGE_HEADERS = { 'X-Kaptur-Extension': '1' };
const POST_URL = /^https:\/\/(www\.|m\.)?(instagram\.com\/([\w.]+\/)?(reels?|p|tv)\/[\w-]+|(x|twitter|mobile\.twitter)\.com\/\w+\/status\/\d+|(www\.|m\.|music\.)?youtube\.com\/(watch\?.*v=|shorts\/)[\w-]{6,}|youtu\.be\/[\w-]{6,})/i;
const CONTEXT_TARGET_TTL = 10_000;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- Köprü ----------
async function bridge(path, init = {}, timeoutMs = 5000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const r = await fetch(BASE + path, {
      cache: 'no-store', ...init, signal: ctrl.signal,
      headers: { ...BRIDGE_HEADERS, ...(init.headers || {}) },
    });
    const body = await r.json().catch(() => ({}));
    if (!r.ok || body.ok === false) throw new Error(body.error || `HTTP ${r.status}`);
    return body;
  } finally {
    clearTimeout(timer);
  }
}

const postJSON = (data) => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });

async function ping() {
  try { return await bridge('/ping', {}, 1200); } catch { return null; }
}

/// Uygulama çalışmıyorsa native messaging host'u (Kaptur.exe) ile başlatır ve köprü açılana kadar bekler.
async function ensureApp() {
  const app = await ping();
  if (app) return app;
  try {
    const r = await chrome.runtime.sendNativeMessage(NATIVE_HOST, { action: 'launchApp' });
    if (!r || !r.ok) throw new Error('başlatıcı hata döndürdü');
  } catch (e) {
    throw new Error('Kaptur çalışmıyor ve başlatılamadı. Uygulamayı bir kez elle açın, sonra tekrar deneyin.');
  }
  for (let i = 0; i < 40; i++) {
    await sleep(500);
    const ready = await ping();
    if (ready) return ready;
  }
  throw new Error('Kaptur başlatıldı ama yanıt vermedi.');
}

async function cookiesFor(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => ({
      name: c.name, value: c.value, domain: c.domain, path: c.path,
      secure: !!c.secure, httpOnly: !!c.httpOnly, expirationDate: c.expirationDate || 0,
    }));
  } catch {
    return [];   // bu site için çerez izni yok: çerezsiz gönder
  }
}

async function sendDownload(url, transcribe, referer) {
  if (!/^https?:\/\//i.test(url || '')) throw new Error('Bu sayfada indirilecek bir link yok.');
  await ensureApp();
  const cookies = await cookiesFor(url);
  // Yeni başlatılan uygulama ilk saniyelerde yavaş yanıt verebilir (ör. ilk açılışta virüs taraması);
  // cömert zaman aşımı. Tekrar gönderilse bile köprü aynı linki "zaten sırada" diye tekilleştirir.
  const r = await bridge('/download', postJSON({
    url, transcribe: !!transcribe, cookies, userAgent: navigator.userAgent, referer: referer || '',
  }), 20000);
  startProgress();
  return r;
}

// ---------- Rozet: ilerleme ----------
let progressTimer = null;
let hadActive = false;

async function setBadge(text, color) {
  try {
    await chrome.action.setBadgeText({ text });
    if (color) await chrome.action.setBadgeBackgroundColor({ color });
  } catch {}
}

async function tickProgress() {
  let items;
  try { items = (await bridge('/jobs', {}, 2000)).items || []; }
  catch { stopProgress(); await setBadge(''); return; }

  const active = items.filter((i) => i.active);
  if (active.length) {
    hadActive = true;
    const first = active[0];
    let text = '…';
    if (active.length > 1) text = active.length + '↓';
    else if (first.progress >= 0) text = Math.round(first.progress * 100) + '%';
    // İndirme mavi, transcript mor.
    await setBadge(text, first.state === 'transcribing' ? '#8a3ffc' : '#0b5cff');
    return;
  }
  stopProgress();
  if (hadActive) {
    hadActive = false;
    const failed = items[0] && items[0].state === 'failed';
    await setBadge(failed ? '!' : '✓', failed ? '#d93025' : '#1e8e3e');
    setTimeout(() => setBadge(''), 4000);
  }
}

function startProgress() {
  if (progressTimer) return;
  tickProgress();
  progressTimer = setInterval(tickProgress, 1500);   // her fetch, iş sürerken servis çalışanını uyanık tutar
}

function stopProgress() {
  if (progressTimer) { clearInterval(progressTimer); progressTimer = null; }
}

function notify(message, title = 'Kaptur') {
  try {
    chrome.notifications.create('', {
      type: 'basic', iconUrl: 'icons/icon128.png', title, message: String(message || '').slice(0, 240),
    });
  } catch {}
}

// ---------- Sağ tık menüsü ----------
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    const contexts = ['link', 'video', 'page'];
    chrome.contextMenus.create({ id: 'rg-download', title: 'Kaptur ile indir', contexts });
    chrome.contextMenus.create({ id: 'rg-download-transcript', title: 'Kaptur ile indir + transcript', contexts });
  });
});

/// Hangi linki indireceğiz? Sırasıyla: tıklanan link bir gönderiyse o; içerik betiğinin bulduğu gönderi
/// (X/Instagram akışında videonun üstüne sağ tıklandığında); başka bir link; en son sayfanın kendisi.
async function pickUrl(info) {
  if (info.linkUrl && POST_URL.test(info.linkUrl)) return info.linkUrl;
  const { contextTarget } = await chrome.storage.session.get('contextTarget');
  if (contextTarget && Date.now() - contextTarget.at < CONTEXT_TARGET_TTL && POST_URL.test(contextTarget.url))
    return contextTarget.url;
  if (info.linkUrl && /^https?:/i.test(info.linkUrl)) return info.linkUrl;
  return info.pageUrl;
}

chrome.contextMenus.onClicked.addListener(async (info) => {
  const transcribe = info.menuItemId === 'rg-download-transcript';
  try {
    const url = await pickUrl(info);
    const r = await sendDownload(url, transcribe, info.pageUrl);
    notify((r.alreadyQueued ? 'Zaten sırada: ' : transcribe ? 'İndirilecek + transcript: ' : 'İndiriliyor: ') + url);
  } catch (e) {
    notify(e.message || String(e), 'Kaptur — hata');
  }
});

// ---------- Açılır pencere ve içerik betiği mesajları ----------
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  // Eklentinin kendi sayfaları (açılır pencere) her şeyi isteyebilir; içerik betikleri yalnızca sağ tık hedefini bildirebilir.
  const fromPage = !(sender.id === chrome.runtime.id && (sender.url || '').startsWith(chrome.runtime.getURL('')));
  (async () => {
    if (msg.type === 'contextTarget') {
      await chrome.storage.session.set({ contextTarget: { url: String(msg.url || ''), at: Date.now() } });
      return { ok: true };
    }
    if (fromPage) throw new Error('izin verilmeyen mesaj');
    switch (msg.type) {
      case 'status': {
        const app = await ping();
        let items = [];
        if (app) {
          try { items = (await bridge('/jobs')).items || []; } catch {}
          if (items.some((i) => i.active)) startProgress();
        }
        return { app, items };
      }
      case 'download':
        return await sendDownload(msg.url, msg.transcribe, msg.referer);
      case 'action': {
        const r = await bridge('/action', postJSON({ id: msg.id, action: msg.action }));
        startProgress();
        return r;
      }
      case 'show':
        await ensureApp();
        return await bridge('/show', { method: 'POST' });
      default:
        throw new Error('bilinmeyen mesaj');
    }
  })().then(sendResponse, (e) => sendResponse({ ok: false, error: e.message || String(e) }));
  return true;   // yanıt asenkron
});

// Tarayıcı açıldığında süren bir iş varsa rozeti göster.
chrome.runtime.onStartup.addListener(async () => {
  const app = await ping();
  if (app && app.active > 0) startProgress();
});

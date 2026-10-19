// Kaptur — açılır pencere
'use strict';

const $ = (id) => document.getElementById(id);
const POST_URL = /^https:\/\/(www\.|m\.)?(instagram\.com\/([\w.]+\/)?(reels?|p|tv)\/[\w-]+|(x|twitter|mobile\.twitter)\.com\/\w+\/status\/\d+|(www\.|m\.|music\.)?youtube\.com\/(watch\?.*v=|shorts\/)[\w-]{6,}|youtu\.be\/[\w-]{6,})/i;
const SOCIAL_HOST = /(^|\.)(instagram\.com|x\.com|twitter\.com)$/i;

let tabUrl = '';

function send(msg) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage(msg, (r) => resolve(chrome.runtime.lastError ? { ok: false, error: chrome.runtime.lastError.message } : r));
  });
}

function showMessage(text, kind) {
  const m = $('message');
  m.textContent = text;
  m.className = 'message ' + (kind || '');
  m.hidden = !text;
}

function describeTab(url) {
  let u;
  try { u = new URL(url); } catch { return { site: 'İndirilemez sayfa', ok: false, hint: '' }; }
  if (!/^https?:$/.test(u.protocol)) return { site: 'İndirilemez sayfa', ok: false, hint: 'Bir Instagram, X ya da YouTube videosu açın.' };
  if (POST_URL.test(url)) {
    if (/youtu/.test(u.hostname)) return { site: u.pathname.startsWith('/shorts/') ? 'YouTube Shorts' : 'YouTube videosu', ok: true, hint: '' };
    const insta = u.hostname.includes('instagram');
    return { site: insta ? (/\/reels?\//.test(u.pathname) ? 'Instagram reel' : 'Instagram gönderisi') : 'X gönderisi', ok: true, hint: '' };
  }
  if (SOCIAL_HOST.test(u.hostname)) {
    return {
      site: u.hostname.includes('instagram') ? 'Instagram' : 'X', ok: false,
      hint: 'Akıştaki bir videoyu indirmek için gönderiye sağ tıklayıp "Kaptur ile indir"i seçin ya da gönderiyi açın.',
    };
  }
  return { site: u.hostname.replace(/^www\./, ''), ok: true, hint: 'yt-dlp bu siteyi destekliyorsa video iner.' };
}

async function download(transcribe) {
  $('dl').disabled = $('dltr').disabled = true;
  showMessage('Gönderiliyor…');
  const r = await send({ type: 'download', url: tabUrl, transcribe, referer: tabUrl });
  if (r && r.ok) {
    showMessage(r.alreadyQueued ? 'Bu video zaten sırada.' : (transcribe ? 'İndiriliyor, ardından transcript çıkarılacak.' : 'İndiriliyor.'), 'ok');
  } else {
    showMessage((r && r.error) || 'Gönderilemedi.', 'error');
  }
  $('dl').disabled = $('dltr').disabled = false;
  refresh();
}

function actionButton(label, job, action) {
  const b = document.createElement('button');
  b.className = 'small';
  b.textContent = label;
  b.addEventListener('click', async () => {
    const r = await send({ type: 'action', id: job.id, action });
    if (r && r.ok === false) showMessage(r.error, 'error');
    refresh();
  });
  return b;
}

function renderJobs(items) {
  const list = $('jobs');
  list.replaceChildren();
  $('empty').hidden = items.length > 0;
  for (const job of items.slice(0, 6)) {
    const li = document.createElement('li');

    const title = document.createElement('div');
    title.className = 'job-title';
    const tag = document.createElement('span');
    tag.className = 'site-tag';
    tag.textContent = job.site;
    title.append(tag, document.createTextNode(job.title || job.source));
    title.title = job.source;
    li.append(title);

    if (job.active) {
      const bar = document.createElement('div');
      bar.className = 'bar' + (job.progress < 0 ? ' indeterminate' : '');
      const fill = document.createElement('span');
      fill.style.width = Math.max(0, Math.round(job.progress * 100)) + '%';
      bar.append(fill);
      li.append(bar);
    }

    const status = document.createElement('div');
    status.className = 'job-status' + (job.state === 'failed' ? ' failed' : job.state === 'done' ? ' done' : '');
    status.textContent = job.status;
    status.title = job.status;
    li.append(status);

    const actions = document.createElement('div');
    actions.className = 'actions';
    if (job.hasFile) actions.append(actionButton('Videoyu aç', job, 'open'), actionButton('Klasör', job, 'reveal'));
    if (job.hasTranscript) actions.append(actionButton('Metni aç', job, 'openTranscript'));
    if (!job.active && job.hasFile && !job.hasTranscript) actions.append(actionButton('Transcript çıkar', job, 'transcribe'));
    if (job.state === 'failed' || job.state === 'canceled') actions.append(actionButton('Tekrar dene', job, 'retry'));
    if (job.active) actions.append(actionButton('İptal', job, 'cancel'));
    if (actions.childElementCount) li.append(actions);

    list.append(li);
  }
}

async function refresh() {
  const r = await send({ type: 'status' });
  const conn = $('conn');
  if (r && r.app) {
    conn.textContent = 'Bağlı';
    conn.className = 'pill on';
    conn.title = '';
    $('empty').textContent = 'Henüz indirme yok.';
    renderJobs(r.items || []);
  } else {
    conn.textContent = 'Kapalı';
    conn.className = 'pill';
    conn.title = 'İndir\'e basınca Kaptur otomatik başlatılır.';
    renderJobs([]);
    $('empty').textContent = 'Kaptur kapalı. İndir\'e bastığınızda başlatılır.';
  }
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabUrl = (tab && tab.url) || '';
  const d = describeTab(tabUrl);
  $('site').textContent = d.site;
  $('target').textContent = d.ok ? tabUrl : '';
  $('target').title = tabUrl;
  $('hint').textContent = d.hint;
  $('dl').disabled = $('dltr').disabled = !d.ok;

  $('dl').addEventListener('click', () => download(false));
  $('dltr').addEventListener('click', () => download(true));
  $('openApp').addEventListener('click', async () => {
    const r = await send({ type: 'show' });
    if (r && r.ok === false) showMessage(r.error, 'error');
  });

  await refresh();
  setInterval(refresh, 1000);
}

init();

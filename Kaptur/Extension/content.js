// Kaptur — içerik betiği (yalnızca Instagram ve X)
// Akışta bir videonun üstüne sağ tıklandığında Chrome bize gönderi linkini vermez (video blob: adresidir,
// sayfa adresi de ana akıştır). Sağ tıklanan öğenin ait olduğu gönderinin kalıcı linkini bulup
// arka plana bildiririz; sağ tık menüsü onu kullanır.
(() => {
  'use strict';

  const POST_PATH = /\/(status\/\d+|(reels?|p|tv)\/[\w-]+)\/?$/;

  function permalinkFor(el) {
    const article = el.closest('article') || el.closest('[role="dialog"]') || el.closest('main');
    if (!article) return null;
    // X: zaman damgası gönderinin kalıcı linkidir. Instagram: /p/… ya da /reel/… linki.
    const candidates = [
      ...article.querySelectorAll('a[href*="/status/"] time'),
    ].map((t) => t.closest('a'));
    candidates.push(...article.querySelectorAll('a[href*="/status/"], a[href*="/reel/"], a[href*="/reels/"], a[href*="/p/"], a[href*="/tv/"]'));
    for (const a of candidates) {
      if (!a) continue;
      const url = new URL(a.getAttribute('href'), location.href);
      if (POST_PATH.test(url.pathname)) return url.origin + url.pathname;
    }
    return null;
  }

  document.addEventListener('contextmenu', (e) => {
    const target = e.target instanceof Element ? e.target : null;
    if (!target) return;
    const onPostPage = POST_PATH.test(location.pathname);
    // Instagram gönderi sayfasında öneri linkleri de olur: sayfa adresi güvenilir. X'te ise bir gönderinin
    // sayfasında yanıtlardaki videolar da vardır: önce sağ tıklanan öğenin kendi gönderisine bak.
    const url = location.hostname.endsWith('instagram.com')
      ? (onPostPage ? location.origin + location.pathname : permalinkFor(target))
      : (permalinkFor(target) || (onPostPage ? location.origin + location.pathname : null));
    if (!url) return;
    try { chrome.runtime.sendMessage({ type: 'contextTarget', url }); } catch {}
  }, true);
})();

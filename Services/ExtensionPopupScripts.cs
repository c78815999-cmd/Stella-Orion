namespace StellaOrion.Services;

internal static class ExtensionPopupScripts
{
    public const string DocumentCreated = """
(() => {
  if (window.__stellaPopupFit) return;
  window.__stellaPopupFit = true;

  const injectStyle = () => {
    let style = document.getElementById('stella-popup-fit');
    if (!style) {
      style = document.createElement('style');
      style.id = 'stella-popup-fit';
      (document.head || document.documentElement).appendChild(style);
    }
    style.textContent = `
      html, body {
        width: auto !important;
        height: auto !important;
        min-width: 0 !important;
        min-height: 0 !important;
        max-width: none !important;
        max-height: none !important;
        overflow: hidden !important;
        margin: 0 !important;
        padding: 0 !important;
      }
    `;
  };

  const measure = () => {
    injectStyle();
    const body = document.body;
    const root = document.documentElement;
    if (!body || !root) return { width: 320, height: 120 };

    let left = Infinity;
    let top = Infinity;
    let right = 0;
    let bottom = 0;
    let found = false;

    const nodes = body.querySelectorAll('*');
    for (const el of nodes) {
      const style = window.getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue;
      const rect = el.getBoundingClientRect();
      if (rect.width < 1 && rect.height < 1) continue;
      found = true;
      left = Math.min(left, rect.left);
      top = Math.min(top, rect.top);
      right = Math.max(right, rect.right);
      bottom = Math.max(bottom, rect.bottom);
    }

    if (!found) {
      const rect = body.getBoundingClientRect();
      return {
        width: Math.max(1, Math.ceil(rect.width)),
        height: Math.max(1, Math.ceil(rect.height))
      };
    }

    return {
      width: Math.max(1, Math.ceil(right - left + 2)),
      height: Math.max(1, Math.ceil(bottom - top + 2))
    };
  };

  const postSize = () => {
    try {
      const size = measure();
      chrome.webview.postMessage({
        type: 'stella-popup-size',
        width: size.width,
        height: size.height
      });
    } catch {}
  };

  const boot = () => {
    injectStyle();
    postSize();
    try {
      new ResizeObserver(() => postSize()).observe(document.documentElement);
    } catch {}
    try {
      new MutationObserver(() => postSize()).observe(document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        characterData: true
      });
    } catch {}
    requestAnimationFrame(postSize);
    setTimeout(postSize, 60);
    setTimeout(postSize, 180);
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot, { once: true });
  } else {
    boot();
  }
})();
""";

    public const string MeasureNow = """
(() => {
  if (typeof window.__stellaPopupFit !== 'undefined' && window.__stellaPopupFit) {
    const style = document.getElementById('stella-popup-fit');
    if (style) style.disabled = false;
  }
  const body = document.body;
  if (!body) return { width: 320, height: 120 };
  let left = Infinity, top = Infinity, right = 0, bottom = 0, found = false;
  for (const el of body.querySelectorAll('*')) {
    const st = getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) continue;
    const r = el.getBoundingClientRect();
    if (r.width < 1 && r.height < 1) continue;
    found = true;
    left = Math.min(left, r.left);
    top = Math.min(top, r.top);
    right = Math.max(right, r.right);
    bottom = Math.max(bottom, r.bottom);
  }
  if (!found) {
    const r = body.getBoundingClientRect();
    return { width: Math.ceil(r.width) || 320, height: Math.ceil(r.height) || 120 };
  }
  return { width: Math.ceil(right - left + 2), height: Math.ceil(bottom - top + 2) };
})()
""";
}

// First-visit language routing for the suffix-i18n docs site.
//
// The site ships paired locales: "/" is English (default), "/zh/" is Chinese,
// and until now the only switch has been the manual Material nav selector. This
// script adds the one missing hop: on a visitor's FIRST contact (no recorded
// preference) a browser set to Chinese is moved from an English page to the
// same page in Chinese — through the theme's own locale link, so the target is
// right for every page depth and base URL. Everything else stays untouched:
// /zh/ deep links never bounce, any recorded choice wins forever, and an
// explicit switcher click is recorded so the routing never fights the user.

(function () {
  "use strict";

  var STORAGE_KEY = "vapor-lang";

  function storedChoice() {
    try { return window.localStorage.getItem(STORAGE_KEY); } catch (_) { return null; }
  }

  function recordChoice(locale) {
    try { window.localStorage.setItem(STORAGE_KEY, locale); } catch (_) { /* storage blocked: manual switching still works, just without memory */ }
  }

  function prefersChinese() {
    var preferred = (navigator.languages && navigator.languages.length > 0)
      ? navigator.languages[0]
      : navigator.language;
    return /^zh/i.test(String(preferred || ""));
  }

  function onChinesePage() {
    // <html lang> is "en" on the default build and "zh" on the /zh/ build —
    // locale truth from the page itself, independent of base URL or depth.
    return /^zh/i.test(String(document.documentElement.lang || ""));
  }

  // The switcher is the only other writer: an explicit pick (either way)
  // outranks the browser locale forever after.
  document.addEventListener("click", function (event) {
    var anchor = event.target && event.target.closest ? event.target.closest("a[hreflang]") : null;
    if (anchor) { recordChoice(anchor.getAttribute("hreflang") || "en"); }
  }, true);

  if (onChinesePage()) { return; }
  if (storedChoice() !== null) { return; }
  if (!prefersChinese()) { return; }

  var twin = document.querySelector("a.md-select__link[hreflang='zh']")
    || document.querySelector("a[hreflang='zh']");
  if (twin) {
    recordChoice("zh"); // one routing per browser, never repeated
    location.replace(twin.href);
  }
})();

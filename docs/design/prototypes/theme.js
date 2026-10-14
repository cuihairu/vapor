/* Vapor 原型主题切换器：只改 data-theme 令牌，不动布局（docs/design/admin-ui.md） */
(function () {
  var THEMES = [
    ["steam", "蒸汽蓝 · 默认"],
    ["midnight", "午夜"],
    ["daylight", "日间"],
    ["ember", "暮橙"]
  ];
  var saved = null;
  try { saved = localStorage.getItem("vapor-proto-theme"); } catch (e) { /* 隐私模式忽略 */ }

  var picker = document.createElement("div");
  picker.className = "theme-picker";
  picker.setAttribute("role", "group");
  picker.setAttribute("aria-label", "主题切换");
  picker.innerHTML = '<div class="head">主题 / theme</div>';
  THEMES.forEach(function (t) {
    var b = document.createElement("button");
    b.type = "button";
    b.textContent = t[1];
    b.dataset.theme = t[0];
    b.setAttribute("aria-pressed", String(t[0] === (saved || "steam")));
    b.addEventListener("click", function () {
      apply(t[0]);
      Array.prototype.forEach.call(picker.querySelectorAll("button"), function (x) {
        x.setAttribute("aria-pressed", String(x === b));
      });
    });
    picker.appendChild(b);
  });
  document.body.appendChild(picker);

  function apply(name) {
    document.documentElement.dataset.theme = name;
    try { localStorage.setItem("vapor-proto-theme", name); } catch (e) { /* 忽略 */ }
  }
  if (saved) { apply(saved); }
})();

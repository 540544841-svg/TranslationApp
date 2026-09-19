/* 演示逻辑：主题 / toast / 模拟翻译 / 键帽录制 / hash 路由 */
(function () {
  var KEY = 'sv-theme';

  function store(get, val) {
    try {
      if (get) return localStorage.getItem(KEY);
      localStorage.setItem(KEY, val);
    } catch (e) { /* file:// 下不可用时退化为仅当前页生效 */ }
    return null;
  }

  function systemTheme() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function apply(t) {
    document.documentElement.setAttribute('data-theme', t);
  }

  function initTheme() {
    apply(store(true) || systemTheme());
    document.querySelectorAll('.js-theme-toggle').forEach(function (btn) {
      btn.innerHTML = iconHtml('sun') + iconHtml('moon');
      btn.classList.add('theme-toggle');
      btn.setAttribute('title', '切换浅/深主题');
      btn.addEventListener('click', function () {
        var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        apply(next);
        store(false, next);
        document.dispatchEvent(new CustomEvent('sv:theme', { detail: next }));
      });
    });
  }

  var toastEl, toastTimer;
  function toast(msg) {
    if (!toastEl) {
      toastEl = document.createElement('div');
      toastEl.className = 'toast acrylic';
      document.body.appendChild(toastEl);
    }
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('show'); }, 2000);
  }

  var dict = {
    'Hello': '你好',
    'Serendipity': '意外发现美好事物的运气',
    'The quick brown fox jumps over the lazy dog': '敏捷的棕色狐狸跳过懒狗',
    'Machine learning': '机器学习',
    'Where is the station?': '车站在哪里？',
    'Concurrency': '并发'
  };
  var firstCall = true;

  function translate(text) {
    var t = text.trim();
    return new Promise(function (resolve) {
      setTimeout(function () {
        var hit = dict[t] || dict[t.toLowerCase()];
        var out = { engine: 'Google' };
        if (firstCall) { out.fallback = 'Bing'; out.engine = 'Bing'; firstCall = false; }
        if (hit) { out.text = hit; }
        else {
          var seed = ['这是一个示例译文：用于预览界面排版效果。', '示例数据 · 真实译文将在引擎返回后显示于此。'];
          out.text = seed[t.length % seed.length];
          out.demo = true;
        }
        resolve(out);
      }, 600);
    });
  }

  function keyName(ev) {
    var k = ev.key;
    if (k === ' ') return 'Space';
    return k.length === 1 ? k.toUpperCase() : (k.charAt(0).toUpperCase() + k.slice(1));
  }
  function isMod(k) { return /^(alt|control|shift|meta)$/i.test(k); }

  function frameHtml(keys) {
    return keys.map(function (k) { return '<span class="kbd">' + k + '</span>'; }).join('<span class="t-caption">+</span>');
  }

  function recorder(el) {
    var taken = recorder._taken = recorder._taken || {};
    var id = el.getAttribute('data-hotkey');
    var current = (el.getAttribute('data-keys') || 'Alt+D').split('+');
    taken[id] = current.join('+');
    el.innerHTML = frameHtml(current);

    el.addEventListener('click', function () {
      el.classList.add('recording');
      el.innerHTML = '<span class="kbd">请按键…</span>';
      function onKey(ev) {
        ev.preventDefault();
        if (isMod(ev.key) || ev.key === 'Unidentified') return;
        document.removeEventListener('keydown', onKey, true);
        var mods = [];
        if (ev.altKey) mods.push('Alt');
        if (ev.ctrlKey) mods.push('Ctrl');
        if (ev.shiftKey) mods.push('Shift');
        if (!mods.length) mods.push('Ctrl');
        mods.push(keyName(ev));
        var combo = mods.join('+');
        var clash = Object.keys(taken).some(function (k) { return k !== id && taken[k] === combo; });
        taken[id] = combo;
        el.classList.remove('recording');
        el.classList.toggle('conflict', clash);
        el.innerHTML = frameHtml(mods);
        if (clash) toast('与其他热键冲突，请换一个');
      }
      document.addEventListener('keydown', onKey, true);
    });
  }
  window.initRecorders = function () {
    document.querySelectorAll('.js-hotkey').forEach(recorder);
  };

  function route(sections, def) {
    function show() {
      var id = (location.hash || '#' + def).slice(1);
      if (sections.indexOf(id) < 0) id = def;
      document.querySelectorAll('.js-nav-item').forEach(function (n) {
        n.setAttribute('aria-selected', String(n.getAttribute('data-sec') === id));
      });
      document.querySelectorAll('.js-section').forEach(function (s) {
        s.hidden = s.getAttribute('data-sec') !== id;
      });
      var head = document.querySelector('.js-sec-title');
      var nav = document.querySelector('.js-nav-item[aria-selected="true"]');
      if (head && nav) head.textContent = nav.getAttribute('data-title') || nav.textContent.trim();
    }
    window.addEventListener('hashchange', show);
    show();
  }

  /* 通用下拉：.select > .select-btn + .menu（menu 内含 .menu-item[data-value]） */
  window.initSelects = function () {
    document.querySelectorAll('.select').forEach(function (sel) {
      var btn = sel.querySelector('.select-btn');
      var menu = sel.querySelector('.menu');
      if (!btn || !menu) return;
      btn.addEventListener('click', function (ev) {
        ev.stopPropagation();
        var open = !menu.classList.contains('open');
        closeAllMenus();
        menu.classList.toggle('open', open);
        sel.classList.toggle('open', open);
      });
      menu.addEventListener('click', function (ev) {
        var item = ev.target.closest('.menu-item');
        if (!item) return;
        menu.querySelectorAll('.menu-item').forEach(function (i) { i.removeAttribute('aria-selected'); });
        if (!menu.hasAttribute('data-multi')) item.setAttribute('aria-selected', 'true');
        else item.toggleAttribute('aria-selected');
        var label = sel.querySelector('.select-label');
        if (label && !menu.hasAttribute('data-multi')) label.textContent = item.textContent.trim();
        closeAllMenus();
      });
    });
    document.addEventListener('click', closeAllMenus);
    function closeAllMenus() {
      document.querySelectorAll('.menu.open').forEach(function (m) { m.classList.remove('open'); });
      document.querySelectorAll('.select.open').forEach(function (s) { s.classList.remove('open'); });
    }
  };

  window.initSliders = function () {
    document.querySelectorAll('input[type="range"].slider').forEach(function (r) {
      function fill() {
        var p = (r.value - r.min) / (r.max - r.min) * 100;
        r.style.setProperty('--fill', p + '%');
        var out = r.parentNode.querySelector('.js-slider-out');
        if (out) out.textContent = Math.round(p) + '%';
      }
      r.addEventListener('input', fill);
      fill();
    });
  };

  window.initSwitchSync = function () {
    document.querySelectorAll('.switch input').forEach(function (i) {
      i.addEventListener('change', function () {
        document.dispatchEvent(new CustomEvent('sv:switch', { detail: { id: i.id, checked: i.checked } }));
      });
    });
  };

  window.Preview = {
    initTheme: initTheme, toast: toast, translate: translate, route: route,
    dict: dict, frameHtml: frameHtml,
    demoTexts: ['Hello', 'Serendipity', 'Machine learning', 'Where is the station?', '随便输入一段没有词库命中的文字试试', 'Concurrency']
  };
})();

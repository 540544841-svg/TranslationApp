/* 内嵌 SVG 图标（24px 线性，stroke=currentColor） */
(function () {
  function svg(body, extra) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" ' +
      'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"' + (extra || '') + '>' + body + '</svg>';
  }
  window.Icons = {
    copy: svg('<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1"/>'),
    star: svg('<path d="m12 3 2.7 5.6 6.1.8-4.5 4.2 1.1 6.1-5.4-3-5.4 3 1.1-6.1L3.2 9.4l6.1-.8Z"/>'),
    starFill: svg('<path d="m12 3 2.7 5.6 6.1.8-4.5 4.2 1.1 6.1-5.4-3-5.4 3 1.1-6.1L3.2 9.4l6.1-.8Z" fill="currentColor"/>'),
    pin: svg('<path d="M12 3v6m0 0-2.5 2.5H6L12 21l6-9.5h-3.5Z"/>'),
    sound: svg('<path d="M4 10v4h3l5 4V6L7 10Z"/><path d="M16 9a4 4 0 0 1 0 6"/><path d="M18.5 6.5a7.5 7.5 0 0 1 0 11"/>'),
    swap: svg('<path d="M7 8h12l-3-3m3 8H7l3 3"/>'),
    chevron: svg('<path d="m6 9 6 6 6-6"/>'),
    compare: svg('<path d="M12 3v18M5 7h4m-2-2v8a2 2 0 0 0 2 2h0a2 2 0 0 0 2-2V7m4 1h4m-2-2v10a2 2 0 0 0 2 2h0a2 2 0 0 0 2-2V5"/>'),
    close: svg('<path d="m5 5 14 14M19 5 5 19"/>'),
    search: svg('<circle cx="11" cy="11" r="6"/><path d="m20 20-4.5-4.5"/>'),
    trash: svg('<path d="M4 7h16M9 7V4h6v3m-8 0 1 13h8l1-13"/>'),
    tune: svg('<path d="M5 8h8m4 0h2M5 16h2m4 0h8"/><circle cx="15" cy="8" r="2"/><circle cx="9" cy="16" r="2"/>'),
    cloud: svg('<path d="M7 18a4 4 0 1 1 .8-7.9A5.5 5.5 0 0 1 18 9.5a3.8 3.8 0 0 1 .5 7.5Z"/>'),
    monitor: svg('<rect x="3" y="5" width="18" height="12" rx="2"/><path d="M9 21h6"/>'),
    keyboard: svg('<rect x="2" y="7" width="20" height="11" rx="2"/><path d="M6 11h.01M10 11h.01M14 11h.01M18 11h.01M6 14.5h.01M18 14.5h.01M9 14.5h6"/>'),
    image: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="1.6"/><path d="m5 18 5-5 3 3 2.5-2.5L21 18"/>'),
    text: svg('<path d="M5 6V4h14v2M12 4v16m-3 0h6"/>'),
    eye: svg('<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z"/><circle cx="12" cy="12" r="2.8"/>'),
    eyeOff: svg('<path d="m3 3 18 18M10 5.8A9.7 9.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17 17 0 0 1-3.3 4.1M6.4 7.6A16.6 16.6 0 0 0 2.5 12S6 18.5 12 18.5c1.5 0 2.8-.4 4-1"/>'),
    sun: svg('<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M4.9 4.9l1.4 1.4m11.4 11.4 1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/>'),
    moon: svg('<path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z"/>'),
    check: svg('<path d="m4.5 12.5 5 5 10-11"/>'),
    warning: svg('<path d="M12 3 2.5 20h19Z"/><path d="M12 9.5v4.5m0 3h.01"/>'),
    error: svg('<circle cx="12" cy="12" r="9"/><path d="M12 7.5v5m0 3.5h.01"/>'),
    info: svg('<circle cx="12" cy="12" r="9"/><path d="M12 11v5.5m0-8.5h.01"/>'),
    globe: svg('<circle cx="12" cy="12" r="9"/><path d="M3.5 9h17M3.5 15h17M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>'),
    play: svg('<path d="M8 5.5v13l11-6.5Z"/>'),
    zoomIn: svg('<circle cx="11" cy="11" r="6"/><path d="m20 20-4.5-4.5M8.5 11h5M11 8.5v5"/>'),
    plus: svg('<path d="M12 5v14M5 12h14"/>'),
    minus: svg('<path d="M5 12h14"/>'),
    history: svg('<path d="M4 12a8 8 0 1 0 2.3-5.6M4 4v3.5h3.5M12 8v4.5l3 1.8"/>'),
    book: svg('<path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v15H6.5A2.5 2.5 0 0 0 4 20.5Z"/><path d="M4 5.5v15"/>'),
    cog: svg('<circle cx="12" cy="12" r="3"/><path d="M12 2.8v2.4m0 13.6v2.4M2.8 12h2.4m13.6 0h2.4M5.3 5.3l1.7 1.7m10 10 1.7 1.7m0-13.4-1.7 1.7m-10 10-1.7 1.7"/>'),
    upload: svg('<path d="M12 16V4m-4 4 4-4 4 4M4 20h16"/>'),
    crop: svg('<path d="M6 2v14a2 2 0 0 0 2 2h14M2 6h14a2 2 0 0 1 2 2v14"/>'),
  };

  window.icon = function (name, cls) {
    var tpl = Icons[name] || Icons.info;
    var d = document.createElement('span');
    d.className = 'ic' + (cls ? ' ' + cls : '');
    d.innerHTML = tpl;
    d.style.display = 'inline-flex';
    var s = d.firstChild;
    if (s && cls) s.setAttribute('class', cls);
    return d;
  };
  window.iconHtml = function (name) { return Icons[name] || Icons.info; };
})();

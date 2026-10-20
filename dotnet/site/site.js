(function () {
  'use strict';
  var root = document.documentElement;
  var repo = document.body.getAttribute('data-repo');
  var base = 'https://github.com/' + repo + '/releases/latest/download/';

  // Все ссылки собираются из data-repo.
  document.querySelectorAll('a[data-file]').forEach(function (a) {
    a.href = base + a.getAttribute('data-file');
  });
  document.querySelectorAll('.url[data-url]').forEach(function (s) {
    s.textContent = base + s.getAttribute('data-url');
  });
  document.getElementById('src-link').href = 'https://github.com/' + repo;
  document.getElementById('rel-link').href = 'https://github.com/' + repo + '/releases';

  var texts = {
    ru: { rec: 'Подсвечена подходящая сборка', none: 'Выберите сборку для своей системы.', det: 'Определена система: ' },
    en: { rec: 'The matching build is highlighted', none: 'Pick the build for your system.', det: 'Detected system: ' }
  };
  var os = null, osName = '';

  function setLang(l) {
    root.lang = l;
    document.querySelectorAll('[data-set-lang]').forEach(function (a) {
      a.setAttribute('aria-current', a.getAttribute('data-set-lang') === l ? 'true' : 'false');
    });
    var d = document.getElementById('detected');
    d.textContent = os ? texts[l].det + osName + '. ' + texts[l].rec + '.' : texts[l].none;
    try { localStorage.setItem('kharos-lang', l); } catch (e) {}
  }

  function pickLang() {
    var h = location.hash.slice(1);
    if (h === 'ru' || h === 'en') return h;
    try {
      var s = localStorage.getItem('kharos-lang');
      if (s === 'ru' || s === 'en') return s;
    } catch (e) {}
    return (navigator.language || 'en').toLowerCase().indexOf('ru') === 0 ? 'ru' : 'en';
  }

  // Определение ОС: userAgentData.platform, запасной путь userAgent.
  function detect() {
    var ud = navigator.userAgentData;
    var p = ((ud && ud.platform) || '').toLowerCase();
    var ua = navigator.userAgent || '';
    if (p === 'windows' || (!p && /Windows/i.test(ua))) return { id: 'win-x64', name: 'Windows' };
    if (p === 'linux' || (!p && /Linux/i.test(ua) && !/Android/i.test(ua))) {
      var arm = /aarch64|arm64|armv8/i.test(ua);
      return { id: arm ? 'linux-arm64' : 'linux-x64', name: 'Linux ' + (arm ? 'arm64' : 'x64') };
    }
    return null; // macOS, телефоны, неизвестное: без подсветки
  }

  var d = detect();
  if (d) {
    os = d.id;
    osName = d.name;
    document.querySelectorAll('a.btn[data-os="' + os + '"]').forEach(function (a) {
      a.classList.add('match');
    });
  }

  setLang(pickLang());
  window.addEventListener('hashchange', function () {
    var h = location.hash.slice(1);
    if (h === 'ru' || h === 'en') setLang(h);
  });
})();

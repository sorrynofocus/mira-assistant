/* Pre-paint theme/font-size: allowlisted values only, constants via setAttribute, no storage writes. */
(function () {
  var themes = ['mira-dark', 'sovereign', 'system'];
  var fonts = ['small', 'default', 'large'];
  var colors = { 'mira-dark': '#121a2c', sovereign: '#000000' };
  var theme = 'mira-dark';
  var font = 'default';
  try {
    var storedTheme = localStorage.getItem('mira.theme');
    var storedFont = localStorage.getItem('mira.fontSize');
    if (themes.includes(storedTheme)) theme = storedTheme;
    if (fonts.includes(storedFont)) font = storedFont;
  } catch (e) {}
  try {
    var resolved =
      theme === 'system'
        ? window.matchMedia('(prefers-color-scheme: light)').matches
          ? 'sovereign'
          : 'mira-dark'
        : theme;
    var root = document.documentElement;
    root.setAttribute('data-theme', resolved);
    root.setAttribute('data-font-size', font);
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', colors[resolved]);
  } catch (e) {}
})();

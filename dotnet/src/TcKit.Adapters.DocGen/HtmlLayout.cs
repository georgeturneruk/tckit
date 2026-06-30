namespace TcKit.Adapters.DocGen;

/// <summary>
/// The shared HTML page shell (the analogue of the Python <c>base.html</c> Jinja layout): full CSS, the
/// theme toggle, the search-enabled sidebar, and the footer. Dynamic regions are spliced in through the
/// <c>@@TOKEN@@</c> placeholders.
/// </summary>
internal static class HtmlLayout
{
    internal const string TitleToken = "@@TITLE@@";
    internal const string ProjectNameToken = "@@PROJECT_NAME@@";
    internal const string NavToken = "@@NAV@@";
    internal const string ContentToken = "@@CONTENT@@";

    internal const string Template = """
<!DOCTYPE html>
<html lang="en" data-theme="dark">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>@@TITLE@@ — TcKit Docs</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=DM+Sans:wght@400;500;600&family=JetBrains+Mono:ital,wght@0,400;0,500;1,400&display=swap" rel="stylesheet">
  <style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

    /* ------------------------------------------------------------------ */
    /* Dark theme (default) */
    /* ------------------------------------------------------------------ */
    :root, [data-theme="dark"] {
      --bg:        #0E0C0D;
      --bg-panel:  #110C0B;
      --bg-code:   #1a1614;
      --border:    rgba(213,208,207,0.12);
      --accent:    #DA7557;
      --accent-lt: #E8987E;
      --text:      rgba(250,245,244,0.87);
      --text-dim:  rgba(250,245,244,0.54);
      --text-code: #d4d4d4;
      --font-body: 'JetBrains Mono', 'SF Mono', monospace;
      --font-head: 'DM Sans', system-ui, sans-serif;
      --shadow:    0 2px 8px rgba(0,0,0,0.4);
    }

    /* ------------------------------------------------------------------ */
    /* Light theme */
    /* ------------------------------------------------------------------ */
    [data-theme="light"] {
      --bg:        #f8f6f5;
      --bg-panel:  #ede9e7;
      --bg-code:   #f0ece9;
      --border:    rgba(30,20,15,0.12);
      --accent:    #C4573A;
      --accent-lt: #DA7557;
      --text:      rgba(30,20,15,0.87);
      --text-dim:  rgba(30,20,15,0.54);
      --text-code: #1e1e1e;
      --shadow:    0 2px 8px rgba(0,0,0,0.1);
    }

    html { font-size: 14px; }
    body {
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-body);
      line-height: 1.7;
      display: flex;
      min-height: 100vh;
      flex-direction: column;
    }

    .layout { display: flex; flex: 1; }

    /* ------------------------------------------------------------------ */
    /* Sidebar */
    /* ------------------------------------------------------------------ */
    nav {
      width: 260px;
      min-width: 260px;
      background: var(--bg-panel);
      border-right: 1px solid var(--border);
      padding: 1rem;
      position: sticky;
      top: 0;
      height: 100vh;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
    nav .site-name {
      font-family: var(--font-head);
      font-weight: 600;
      font-size: 1rem;
      color: var(--accent);
      margin-bottom: 0.75rem;
      display: block;
      text-decoration: none;
    }

    /* Search */
    #search-wrap { position: relative; margin-bottom: 0.75rem; }
    #search {
      width: 100%;
      background: var(--bg-code);
      border: 1px solid var(--border);
      border-radius: 4px;
      color: var(--text);
      font-family: var(--font-body);
      font-size: 0.78rem;
      padding: 0.35rem 0.6rem;
      outline: none;
    }
    #search:focus { border-color: var(--accent); }
    #search::placeholder { color: var(--text-dim); }
    #search-results {
      position: absolute;
      top: calc(100% + 4px);
      left: 0; right: 0;
      background: var(--bg-panel);
      border: 1px solid var(--border);
      border-radius: 4px;
      box-shadow: var(--shadow);
      z-index: 100;
      max-height: 260px;
      overflow-y: auto;
    }
    #search-results a {
      display: block;
      padding: 0.4rem 0.6rem;
      font-size: 0.78rem;
      color: var(--text);
      text-decoration: none;
      border-bottom: 1px solid var(--border);
    }
    #search-results a:last-child { border-bottom: none; }
    #search-results a:hover { background: rgba(218,117,87,0.1); color: var(--accent); }
    #search-results .sr-type { font-size: 0.65rem; color: var(--text-dim); margin-left: 0.3em; }
    #search-results .sr-desc { font-size: 0.7rem; color: var(--text-dim); display: block; }

    nav .nav-section {
      font-family: var(--font-head);
      font-size: 0.65rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--text-dim);
      margin: 0.75rem 0 0.25rem;
    }
    nav a {
      display: block;
      padding: 0.2rem 0.5rem;
      color: var(--text-dim);
      text-decoration: none;
      font-size: 0.82rem;
      border-radius: 3px;
      border-left: 2px solid transparent;
    }
    nav a:hover { color: var(--text); background: rgba(218,117,87,0.08); }
    nav a.active { color: var(--accent); border-left-color: var(--accent); }
    nav a.nav-special {
      color: var(--text-dim);
      font-style: italic;
      font-size: 0.78rem;
    }

    /* ------------------------------------------------------------------ */
    /* Header (top bar with toggle) */
    /* ------------------------------------------------------------------ */
    .top-bar {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      padding: 0.4rem 1rem;
      background: var(--bg-panel);
      border-bottom: 1px solid var(--border);
    }
    #theme-toggle {
      background: none;
      border: 1px solid var(--border);
      border-radius: 4px;
      color: var(--text-dim);
      cursor: pointer;
      padding: 0.25rem 0.5rem;
      font-size: 0.75rem;
      font-family: var(--font-head);
      display: flex;
      align-items: center;
      gap: 0.3em;
      transition: color 0.15s, border-color 0.15s;
    }
    #theme-toggle:hover { color: var(--accent); border-color: var(--accent); }
    #theme-toggle svg { width: 14px; height: 14px; fill: currentColor; }

    /* ------------------------------------------------------------------ */
    /* Main content */
    /* ------------------------------------------------------------------ */
    main {
      flex: 1;
      padding: 2rem 2.5rem;
      max-width: 900px;
      min-width: 0;
    }

    h1 { font-family: var(--font-head); font-size: 1.8rem; font-weight: 600; margin-bottom: 0.5rem; letter-spacing: -0.01em; }
    h2 { font-family: var(--font-head); font-size: 1.2rem; font-weight: 600; margin: 2rem 0 0.75rem; letter-spacing: -0.01em; }
    h3 { font-family: var(--font-head); font-size: 1rem; font-weight: 600; margin: 1.5rem 0 0.5rem; color: var(--text-dim); }
    p { margin-bottom: 0.75rem; }

    a { color: var(--accent); text-decoration: none; }
    a:hover { color: var(--accent-lt); text-decoration: underline; }

    hr { border: none; border-top: 1px solid var(--border); margin: 1.5rem 0; }

    /* ------------------------------------------------------------------ */
    /* Type badges */
    /* ------------------------------------------------------------------ */
    .badge {
      display: inline-block;
      font-size: 0.65rem;
      padding: 0.15em 0.5em;
      border-radius: 3px;
      font-family: var(--font-head);
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      vertical-align: middle;
    }
    .badge-fb     { background: rgba(86,156,214,0.2);  color: #569cd6; }
    .badge-fn     { background: rgba(78,201,176,0.2);  color: #4ec9b0; }
    .badge-prg    { background: rgba(220,220,170,0.2); color: #dcdcaa; }
    .badge-itf    { background: rgba(206,145,120,0.2); color: #ce9178; }
    .badge-gvl    { background: rgba(181,206,168,0.2); color: #b5cea8; }
    .badge-struct { background: rgba(197,134,192,0.2); color: #c586c0; }
    .badge-enum   { background: rgba(218,117,87,0.2);  color: #DA7557; }
    .badge-inout  { background: rgba(255,198,100,0.2); color: #e0a040; }
    .badge-get    { background: rgba(86,156,214,0.15); color: #569cd6; }
    .badge-set    { background: rgba(206,145,120,0.15);color: #ce9178; }
    .badge-pub    { background: rgba(78,201,176,0.15); color: #4ec9b0; }
    .badge-priv   { background: rgba(244,71,71,0.15);  color: #f44747; }
    .badge-prot   { background: rgba(220,220,170,0.15);color: #dcdcaa; }

    /* ------------------------------------------------------------------ */
    /* Tables */
    /* ------------------------------------------------------------------ */
    table { width: 100%; border-collapse: collapse; margin-bottom: 1rem; font-size: 0.85rem; }
    th { background: var(--bg-code); color: var(--text-dim); font-family: var(--font-head); font-weight: 600; font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.05em; padding: 0.5rem 0.75rem; text-align: left; border-bottom: 1px solid var(--border); }
    td { padding: 0.45rem 0.75rem; border-bottom: 1px solid var(--border); vertical-align: top; }
    tr:last-child td { border-bottom: none; }
    tr:hover td { background: rgba(218,117,87,0.04); }
    td code { font-size: 0.82rem; color: var(--accent-lt); }

    /* ------------------------------------------------------------------ */
    /* Code blocks */
    /* ------------------------------------------------------------------ */
    pre {
      background: var(--bg-code);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 1rem 1.25rem;
      overflow-x: auto;
      font-size: 0.8rem;
      color: var(--text-code);
      margin-bottom: 1rem;
    }
    code { font-family: var(--font-body); }

    /* ------------------------------------------------------------------ */
    /* Item cards (methods / properties) */
    /* ------------------------------------------------------------------ */
    .description { color: var(--text-dim); margin-bottom: 1rem; }
    .item-card { border: 1px solid var(--border); border-radius: 6px; margin-bottom: 1rem; overflow: hidden; }
    .item-card-header { background: var(--bg-panel); padding: 0.6rem 1rem; display: flex; align-items: center; gap: 0.5rem; font-family: var(--font-head); font-weight: 600; font-size: 0.9rem; flex-wrap: wrap; }
    .item-card-body { padding: 0.75rem 1rem; }
    .item-signature { display: inline-flex; align-items: baseline; min-width: 0; }
    .item-name { color: var(--text); }
    .item-return-type { color: var(--text-dim); font-size: 0.85rem; font-weight: 400; }
    .item-badges { display: inline-flex; align-items: center; gap: 0.3rem; margin-left: auto; flex-wrap: wrap; }
    .default-value { color: var(--text-dim); font-size: 0.78rem; }
    .empty { color: var(--text-dim); font-style: italic; font-size: 0.85rem; }

    /* ------------------------------------------------------------------ */
    /* Collapsible Implementation / Declaration */
    /* ------------------------------------------------------------------ */
    details.impl-details { margin-bottom: 1rem; }
    details.impl-details > summary {
      cursor: pointer;
      font-family: var(--font-head);
      font-weight: 600;
      font-size: 0.85rem;
      color: var(--text-dim);
      padding: 0.35rem 0;
      list-style: none;
      user-select: none;
    }
    details.impl-details > summary::-webkit-details-marker { display: none; }
    details.impl-details > summary::before { content: "\25B8\00a0"; color: var(--accent); }
    details.impl-details[open] > summary::before { content: "\25BE\00a0"; }
    details.impl-details > summary:hover { color: var(--text); }
    details.impl-details > pre { margin-top: 0.5rem; }
    details.declaration-details { margin-top: 2rem; padding-top: 1rem; border-top: 1px solid var(--border); }

    /* ------------------------------------------------------------------ */
    /* Footer */
    /* ------------------------------------------------------------------ */
    footer {
      text-align: right;
      padding: 1rem 2.5rem;
      font-size: 0.72rem;
      color: var(--text-dim);
      border-top: 1px solid var(--border);
    }
    footer a { color: var(--text-dim); }
    footer a:hover { color: var(--accent); }

    /* ------------------------------------------------------------------ */
    /* Sidebar disclosure (hidden on desktop, used as a drawer on mobile) */
    /* ------------------------------------------------------------------ */
    #sidebar > summary {
      cursor: pointer;
      font-family: var(--font-head);
      font-weight: 600;
      font-size: 0.85rem;
      color: var(--text);
      padding: 0.6rem 0.75rem;
      margin: -0.25rem -0.25rem 0.5rem -0.25rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: var(--bg-code);
      list-style: none;
      user-select: none;
    }
    #sidebar > summary::-webkit-details-marker { display: none; }
    #sidebar > summary::before { content: "\2630\00a0\00a0"; color: var(--accent); }
    #sidebar[open] > summary::before { content: "\2715\00a0\00a0"; }

    @media (min-width: 769px) {
      #sidebar > summary { display: none; }
    }

    /* ------------------------------------------------------------------ */
    /* Narrow viewport (mobile / portrait tablet) */
    /* ------------------------------------------------------------------ */
    @media (max-width: 768px) {
      html { font-size: 15px; }
      .layout { flex-direction: column; }
      nav {
        width: 100%;
        min-width: 0;
        position: static;
        height: auto;
        max-height: none;
        border-right: none;
        border-bottom: 1px solid var(--border);
        padding: 0.75rem 1rem;
      }
      main {
        padding: 1rem 1.1rem 2rem;
        max-width: 100%;
      }
      h1 { font-size: 1.4rem; }
      h2 { font-size: 1.05rem; margin: 1.5rem 0 0.5rem; }
      h3 { font-size: 0.9rem; }
      table { font-size: 0.78rem; display: block; overflow-x: auto; }
      td, th { padding: 0.4rem 0.55rem; word-break: break-word; overflow-wrap: anywhere; }
      pre { font-size: 0.72rem; padding: 0.7rem 0.85rem; }
      footer { padding: 0.75rem 1.1rem; text-align: left; }
      .top-bar { padding: 0.35rem 0.75rem; }
      .item-card-header { padding: 0.5rem 0.75rem; font-size: 0.85rem; }
      .item-card-body { padding: 0.65rem 0.75rem; }
      .item-badges { margin-left: 0; flex-basis: 100%; }
    }
  </style>
</head>
<body>

<div class="top-bar">
  <button id="theme-toggle" title="Toggle dark/light mode">
    <svg id="icon-moon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M21 12.79A9 9 0 1111.21 3a7 7 0 009.79 9.79z"/></svg>
    <svg id="icon-sun" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg" style="display:none"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3" stroke="currentColor" stroke-width="2"/><line x1="12" y1="21" x2="12" y2="23" stroke="currentColor" stroke-width="2"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64" stroke="currentColor" stroke-width="2"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78" stroke="currentColor" stroke-width="2"/><line x1="1" y1="12" x2="3" y2="12" stroke="currentColor" stroke-width="2"/><line x1="21" y1="12" x2="23" y2="12" stroke="currentColor" stroke-width="2"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36" stroke="currentColor" stroke-width="2"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22" stroke="currentColor" stroke-width="2"/></svg>
    <span id="theme-label">Light</span>
  </button>
</div>

<div class="layout">
<nav>
  <details id="sidebar" open>
    <summary>Navigation</summary>
    <a href="index.html" class="site-name">@@PROJECT_NAME@@</a>
    <a href="../index.html" class="nav-special">&laquo; Solution</a>

    <div id="search-wrap">
      <input id="search" type="search" placeholder="Search…" autocomplete="off" spellcheck="false">
      <div id="search-results" hidden></div>
    </div>

    <a href="hierarchy.html" class="nav-special">⬡ Hierarchy</a>

@@NAV@@
  </details>
</nav>

<main>
@@CONTENT@@
</main>
</div>

<footer>
  Built with <a href="https://tckit.org" target="_blank" rel="noopener">TcKit</a>
</footer>

<script>
(function() {
  /* ---- Sidebar drawer (mobile) ---- */
  /* Default to closed on narrow viewports so long nav lists do not push the
     content off-screen. Desktop CSS hides the <summary> entirely, so the
     `open` attribute is a no-op there. */
  var sidebar = document.getElementById('sidebar');
  if (sidebar && window.matchMedia('(max-width: 768px)').matches) {
    sidebar.removeAttribute('open');
  }

  /* ---- Theme toggle ---- */
  var root = document.documentElement;
  var btn = document.getElementById('theme-toggle');
  var moon = document.getElementById('icon-moon');
  var sun  = document.getElementById('icon-sun');
  var lbl  = document.getElementById('theme-label');

  function applyTheme(t) {
    root.dataset.theme = t;
    if (t === 'light') {
      moon.style.display = 'none'; sun.style.display = '';
      lbl.textContent = 'Dark';
    } else {
      sun.style.display = 'none'; moon.style.display = '';
      lbl.textContent = 'Light';
    }
  }

  var saved = localStorage.getItem('tckit-theme');
  var initial = saved || (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
  applyTheme(initial);

  btn.addEventListener('click', function() {
    var next = root.dataset.theme === 'dark' ? 'light' : 'dark';
    applyTheme(next);
    localStorage.setItem('tckit-theme', next);
  });

  /* ---- Search ---- */
  var input   = document.getElementById('search');
  var results = document.getElementById('search-results');
  var idx = null;
  var docs = [];

  function loadIndex(cb) {
    if (idx) { cb(); return; }
    fetch('search-index.json')
      .then(function(r) { return r.json(); })
      .then(function(data) {
        docs = data;
        idx = lunr(function() {
          this.ref('id');
          this.field('title', { boost: 10 });
          this.field('description', { boost: 3 });
          this.field('body');
          data.forEach(function(d) { this.add(d); }, this);
        });
        cb();
      })
      .catch(function() {});
  }

  function showResults(hits) {
    if (!hits.length) { results.hidden = true; return; }
    results.innerHTML = hits.slice(0, 8).map(function(h) {
      var d = docs.find(function(x) { return x.id === h.ref; }) || {};
      var desc = d.description ? '<span class="sr-desc">' + d.description.substring(0, 70) + '</span>' : '';
      return '<a href="' + h.ref + '.html">' + h.ref +
             '<span class="sr-type">' + (d.type || '') + '</span>' + desc + '</a>';
    }).join('');
    results.hidden = false;
  }

  input.addEventListener('input', function() {
    var q = input.value.trim();
    if (!q) { results.hidden = true; return; }
    loadIndex(function() {
      try { showResults(idx.search(q + '*')); }
      catch(e) { try { showResults(idx.search(q)); } catch(e2) {} }
    });
  });

  document.addEventListener('click', function(e) {
    if (!results.contains(e.target) && e.target !== input) results.hidden = true;
  });
})();
</script>
<!-- lunr.js loaded from CDN; search gracefully degrades if offline -->
<script src="https://cdn.jsdelivr.net/npm/lunr@2.3.9/lunr.min.js" defer></script>

</body>
</html>
""";
}

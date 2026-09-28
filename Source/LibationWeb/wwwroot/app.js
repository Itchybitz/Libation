/* Libation web portal - one page, no build step.
   Talks to /api (see Api/ApiEndpoints.cs) and listens on /api/events for live queue and scan updates. */
(() => {
  "use strict";

  // ---------- state ----------
  const state = {
    authed: false,
    allowAnonymous: false,
    view: (location.hash.replace("#", "") || "library"),
    status: null,
    scan: null,
    schedule: null,
    queue: { items: [], queued: 0, working: 0, completed: 0, failed: 0, skipped: 0, cancelled: 0 },
    counts: null,
    filter: { q: "", status: "new", sort: "added" },
    books: { items: [], total: 0, page: 0, loading: false },
    selection: new Set(),
    logs: null,
    login: { pending: null, last: null },
    locales: null,
    addAccount: null,
    paste: "",
    pasteFocus: false,
    loginBusy: false,
    loginError: null,
    sse: null,
    sseOpen: false,
  };

  const $app = document.getElementById("app");
  const PAGE_SIZE = 100;

  // ---------- api ----------
  class ApiError extends Error {
    constructor(status, code, message) { super(message); this.status = status; this.code = code; }
  }

  async function api(method, path, body) {
    const init = { method, credentials: "same-origin", headers: { Accept: "application/json" } };
    if (method !== "GET") {
      init.headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body === undefined ? {} : body);
    }
    let res;
    try { res = await fetch(path, init); }
    catch { throw new ApiError(0, "network", "Can't reach the portal. Check the connection."); }
    if (res.status === 204) return null;
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = null; }
    if (!res.ok) {
      const err = data && data.error ? data.error : {};
      if (res.status === 401 && !path.startsWith("/api/auth/")) { setAuthed(false); }
      throw new ApiError(res.status, err.code || "error", err.message || `Request failed (${res.status}).`);
    }
    return data;
  }

  // ---------- helpers ----------
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmtBytes = (n) => {
    if (!n || n <= 0) return "";
    if (n >= 1e9) return (n / 1e9).toFixed(2) + " GB";
    if (n >= 1e6) return (n / 1e6).toFixed(0) + " MB";
    return (n / 1e3).toFixed(0) + " KB";
  };
  const fmtLen = (min) => {
    if (!min) return "";
    const h = Math.floor(min / 60), m = min % 60;
    return h ? `${h}h ${m}m` : `${m}m`;
  };
  const fmtEta = (sec) => {
    if (sec == null || !isFinite(sec) || sec <= 0) return "";
    const s = Math.round(sec), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    return h ? `${h}h ${m}m left` : m ? `${m}m ${r}s left` : `${r}s left`;
  };
  const fmtWhen = (iso) => {
    if (!iso) return "";
    const d = new Date(iso), diff = (Date.now() - d.getTime()) / 1000;
    if (diff < 60) return "just now";
    if (diff < 3600) return `${Math.floor(diff / 60)} min ago`;
    if (diff < 86400) return `${Math.floor(diff / 3600)} h ago`;
    if (diff < 86400 * 14) return `${Math.floor(diff / 86400)} d ago`;
    return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
  };
  // fmtWhen's opposite, for the one thing the portal knows about the future: the next automatic scan.
  const fmtIn = (iso) => {
    if (!iso) return "";
    const diff = (new Date(iso).getTime() - Date.now()) / 1000;
    if (diff <= 60) return "in under a minute";
    if (diff < 3600) return `in ${Math.round(diff / 60)} min`;
    if (diff < 86400) return `in ${Math.round(diff / 3600)} h`;
    return `in ${Math.round(diff / 86400)} d`;
  };
  const fmtClock = (iso) => iso ? new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }) : "";
  const fmtDate = (iso) => iso ? new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }) : "";
  const statusBadge = (b) => {
    if (b.absent) return `<span class="badge warn">Not on Audible</span>`;
    switch (b.status) {
      case "Liberated": return `<span class="badge ok">Downloaded</span>`;
      case "Error": return `<span class="badge err">Error</span>`;
      case "PartialDownload": return `<span class="badge warn">Partial</span>`;
      default: return `<span class="badge new">Not downloaded</span>`;
    }
  };
  const jobBadge = (j) => ({
    Queued: `<span class="badge">Queued</span>`,
    Working: `<span class="badge new">${esc(j.step || "Working")}</span>`,
    Completed: `<span class="badge ok">Done</span>`,
    Failed: `<span class="badge err">Failed</span>`,
    Cancelled: `<span class="badge">Cancelled</span>`,
    Skipped: `<span class="badge warn">Skipped</span>`,
  }[j.status] || `<span class="badge">${esc(j.status)}</span>`);
  const kindLabel = (k) => ({ Download: "Download", Pdf: "PDF", ConvertMp3: "Convert to MP3" }[k] || k);
  const coverImg = (asin, pictureId, size, cls = "cover") => pictureId
    ? `<img class="${cls}" src="api/books/${encodeURIComponent(asin)}/cover?size=${size}" alt="" loading="lazy" onerror="this.replaceWith(Object.assign(document.createElement('div'),{className:'${cls} ph',textContent:'♪'}))">`
    : `<div class="${cls} ph">♪</div>`;

  function toast(message, kind = "") {
    const el = document.createElement("div");
    el.className = "toast " + kind;
    el.textContent = message;
    document.getElementById("toasts").appendChild(el);
    setTimeout(() => el.remove(), kind === "err" ? 6000 : 3500);
  }

  const icons = {
    library: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19.5V5a2 2 0 0 1 2-2h12v18H6a2 2 0 0 1-2-2z"/><path d="M8 7h8M8 11h8"/></svg>`,
    queue: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v12m0 0l-4-4m4 4l4-4"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/></svg>`,
    activity: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 12h4l3-8 4 16 3-8h4"/></svg>`,
    about: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M11 12h1v4h1"/></svg>`,
  };

  // ---------- auth ----------
  function setAuthed(v) {
    if (state.authed === v) return;
    state.authed = v;
    if (!v) { closeSse(); render(); }
  }

  async function checkAuth() {
    try {
      const s = await api("GET", "api/auth/status");
      state.allowAnonymous = !!s.allowAnonymous;
      state.authed = !!s.authenticated;
    } catch (e) { state.authed = false; }
  }

  // ---------- data loading ----------
  async function loadStatus() {
    try {
      state.status = await api("GET", "api/status");
      state.scan = state.status.scan;
      state.schedule = state.status.schedule;
      state.counts = state.status.counts;
      renderHeader();
      if (state.view === "activity" || state.view === "about") render();
    } catch (e) { if (e.status !== 401) toast(e.message, "err"); }
  }

  async function loadBooks(reset) {
    if (state.books.loading) return;
    state.books.loading = true;
    const page = reset ? 1 : state.books.page + 1;
    const f = state.filter;
    const qs = new URLSearchParams({ status: f.status, sort: f.q && f.sort === "added" ? "relevance" : f.sort, page, pageSize: PAGE_SIZE });
    if (f.q) qs.set("q", f.q);
    try {
      const data = await api("GET", "api/books?" + qs.toString());
      state.books = { items: reset ? data.items : state.books.items.concat(data.items), total: data.total, page: data.page, loading: false };
    } catch (e) {
      state.books.loading = false;
      if (e.status !== 401) toast(e.message, "err");
    }
    if (state.view === "library") renderLibraryList();
  }

  async function loadQueue() {
    try { state.queue = await api("GET", "api/queue"); if (state.view === "queue") render(); renderHeader(); }
    catch (e) { if (e.status !== 401) toast(e.message, "err"); }
  }

  async function loadLoginState(rerender = true) {
    try { state.login = await api("GET", "api/accounts/login"); }
    catch (e) { if (e.status !== 401) toast(e.message, "err"); return; }
    if (rerender && state.view === "activity") render();
  }

  // The marketplace list never changes while the portal runs; fetch it once for the Add account form.
  async function loadLocales() {
    if (state.locales) return;
    try { state.locales = await api("GET", "api/accounts/locales"); }
    catch { state.locales = [{ countryCode: "us", name: "us" }]; }
  }

  async function loadLogs() {
    try { state.logs = await api("GET", "api/logs?lines=300"); if (state.view === "activity") render(); }
    catch (e) { if (e.status !== 401) toast(e.message, "err"); }
  }

  // ---------- live events ----------
  function openSse() {
    if (state.sse) return;
    const es = new EventSource("api/events");
    state.sse = es;
    es.onopen = () => { state.sseOpen = true; renderHeader(); };
    es.onerror = async () => {
      state.sseOpen = false; renderHeader();
      // A 401 is invisible to EventSource; ask.
      try { const s = await api("GET", "api/auth/status"); if (!s.authenticated) { closeSse(); setAuthed(false); } } catch { /* offline */ }
    };
    es.addEventListener("hello", (e) => {
      const d = JSON.parse(e.data);
      state.scan = d.scan; state.queue = d.queue; state.counts = d.counts;
      renderHeader(); if (state.view === "queue") render();
    });
    es.addEventListener("queue", (e) => {
      state.queue = JSON.parse(e.data);
      renderHeader();
      if (state.view === "queue") renderQueueList();
    });
    es.addEventListener("job", (e) => {
      const job = JSON.parse(e.data);
      const idx = state.queue.items.findIndex((j) => j.id === job.id);
      if (idx >= 0) state.queue.items[idx] = job; else state.queue.items.unshift(job);
      if (state.view === "queue") patchJob(job);
    });
    es.addEventListener("scan", (e) => {
      const prev = state.scan;
      state.scan = JSON.parse(e.data);
      renderHeader();
      if (prev && prev.running && !state.scan.running) {
        toast(state.scan.error ? "Scan failed: " + state.scan.error : `Scan done: ${state.scan.newBooks} new of ${state.scan.totalProcessed}.`, state.scan.error ? "err" : "ok");
        loadStatus(); loadBooks(true);
      }
      if (state.view === "activity") render();
    });
    es.addEventListener("schedule", (e) => {
      // Sent when the schedule is saved - from this page or another one - and around an automatic scan.
      try { state.schedule = JSON.parse(e.data); } catch { return; }
      if (state.view === "activity") render();
    });
    es.addEventListener("library", () => { loadStatus(); if (state.view === "library") loadBooks(true); });
    es.addEventListener("accounts", (e) => {
      try { state.login = JSON.parse(e.data); } catch { /* keep what we have */ }
      // "Signed in" comes from AccountsSettings.json, which a finished sign-in has just rewritten.
      loadStatus();
      if (state.view === "activity") render();
    });
  }
  function closeSse() { if (state.sse) { state.sse.close(); state.sse = null; state.sseOpen = false; } }

  // ---------- actions ----------
  async function enqueue(asins, kind = "download", force = false) {
    try {
      const r = await api("POST", "api/queue", { asins, kind, force });
      const n = r.added.length;
      if (n) toast(`${n} ${n === 1 ? "title" : "titles"} queued.`, "ok");
      if (r.skipped.length && !n) toast(r.skipped.map((s) => s.reason).filter((v, i, a) => a.indexOf(v) === i).join(" "), "err");
      state.selection.clear();
      if (state.view === "library") renderLibraryList();
    } catch (e) { toast(e.message, "err"); }
  }

  // LibationProcessor defers a title Audible refused recently, and its skip message tells the user to try
  // now - so the queue row and the detail sheet both have to offer that. They share this one warning
  // because asking again inside an Audible throttle can push the wait further out, not shorten it.
  const refusalSkipPrefix = "Audible refused this title recently";
  function isRefusalSkip(job) { return job.status === "Skipped" && !!job.message && job.message.startsWith(refusalSkipPrefix); }

  // false when the user backs out, so a caller that closes a sheet afterwards can leave it open instead.
  async function forceAfterRefusal(asin) {
    if (!confirm("Libation is waiting because Audible refused this title recently.\n\nAsking again during an Audible throttle can extend the wait, so try once - not repeatedly.\n\nTry this title now, ignoring the wait?")) return false;
    await enqueue([asin], "download", true);
    return true;
  }

  async function enqueueAllNew() {
    const n = state.counts ? state.counts.notDownloaded : 0;
    if (!confirm(`Download everything that is not downloaded yet${n ? ` (${n} titles)` : ""}? Titles Audible refused recently are left out.`)) return;
    try {
      const r = await api("POST", "api/queue/new", { force: false });
      toast(r.added.length ? `${r.added.length} titles queued.` : "Nothing to download.", r.added.length ? "ok" : "");
      navigate("queue");
    } catch (e) { toast(e.message, "err"); }
  }

  async function startScan() {
    try { await api("POST", "api/scan", {}); toast("Scanning Audible..."); }
    catch (e) { toast(e.message, "err"); }
  }

  // The schedule is saved the moment a control changes - there is no Save button - so the two controls
  // send each other's current value along with their own.
  async function saveSchedule(change) {
    const s = state.schedule || {};
    const body = {
      scanMode: change.scanMode !== undefined ? change.scanMode : (s.mode || "manual"),
      scanEveryHours: change.scanEveryHours !== undefined ? change.scanEveryHours : (s.everyHours || 6),
      downloadAfterScan: change.downloadAfterScan !== undefined ? change.downloadAfterScan : !!s.downloadAfterScan,
    };
    try {
      const r = await api("PUT", "api/settings/portal", body);
      state.schedule = r.schedule;
      toast("Saved.");
    } catch (e) { toast(e.message, "err"); }
    if (state.view === "activity") render();
  }

  async function cancelJob(id) {
    try { await api("DELETE", "api/queue/" + encodeURIComponent(id)); }
    catch (e) { toast(e.message, "err"); }
  }

  async function clearFinished() {
    try { const r = await api("POST", "api/queue/clear", {}); toast(`Removed ${r.removed}.`); await loadQueue(); }
    catch (e) { toast(e.message, "err"); }
  }

  async function setBookStatus(asin, status) {
    try { await api("POST", `api/books/${encodeURIComponent(asin)}/status`, { status }); toast("Updated."); }
    catch (e) { toast(e.message, "err"); }
  }

  // ---------- accounts ----------
  async function startLogin(accountId, locale, deviceRegistration) {
    state.loginBusy = true; state.loginError = null; render();
    try {
      const r = await api("POST", "api/accounts/login", { accountId, locale, deviceRegistration: deviceRegistration || null });
      if (r && r.alreadySignedIn) {
        toast("Already signed in. Remove the account first to register it afresh.");
        state.addAccount = null;
      } else {
        state.login = { pending: r, last: state.login.last };
        state.paste = ""; state.addAccount = null;
      }
    } catch (e) {
      state.loginError = e.message;
      if (e.status === 409) await loadLoginState(false);
    } finally {
      state.loginBusy = false;
      render();
    }
  }

  async function finishLogin() {
    const flow = state.login.pending;
    if (!flow) return;
    const responseUrl = (state.paste || "").trim();
    if (!responseUrl) { state.loginError = "Paste the whole address your browser ended up on."; render(); return; }

    state.loginBusy = true; state.loginError = null; render();
    try {
      const r = await api("POST", "api/accounts/login/complete", { flowId: flow.flowId, responseUrl });
      if (r && r.status === "succeeded") {
        state.paste = "";
        toast("Signed in to Audible.", "ok");
        loadStatus();
      } else {
        state.loginError = (r && r.error) || "That sign-in did not complete.";
      }
      await loadLoginState(false);
    } catch (e) {
      state.loginError = e.message;
      if (e.status === 404) await loadLoginState(false);
    } finally {
      state.loginBusy = false;
      render();
    }
  }

  async function cancelLogin() {
    const flow = state.login.pending;
    if (!flow) return;
    state.loginBusy = true; render();
    try { await api("POST", "api/accounts/login/cancel", { flowId: flow.flowId }); }
    catch (e) { if (e.status !== 404) toast(e.message, "err"); }
    state.paste = ""; state.loginError = null; state.loginBusy = false;
    await loadLoginState(false);
    render();
  }

  async function removeAccount(accountId, locale) {
    if (!confirm(`Remove ${accountId} (${locale}) from Libation?\n\nBooks already downloaded are kept. You can add the account again afterwards.`)) return;
    try {
      await api("DELETE", `api/accounts/${encodeURIComponent(accountId)}?locale=${encodeURIComponent(locale)}`);
      toast("Account removed.", "ok");
      loadStatus();
    } catch (e) { toast(e.message, "err"); }
  }

  async function copyLoginUrl(url) {
    try {
      await navigator.clipboard.writeText(url);
      toast("Link copied.");
      return;
    } catch { /* the clipboard needs HTTPS or localhost; fall back to a selectable box */ }
    const box = document.getElementById("acct-url-fallback");
    if (!box) return;
    box.hidden = false;
    box.select();
    box.setSelectionRange(0, url.length);
    let copied = false;
    try { copied = document.execCommand("copy"); } catch { copied = false; }
    toast(copied ? "Link copied." : "Copy the selected link by hand.");
  }

  async function signOut() {
    try { await api("POST", "api/auth/logout", {}); } catch { /* ignore */ }
    closeSse(); state.authed = false; render();
  }

  function navigate(view) {
    state.view = view;
    if (location.hash !== "#" + view) history.replaceState(null, "", "#" + view);
    render();
    if (view === "library" && !state.books.page) loadBooks(true);
    if (view === "queue") loadQueue();
    if (view === "activity") { loadStatus(); loadLogs(); loadLoginState(); loadLocales(); }
    if (view === "about") loadStatus();
  }

  // ---------- rendering ----------
  function render() {
    if (!state.authed) { renderLogin(); return; }
    $app.innerHTML = `
      <header class="top">
        <div class="top-row">
          <div class="brand"><img src="icon.svg" alt=""><span>Libation</span></div>
          <div class="chips" id="chips"></div>
        </div>
      </header>
      <nav class="nav" id="nav"></nav>
      <main id="main"></main>`;
    renderHeader();
    const main = document.getElementById("main");
    ({ library: renderLibrary, queue: renderQueue, activity: renderActivity, about: renderAbout }[state.view] || renderLibrary)(main);
  }

  function renderHeader() {
    if (!state.authed) return;
    const chips = document.getElementById("chips"), nav = document.getElementById("nav");
    if (!chips || !nav) return;
    const q = state.queue, scan = state.scan;
    const active = (q.queued || 0) + (q.working || 0);
    const parts = [];
    if (scan && scan.running) parts.push(`<span class="chip busy"><i class="dot"></i>Scanning</span>`);
    if (q.working) parts.push(`<span class="chip busy"><i class="dot"></i>Downloading ${q.working}${q.queued ? ` · ${q.queued} waiting` : ""}</span>`);
    else if (q.queued) parts.push(`<span class="chip"><i class="dot"></i>${q.queued} queued</span>`);
    if (state.counts && state.counts.notDownloaded) parts.push(`<span class="chip on"><i class="dot"></i>${state.counts.notDownloaded} new</span>`);
    parts.push(`<span class="chip ${state.sseOpen ? "on" : "bad"}" title="${state.sseOpen ? "Live updates connected" : "Live updates disconnected"}"><i class="dot"></i>${state.sseOpen ? "Live" : "Offline"}</span>`);
    chips.innerHTML = parts.join("");
    nav.innerHTML = [["library", "Library"], ["queue", "Queue"], ["activity", "Activity"], ["about", "About"]].map(([v, label]) =>
      `<button type="button" data-view="${v}" class="${state.view === v ? "active" : ""}">${icons[v]}<span>${label}</span>${v === "queue" && active ? `<span class="count">${active}</span>` : ""}</button>`).join("");
    nav.querySelectorAll("button").forEach((b) => b.onclick = () => navigate(b.dataset.view));
  }

  function renderLogin() {
    $app.innerHTML = `
      <div class="login">
        <img src="icon.svg" alt="">
        <h1>Libation</h1>
        <p class="muted">Your Audible library, from anywhere.</p>
        <form id="login">
          <input class="input" type="password" name="password" placeholder="Portal password" autocomplete="current-password" required autofocus>
          <button class="btn primary" type="submit">Sign in</button>
          <div class="error" id="login-error" hidden></div>
        </form>
      </div>`;
    const form = document.getElementById("login");
    form.onsubmit = async (e) => {
      e.preventDefault();
      const err = document.getElementById("login-error");
      err.hidden = true;
      try {
        await api("POST", "api/auth/login", { password: form.password.value });
        state.authed = true;
        await boot();
      } catch (ex) { err.textContent = ex.message; err.hidden = false; }
    };
  }

  // library
  function renderLibrary(main) {
    const c = state.counts || {};
    const f = state.filter;
    const filters = [["new", `Not downloaded${c.notDownloaded != null ? ` (${c.notDownloaded})` : ""}`], ["all", `All${c.total != null ? ` (${c.total})` : ""}`],
      ["downloaded", "Downloaded"], ["error", "Errors"], ["pdf", "PDF missing"], ["absent", "Not on Audible"]];
    main.innerHTML = `
      ${state.status && !state.status.booksDirectoryExists ? `<div class="card banner"><strong>Books folder not found.</strong> <span class="small">${esc(state.status.booksDirectory || "(not set)")} - downloads will fail until it exists.</span></div>` : ""}
      <div class="card">
        <input class="input" id="q" type="search" placeholder="Search title, author, narrator, series or ASIN" value="${esc(f.q)}" autocomplete="off">
        <div class="filters" id="filters">${filters.map(([k, label]) => `<button type="button" class="filter ${f.status === k ? "active" : ""}" data-status="${k}">${label}</button>`).join("")}</div>
        <div class="toolbar">
          <select class="select" id="sort">
            ${[["added", "Newest first"], ["title", "Title"], ["author", "Author"], ["published", "Release date"], ["length", "Longest first"]].map(([v, l]) => `<option value="${v}" ${f.sort === v ? "selected" : ""}>${l}</option>`).join("")}
          </select>
          <span style="flex:1"></span>
          <button type="button" class="btn sm" id="scan-btn">Scan Audible</button>
          <button type="button" class="btn sm primary" id="all-new-btn" ${c.notDownloaded ? "" : "disabled"}>Download all new</button>
        </div>
      </div>
      <div id="list"></div>
      <div id="selbar"></div>`;
    const q = document.getElementById("q");
    let timer;
    q.oninput = () => { clearTimeout(timer); timer = setTimeout(() => { f.q = q.value.trim(); loadBooks(true); }, 300); };
    q.onkeydown = (e) => { if (e.key === "Enter") { clearTimeout(timer); f.q = q.value.trim(); loadBooks(true); } };
    document.getElementById("filters").querySelectorAll(".filter").forEach((b) => b.onclick = () => {
      f.status = b.dataset.status;
      main.querySelectorAll(".filter").forEach((x) => x.classList.toggle("active", x === b));
      loadBooks(true);
    });
    document.getElementById("sort").onchange = (e) => { f.sort = e.target.value; loadBooks(true); };
    document.getElementById("scan-btn").onclick = startScan;
    document.getElementById("all-new-btn").onclick = enqueueAllNew;
    renderLibraryList();
  }

  function renderLibraryList() {
    const list = document.getElementById("list");
    if (!list) return;
    const b = state.books;
    if (!b.items.length) {
      list.innerHTML = `<div class="empty">${b.loading ? "Loading..." : state.filter.status === "new" && !state.filter.q ? "Everything is downloaded. Scan Audible to look for new purchases." : "Nothing matches."}</div>`;
      renderSelbar();
      return;
    }
    list.innerHTML = `<div class="list">${b.items.map(bookRow).join("")}</div>
      ${b.items.length < b.total ? `<div class="more"><button type="button" class="btn" id="more">Show more (${b.total - b.items.length} left)</button></div>` : `<p class="muted small" style="text-align:center">${b.total} ${b.total === 1 ? "title" : "titles"}</p>`}`;
    list.querySelectorAll(".book").forEach((row) => {
      const asin = row.dataset.asin;
      row.querySelector("input").onchange = (e) => { e.target.checked ? state.selection.add(asin) : state.selection.delete(asin); row.classList.toggle("selected", e.target.checked); renderSelbar(); };
      row.querySelector(".text").onclick = () => openBook(asin);
      row.querySelector(".cover").onclick = () => openBook(asin);
      const dl = row.querySelector("[data-dl]");
      if (dl) dl.onclick = () => enqueue([asin], "download", dl.dataset.dl === "force");
      row.querySelector("[data-menu]").onclick = () => openBook(asin);
    });
    const more = document.getElementById("more");
    if (more) more.onclick = () => loadBooks(false);
    renderSelbar();
  }

  function bookRow(b) {
    const selected = state.selection.has(b.asin);
    const dlButton = b.absent ? "" : b.needsDownload
      ? `<button type="button" class="btn sm primary" data-dl="new">Download</button>`
      : b.status === "Liberated" ? `<button type="button" class="btn sm ghost" data-dl="force">Re-download</button>` : `<button type="button" class="btn sm" data-dl="force">Retry</button>`;
    return `<div class="book ${selected ? "selected" : ""}" data-asin="${esc(b.asin)}">
      <input type="checkbox" ${selected ? "checked" : ""} aria-label="Select ${esc(b.title)}">
      ${coverImg(b.asin, b.pictureId, 80)}
      <div class="text">
        <div class="title">${esc(b.titleWithSubtitle)}</div>
        <div class="sub">${esc(b.authors)}${b.series ? ` · ${esc(b.series)}` : ""}</div>
        <div class="meta">${statusBadge(b)}${b.needsPdf ? `<span class="badge">PDF missing</span>` : ""}${b.isPlus ? `<span class="badge">Plus</span>` : ""}<span class="small muted">${fmtLen(b.lengthMinutes)}${b.dateAdded ? ` · added ${fmtWhen(b.dateAdded)}` : ""}</span></div>
      </div>
      <div class="side">${dlButton}<button type="button" class="btn sm ghost icon" data-menu aria-label="More">⋯</button></div>
    </div>`;
  }

  function renderSelbar() {
    const bar = document.getElementById("selbar");
    if (!bar) return;
    const n = state.selection.size;
    if (!n) { bar.innerHTML = ""; return; }
    bar.innerHTML = `<div class="selbar">
      <strong>${n} selected</strong>
      <span style="flex:1"></span>
      <button type="button" class="btn sm primary" id="sel-dl">Download</button>
      <button type="button" class="btn sm" id="sel-force">Re-download</button>
      <button type="button" class="btn sm ghost" id="sel-clear">Clear</button>
    </div>`;
    document.getElementById("sel-dl").onclick = () => enqueue([...state.selection], "download", false);
    document.getElementById("sel-force").onclick = () => { if (confirm(`Re-download ${n} titles even if they are already on disk?`)) enqueue([...state.selection], "download", true); };
    document.getElementById("sel-clear").onclick = () => { state.selection.clear(); renderLibraryList(); };
  }

  async function openBook(asin) {
    let d;
    try { d = await api("GET", "api/books/" + encodeURIComponent(asin)); }
    catch (e) { toast(e.message, "err"); return; }
    const b = d.book;
    const bg = document.createElement("div");
    bg.className = "modal-bg";
    bg.innerHTML = `<div class="modal" role="dialog" aria-modal="true">
      <div class="head">
        ${coverImg(b.asin, b.pictureId, 500, "cover-lg")}
        <div style="min-width:0">
          <h2>${esc(b.titleWithSubtitle)}</h2>
          <div class="muted">${esc(b.authors)}</div>
          ${b.narrators ? `<div class="muted small">Read by ${esc(b.narrators)}</div>` : ""}
          ${b.series ? `<div class="small">${esc(b.series)}</div>` : ""}
          <div class="meta">${statusBadge(b)}<span class="small muted">${fmtLen(b.lengthMinutes)}${b.datePublished ? ` · ${fmtDate(b.datePublished)}` : ""}</span></div>
        </div>
      </div>
      <dl class="kv" style="margin-top:12px">
        <dt>ASIN</dt><dd class="mono">${esc(b.asin)}</dd>
        ${d.audioPath ? `<dt>File</dt><dd class="mono small">${esc(d.audioPath)}</dd>` : ""}
        ${b.lastDownloaded ? `<dt>Downloaded</dt><dd>${fmtDate(b.lastDownloaded)}</dd>` : ""}
        ${d.categories && d.categories.length ? `<dt>Genre</dt><dd>${esc(d.categories.join(", "))}</dd>` : ""}
        ${b.tags ? `<dt>Tags</dt><dd>${esc(b.tags)}</dd>` : ""}
      </dl>
      ${d.description ? `<div class="desc">${esc(d.description)}</div>` : ""}
      <div class="menu">
        ${b.absent ? "" : b.needsDownload ? `<button type="button" class="btn primary" data-act="download">Download</button>` : `<button type="button" class="btn" data-act="force">Re-download</button>`}
        ${!b.absent && b.needsDownload ? `<button type="button" class="btn ghost" data-act="force-deferred">Try now, ignoring the recent refusal</button>` : ""}
        ${b.hasPdf ? `<button type="button" class="btn" data-act="pdf">Download PDF</button>` : ""}
        ${b.status === "Liberated" ? `<button type="button" class="btn" data-act="convert">Convert to MP3</button>` : ""}
        ${b.status === "Liberated" ? `<button type="button" class="btn ghost" data-act="mark-not">Mark as not downloaded</button>` : `<button type="button" class="btn ghost" data-act="mark-done">Mark as downloaded</button>`}
        <button type="button" class="btn ghost" data-act="close">Close</button>
      </div>
    </div>`;
    const img = bg.querySelector(".cover-lg");
    if (img) { img.className = "cover"; img.style.width = "96px"; img.style.height = "96px"; img.style.borderRadius = "12px"; }
    const close = () => bg.remove();
    bg.onclick = (e) => { if (e.target === bg) close(); };
    bg.querySelectorAll("[data-act]").forEach((btn) => btn.onclick = async () => {
      const act = btn.dataset.act;
      if (act === "download") await enqueue([asin], "download", false);
      else if (act === "force") { if (confirm("Re-download this title even if it is already on disk?")) await enqueue([asin], "download", true); else return; }
      else if (act === "force-deferred") { if (!await forceAfterRefusal(asin)) return; }
      else if (act === "pdf") await enqueue([asin], "pdf", false);
      else if (act === "convert") await enqueue([asin], "convert", false);
      else if (act === "mark-not") await setBookStatus(asin, "NotLiberated");
      else if (act === "mark-done") await setBookStatus(asin, "Liberated");
      close();
    });
    document.body.appendChild(bg);
  }

  // queue
  function renderQueue(main) {
    const q = state.queue;
    main.innerHTML = `
      <div class="card row-between">
        <div><h2>Queue</h2><div class="muted small">${q.working || 0} working · ${q.queued || 0} waiting · ${q.completed || 0} done${q.failed ? ` · ${q.failed} failed` : ""}${q.skipped ? ` · ${q.skipped} skipped` : ""}</div></div>
        <div class="actions">
          <button type="button" class="btn sm" id="clear-btn" ${(q.completed || q.failed || q.skipped || q.cancelled) ? "" : "disabled"}>Clear finished</button>
        </div>
      </div>
      <div id="jobs"></div>`;
    document.getElementById("clear-btn").onclick = clearFinished;
    renderQueueList();
  }

  const jobOrder = { Working: 0, Queued: 1, Failed: 2, Skipped: 3, Cancelled: 4, Completed: 5 };
  function renderQueueList() {
    const box = document.getElementById("jobs");
    if (!box) return;
    const items = [...state.queue.items].sort((a, b) => (jobOrder[a.status] - jobOrder[b.status]) || (new Date(b.queuedAt) - new Date(a.queuedAt)));
    if (!items.length) { box.innerHTML = `<div class="empty">Nothing queued. Pick titles in the Library.</div>`; return; }
    box.innerHTML = `<div class="list">${items.map(jobRow).join("")}</div>`;
    box.querySelectorAll("[data-cancel]").forEach((b) => b.onclick = () => cancelJob(b.dataset.cancel));
    box.querySelectorAll("[data-force-asin]").forEach((b) => b.onclick = () => forceAfterRefusal(b.dataset.forceAsin));
    const clear = document.getElementById("clear-btn");
    if (clear) clear.disabled = !items.some((j) => !["Queued", "Working"].includes(j.status));
  }

  function jobRow(j) {
    const active = j.status === "Queued" || j.status === "Working";
    return `<div class="job ${j.status.toLowerCase()}" data-job="${esc(j.id)}">
      ${coverImg(j.asin, j.pictureId, 80)}
      <div style="min-width:0">
        <div class="title">${esc(j.title)}</div>
        <div class="sub">${esc(j.authors)}${j.kind !== "Download" ? ` · ${kindLabel(j.kind)}` : ""}${j.force ? " · re-download" : ""}</div>
        <div class="meta">${jobBadge(j)}<span class="small muted">${j.status === "Queued" ? "queued " + fmtWhen(j.queuedAt) : j.finishedAt ? fmtWhen(j.finishedAt) : ""}</span></div>
        <div class="bar" ${j.status === "Working" || j.status === "Completed" ? "" : "hidden"}><i style="width:${j.progress || 0}%"></i></div>
        <div class="stats" ${j.status === "Working" ? "" : "hidden"}>
          <span data-pct>${Math.round(j.progress || 0)}%</span>
          <span data-bytes>${j.totalBytes ? `${fmtBytes(j.bytesReceived)} / ${fmtBytes(j.totalBytes)}` : ""}</span>
          <span data-eta>${fmtEta(j.etaSeconds)}</span>
        </div>
        ${j.message ? `<div class="msg">${esc(j.message)}</div>` : ""}
        ${j.log && j.log.length ? `<details class="log"><summary>Log (${j.log.length})</summary><pre class="mono">${esc(j.log.join("\n"))}</pre></details>` : ""}
      </div>
      <div>${active ? `<button type="button" class="btn sm ghost danger" data-cancel="${esc(j.id)}">Cancel</button>` : isRefusalSkip(j) ? `<button type="button" class="btn sm" data-force-asin="${esc(j.asin)}">Try now</button>` : ""}</div>
    </div>`;
  }

  function patchJob(j) {
    const row = document.querySelector(`[data-job="${j.id}"]`);
    if (!row) { renderQueueList(); return; }
    if (!row.classList.contains(j.status.toLowerCase())) { renderQueueList(); return; }
    const bar = row.querySelector(".bar > i"); if (bar) bar.style.width = (j.progress || 0) + "%";
    const pct = row.querySelector("[data-pct]"); if (pct) pct.textContent = Math.round(j.progress || 0) + "%";
    const bytes = row.querySelector("[data-bytes]"); if (bytes) bytes.textContent = j.totalBytes ? `${fmtBytes(j.bytesReceived)} / ${fmtBytes(j.totalBytes)}` : "";
    const eta = row.querySelector("[data-eta]"); if (eta) eta.textContent = fmtEta(j.etaSeconds);
    const badge = row.querySelector(".meta .badge"); if (badge && j.status === "Working") badge.textContent = j.step || "Working";
  }

  // activity
  const DEVICE_KINDS = [["", "Default"], ["CurrentAndroid", "CurrentAndroid"], ["RetailAndroid", "RetailAndroid"], ["Mkb79IPhone", "Mkb79IPhone"]];

  const accountRow = (a) => `
    <div class="account">
      <strong>${esc(a.name || a.accountId)}</strong>
      <span class="badge">${esc(a.locale)}</span>
      ${a.alsoScans && a.alsoScans.length ? `<span class="badge">+ ${esc(a.alsoScans.join(", "))}</span>` : ""}
      <span class="badge ${a.authenticated ? "ok" : "err"}">${a.authenticated ? "Signed in" : "Login needed"}</span>
      <span class="spacer"></span>
      <button type="button" class="btn sm" data-signin="${esc(a.accountId)}" data-locale="${esc(a.locale)}">Sign in again</button>
      <button type="button" class="btn sm ghost danger" data-remove="${esc(a.accountId)}" data-locale="${esc(a.locale)}">Remove</button>
    </div>`;

  const addAccountForm = () => {
    const locales = state.locales && state.locales.length ? state.locales : [{ countryCode: "us", name: "us" }];
    const form = state.addAccount || {};
    return `
      <div class="stepbox">
        <h3>Add an Audible account</h3>
        ${state.loginError ? `<p class="error small">${esc(state.loginError)}</p>` : ""}
        <div class="addform">
          <input type="email" class="input" id="acct-id" inputmode="email" autocapitalize="off" autocomplete="username" spellcheck="false" placeholder="you@example.com" value="${esc(form.accountId || "")}">
          <label class="small muted">Marketplace
            <select class="select" id="acct-locale">
              ${locales.map((l) => `<option value="${esc(l.countryCode)}"${l.countryCode === (form.locale || "us") ? " selected" : ""}>${esc(l.countryCode)} - ${esc(l.name)}</option>`).join("")}
            </select>
          </label>
          <label class="small muted">Device registration
            <select class="select" id="acct-device">
              ${DEVICE_KINDS.map(([v, label]) => `<option value="${v}"${v === (form.device || "") ? " selected" : ""}>${label}</option>`).join("")}
            </select>
          </label>
          <div class="actions">
            <button type="button" class="btn sm primary" id="acct-start"${state.loginBusy ? " disabled" : ""}>${state.loginBusy ? "Starting..." : "Start"}</button>
            <button type="button" class="btn sm ghost" id="acct-close">Cancel</button>
          </div>
        </div>
      </div>`;
  };

  const pendingFlowCard = (f) => `
    <div class="stepbox">
      <h3>1. Open this link in a new tab and sign in to Audible</h3>
      <div class="loginurl">
        <a href="${esc(f.loginUrl)}" target="_blank" rel="noopener">${esc(f.loginUrl)}</a>
        <button type="button" class="btn sm" id="acct-copy">Copy</button>
      </div>
      <input type="text" class="input mono" id="acct-url-fallback" readonly hidden value="${esc(f.loginUrl)}">
      <p class="small muted">${esc(f.accountId)} &middot; ${esc(f.locale)} &middot; started ${fmtWhen(f.startedAt)}</p>
    </div>
    <div class="stepbox">
      <h3>2. When the browser lands on a page that may look broken, copy its full address and paste it here</h3>
      <textarea class="input mono" id="acct-paste" rows="3" placeholder="https://www.amazon.com/ap/maplanding?..."></textarea>
      ${state.loginError ? `<p class="error small">${esc(state.loginError)}</p>` : ""}
      <div class="actions" style="margin-top:8px">
        <button type="button" class="btn sm primary" id="acct-finish"${state.loginBusy ? " disabled" : ""}>${state.loginBusy ? "Finishing..." : "Finish sign-in"}</button>
        <button type="button" class="btn sm ghost" id="acct-cancel"${state.loginBusy ? " disabled" : ""}>Cancel</button>
      </div>
    </div>`;

  const lastLoginLine = (l) => l.status === "succeeded"
    ? `<p class="small muted">Last sign-in: ${esc(l.accountId)} (${esc(l.locale)}) succeeded ${fmtWhen(l.finishedAt)}.</p>`
    : l.status === "failed"
      ? `<p class="error small">Last sign-in for ${esc(l.accountId)} (${esc(l.locale)}) failed: ${esc(l.error || "")}</p>`
      : `<p class="small muted">Last sign-in for ${esc(l.accountId)} (${esc(l.locale)}) was cancelled.</p>`;

  const SCAN_INTERVALS = [["manual", "Manually only"], ["1", "Every hour"], ["2", "Every 2 hours"], ["3", "Every 3 hours"], ["6", "Every 6 hours"], ["12", "Every 12 hours"], ["24", "Once a day"]];

  const scheduleCard = (sched) => {
    const automatic = sched.mode === "automatic";
    const selected = automatic ? String(sched.everyHours) : "manual";
    return `
      <div class="schedule">
        <label class="small muted">Scan Audible
          <select class="select" id="scan-mode">
            ${SCAN_INTERVALS.map(([v, label]) => `<option value="${v}"${v === selected ? " selected" : ""}>${label}</option>`).join("")}
          </select>
        </label>
        <label class="small check"><input type="checkbox" id="scan-download"${sched.downloadAfterScan ? " checked" : ""}><span>After an automatic scan, download the new titles</span></label>
        <p class="small muted">Off by default. Queuing many titles at once is what makes Audible throttle an account.</p>
        ${automatic && sched.nextScanAt ? `<p class="small muted">Next automatic scan: ${esc(fmtIn(sched.nextScanAt))} (${esc(fmtClock(sched.nextScanAt))})</p>` : ""}
        ${sched.lastAutomaticScanError ? `<p class="error small">Last automatic scan failed: ${esc(sched.lastAutomaticScanError)}</p>` : ""}
      </div>`;
  };

  function renderActivity(main) {
    const s = state.scan || {}, st = state.status || {};
    const accounts = st.accounts || [];
    const pending = state.login && state.login.pending;
    const last = state.login && state.login.last;
    main.innerHTML = `
      <div class="card">
        <div class="row-between">
          <h2>Audible scan</h2>
          <button type="button" class="btn sm primary" id="scan-btn" ${s.running ? "disabled" : ""}>${s.running ? "Scanning..." : "Scan now"}</button>
        </div>
        <dl class="kv" style="margin-top:10px">
          <dt>Last run</dt><dd>${s.finishedAt ? fmtWhen(s.finishedAt) : s.running ? "running since " + fmtWhen(s.startedAt) : "never in this session"}</dd>
          ${s.finishedAt ? `<dt>Result</dt><dd>${s.error ? `<span class="error">${esc(s.error)}</span>` : `${s.totalProcessed} titles, ${s.newBooks} new`}</dd>` : ""}
          ${state.schedule && state.schedule.lastAutomaticScanAt ? `<dt>Last automatic</dt><dd>${esc(fmtWhen(state.schedule.lastAutomaticScanAt))}</dd>` : ""}
        </dl>
        ${state.schedule ? scheduleCard(state.schedule) : ""}
        ${st.libraryError ? `<p class="error small">Library could not be read: ${esc(st.libraryError)}</p>` : ""}
      </div>
      <div class="card">
        <div class="row-between">
          <h2>Audible accounts</h2>
          ${pending || state.addAccount ? "" : `<button type="button" class="btn sm" id="acct-add">Add account</button>`}
        </div>
        ${st.accountsError ? `<p class="error small">${esc(st.accountsError)}</p>` : ""}
        ${accounts.length ? accounts.map(accountRow).join("") : st.accountsError ? "" : `<p class="small muted">No accounts yet. Add the Audible account whose books you want.</p>`}
        ${pending ? pendingFlowCard(pending) : state.addAccount ? addAccountForm() : ""}
        ${!pending && !state.addAccount && state.loginError ? `<p class="error small">${esc(state.loginError)}</p>` : ""}
        ${!pending && last ? lastLoginLine(last) : ""}
        <p class="small muted" style="margin-top:10px">Signing in opens Amazon&#39;s own page in your browser: your password is typed there, never here. Removing an account keeps the books it already downloaded.</p>
      </div>
      <div class="card">
        <div class="row-between"><h2>Libation log</h2><button type="button" class="btn sm" id="log-refresh">Refresh</button></div>
        ${state.logs ? `<div class="muted small mono">${esc(state.logs.file || "no log file")}</div><pre class="logtail mono" id="logtail">${esc((state.logs.lines || []).join("\n"))}</pre>` : `<div class="muted small">Loading...</div>`}
      </div>`;
    document.getElementById("scan-btn").onclick = startScan;
    document.getElementById("log-refresh").onclick = loadLogs;
    const mode = document.getElementById("scan-mode");
    if (mode) mode.onchange = () => saveSchedule(mode.value === "manual" ? { scanMode: "manual" } : { scanMode: "automatic", scanEveryHours: Number(mode.value) });
    const after = document.getElementById("scan-download");
    if (after) after.onchange = () => saveSchedule({ downloadAfterScan: after.checked });
    wireAccounts(main, pending);
    const pre = document.getElementById("logtail");
    if (pre) pre.scrollTop = pre.scrollHeight;
  }

  function wireAccounts(main, pending) {
    main.querySelectorAll("[data-signin]").forEach((b) => b.onclick = () => startLogin(b.dataset.signin, b.dataset.locale));
    main.querySelectorAll("[data-remove]").forEach((b) => b.onclick = () => removeAccount(b.dataset.remove, b.dataset.locale));

    const add = document.getElementById("acct-add");
    if (add) add.onclick = async () => { state.addAccount = { locale: "us", device: "" }; state.loginError = null; await loadLocales(); render(); };

    const close = document.getElementById("acct-close");
    if (close) close.onclick = () => { state.addAccount = null; state.loginError = null; render(); };

    const start = document.getElementById("acct-start");
    if (start) start.onclick = () => {
      const id = document.getElementById("acct-id").value;
      const locale = document.getElementById("acct-locale").value;
      const device = document.getElementById("acct-device").value;
      state.addAccount = { accountId: id, locale, device };
      startLogin(id, locale, device);
    };

    const copy = document.getElementById("acct-copy");
    if (copy && pending) copy.onclick = () => copyLoginUrl(pending.loginUrl);

    const finish = document.getElementById("acct-finish");
    if (finish) finish.onclick = finishLogin;

    const cancel = document.getElementById("acct-cancel");
    if (cancel) cancel.onclick = cancelLogin;

    // The textarea is rebuilt on every render (a live scan or queue redraws this view), so whatever has
    // already been pasted lives in state and is put back - with the caret - instead of being lost.
    const paste = document.getElementById("acct-paste");
    if (paste) {
      paste.value = state.paste || "";
      paste.oninput = () => { state.paste = paste.value; };
      paste.onfocus = () => { state.pasteFocus = true; };
      paste.onblur = () => { state.pasteFocus = false; };
      if (state.pasteFocus) { paste.focus(); paste.setSelectionRange(paste.value.length, paste.value.length); }
    }
  }


  // about
  function renderAbout(main) {
    const st = state.status || {}, c = st.counts || {}, set = st.settings || {};
    main.innerHTML = `
      <div class="card">
        <h2>This portal</h2>
        <dl class="kv" style="margin-top:8px">
          <dt>Libation</dt><dd>${esc(st.version || "")}</dd>
          <dt>Library</dt><dd>${c.total != null ? `${c.total} titles · ${c.downloaded} downloaded · ${c.notDownloaded} not yet${c.errors ? ` · ${c.errors} errors` : ""}` : ""}</dd>
          <dt>Books folder</dt><dd class="mono small">${esc(st.booksDirectory || "(not set)")} ${st.booksDirectoryExists === false ? "<span class='error'>(not found)</span>" : ""}</dd>
          <dt>Libation files</dt><dd class="mono small">${esc(st.libationFiles || "")}</dd>
          <dt>Database</dt><dd class="mono small">${esc(st.database || "")}</dd>
          <dt>Downloads</dt><dd>${set.workers || 1} at a time${set.downloadEpisodes === false ? " · podcast episodes excluded" : ""}${set.audiobookshelfEnabled ? " · Audiobookshelf upload on" : ""}${set.dailyLimit && set.dailyLimit !== "NoLimit" ? ` · daily limit ${set.dailyLimitQuantity} (${esc(set.dailyLimit)})` : ""}</dd>
          <dt>Sessions</dt><dd>${st.auth ? `${st.auth.sessions} signed-in device${st.auth.sessions === 1 ? "" : "s"}${st.auth.allowAnonymous ? " · <span class='error'>anonymous access is on</span>" : ""}` : ""}</dd>
          <dt>Up</dt><dd>${st.uptimeSeconds != null ? fmtEta(st.uptimeSeconds).replace(" left", "") : ""}</dd>
        </dl>
        <div class="actions" style="margin-top:12px">
          <button type="button" class="btn" id="refresh-btn">Refresh</button>
          <button type="button" class="btn ghost danger" id="signout-btn">Sign out</button>
        </div>
      </div>
      <div class="card">
        <h2>How this works</h2>
        <p class="small muted">Scan asks Audible for your library and adds new purchases to Libation's database. Download runs Libation's own download and decrypt, and the files land in the Books folder above, where your player (Nightstand, Audiobookshelf, ...) picks them up. Nothing is ever deleted from here.</p>
        <p class="small muted">Setup, remote access and the API are described in <span class="mono">docs/installation/web-portal.md</span> in the Libation repository.</p>
      </div>`;
    document.getElementById("refresh-btn").onclick = loadStatus;
    document.getElementById("signout-btn").onclick = signOut;
  }

  // ---------- boot ----------
  async function boot() {
    render();
    openSse();
    await loadStatus();
    navigate(state.view);
  }

  window.addEventListener("hashchange", () => { const v = location.hash.replace("#", ""); if (v && v !== state.view && state.authed) navigate(v); });
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible" && state.authed) { loadStatus(); if (state.view === "queue") loadQueue(); } });
  setInterval(() => { if (state.authed && !state.sseOpen) { loadStatus(); if (state.view === "queue") loadQueue(); } }, 30000);

  (async () => {
    await checkAuth();
    if (state.authed) await boot(); else render();
  })();
})();

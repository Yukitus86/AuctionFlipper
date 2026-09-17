// Live board for the local dashboard.
//
// State arrives over server-sent events, one snapshot a second, already formatted by the desktop
// app. The page does no maths of its own on purpose: there is exactly one implementation of what a
// flip is worth, and it is the one the tool trades on.

(function () {
  "use strict";

  var state = { flips: [] };
  var sortKey = "score";
  var filterText = "";

  var rowsEl = document.getElementById("rows");
  var emptyEl = document.getElementById("empty");
  var countEl = document.getElementById("count");
  var dotEl = document.getElementById("live-dot");

  // ---- controls ----

  document.getElementById("sorts").addEventListener("click", function (e) {
    var button = e.target.closest(".chip");
    if (!button) return;

    sortKey = button.dataset.sort;
    document.querySelectorAll("#sorts .chip").forEach(function (c) {
      c.classList.toggle("active", c === button);
    });
    render();
  });

  document.getElementById("filter").addEventListener("input", function (e) {
    filterText = e.target.value.trim().toLowerCase();
    render();
  });

  // ---- transport ----

  function connect() {
    var source = new EventSource("/events");

    source.onmessage = function (event) {
      state = JSON.parse(event.data);
      dotEl.classList.remove("stale");
      renderHeader();
      render();
    };

    source.onerror = function () {
      // The desktop app is closed or restarting; EventSource retries on its own.
      dotEl.classList.add("stale");
    };
  }

  function renderHeader() {
    document.getElementById("s-budget").textContent = state.budgetUsed + "/" + state.budgetLimit + " req/min";
    document.getElementById("s-book").textContent = state.book || "-";
    document.getElementById("s-tape").textContent = state.tape || "-";
    document.getElementById("s-sniper").textContent = state.sniper || "-";
    document.getElementById("s-scan").textContent = state.scan || "-";
    document.getElementById("s-uptime").textContent = state.uptime || "-";

    var pct = state.budgetLimit ? Math.min(100, (state.budgetUsed / state.budgetLimit) * 100) : 0;
    document.getElementById("s-gauge").style.width = pct + "%";
  }

  // ---- board ----

  // The desktop app already sends flips ranked by risk-adjusted profit per hour, so the default
  // sort keeps the order they arrive in and has no comparator of its own.
  var comparators = {
    net: function (a, b) { return b.netRaw - a.netRaw; },
    roi: function (a, b) { return parsePercent(b.roi) - parsePercent(a.roi); },
    age: function (a, b) { return parseAge(a.age) - parseAge(b.age); }
  };

  function parsePercent(text) {
    var value = parseFloat(text);
    return isNaN(value) ? 0 : value;
  }

  function parseAge(text) {
    if (!text) return 1e9;
    var value = parseFloat(text);
    if (isNaN(value)) return 1e9;
    if (text.indexOf("m") >= 0) return value * 60;
    if (text.indexOf("h") >= 0) return value * 3600;
    return value;
  }

  function render() {
    var list = state.flips || [];

    if (filterText) {
      list = list.filter(function (f) {
        return f.item.toLowerCase().indexOf(filterText) >= 0;
      });
    }

    var comparator = comparators[sortKey];
    if (comparator) {
      list = list.slice().sort(comparator);
    }

    countEl.textContent = list.length ? list.length + " flips" : "";
    emptyEl.classList.toggle("hidden", list.length > 0);

    var html = "";
    for (var i = 0; i < list.length; i++) html += rowHtml(list[i]);
    rowsEl.innerHTML = html;
  }

  function rowHtml(f) {
    var fresh = parseAge(f.age) < 30 ? " fresh" : "";
    var confColor = f.confidence < 35 ? "var(--loss)" : f.confidence < 65 ? "var(--accent)" : "var(--profit)";

    return '<div class="row' + fresh + '">' +
      '<div class="spine g-' + esc(f.grade) + '"></div>' +
      '<div class="chipimg" style="background:hsl(' + f.hue + ',52%,35%)">' + esc(f.mono) + "</div>" +
      '<div class="name"><b>' + esc(f.item) + (f.count > 1 ? " x" + f.count : "") + "</b>" +
        '<div class="meta"><span>' + esc(f.seller) + "</span>" +
        (f.badges ? badgeHtml(f.badges) : "") + "</div></div>" +
      '<div class="cells">' +
        cell("buy", f.buy) +
        cell("relist", f.sell) +
        '<div class="cell net"><label>net</label><span>' + esc(f.net) + "</span></div>" +
        '<div class="cell roi"><label>roi</label><span>' + esc(f.roi) + "</span></div>" +
        cell("sold / h", f.rate) +
        '<div class="cell conf"><label>confidence ' + f.confidence + "</label>" +
          '<div class="confbar"><i style="width:' + f.confidence + "%;background:" + confColor + '"></i></div>' +
          '<span style="font-size:10px;color:var(--dim)">sells ' + esc(f.absorb) + "</span></div>" +
      "</div>" +
      '<div class="age">' + esc(f.age) + "</div>" +
      "</div>";
  }

  function cell(label, value) {
    return '<div class="cell"><label>' + label + "</label><span>" + esc(value) + "</span></div>";
  }

  function badgeHtml(badges) {
    // Badges are pipe-separated because several of them contain a space ("1 SELLER").
    return badges.split("|").filter(Boolean).map(function (b) {
      return '<span class="badge">' + esc(b) + "</span>";
    }).join("");
  }

  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }

  connect();
})();

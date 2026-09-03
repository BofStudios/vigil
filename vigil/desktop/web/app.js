/* Vigil bar front end.
   Python pushes events into window.vigil.receive(); everything else goes out
   through pywebview.api. No frameworks, no network. */

(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  const el = {
    shell: $("shell"), bar: $("bar"), grip: $("grip"), pulse: $("pulse"),
    input: $("input"), send: $("send"), stop: $("stop"),
    tabrow: $("tabrow"), stream: $("stream"), panel: $("panel"),
    planStrip: $("plan-strip"), planList: $("plan-list"),
    planCount: $("plan-count"), planBar: $("plan-bar"),
    scrim: $("scrim"), approval: $("approval"), sheet: $("sheet"),
    setup: $("setup"), setupSub: $("setup-sub"), setupKey: $("setup-key"),
    setupGo: $("setup-go"), setupError: $("setup-error"),
    setupTabs: $("setup-tabs"), setupModels: $("setup-models"),
    setupLink: $("setup-link"),
    risk: $("approval-risk"), tool: $("approval-tool"),
    summary: $("approval-summary"), reason: $("approval-reason"),
    detail: $("approval-detail"),
    yes: $("btn-yes"), no: $("btn-no"), always: $("btn-always"),
    sheetTitle: $("sheet-title"), sheetBody: $("sheet-body"), sheetClose: $("sheet-close"),
    chipMode: $("chip-mode"), chipModel: $("chip-model"), chipCwd: $("chip-cwd"),
    chipBrain: $("chip-brain"), brainLabel: $("brain-label"),
    chipTools: $("chip-tools"), tokens: $("chip-tokens"), newTab: $("btn-new"),
    modelLabel: $("model-label"), cwdLabel: $("cwd-label"), toolsLabel: $("tools-label"),
    close: $("win-close"), toasts: $("toasts"),
    voiceHint: $("voice-hint"), voiceText: $("voice-text"),
  };

  const MODES = ["ask", "auto", "yolo"];
  // mirrors ALWAYS_ASK in security.py: these are confirmed one at a time,
  // so "always" is never offered for them
  const INPUT_CONTROL = new Set(["mouse_click", "mouse_move", "mouse_scroll", "click_on",
                                 "keyboard_type", "press_keys", "screen_capture", "clipboard"]);
  const INPUT_MIN = 26;
  const INPUT_MAX = 132;

  const state = { tabs: new Map(), active: null, mode: "ask", model: "", pending: null,
    brain: "direct", brains: [], history: [],
                  expanded: false, queued: "", resting: true };

  const api = () => window.pywebview && window.pywebview.api;

  // ------------------------------------------------------------ utilities
  const escapeHtml = (text) =>
    String(text ?? "")
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");

  /* ------------------------------------------------------------- motion */
  /* Decoration, kept in one place and switched off wholesale for anyone whose
     system asks for less movement. Nothing here changes what anything does. */

  const REDUCED = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const NOISE = "abcdefghijklmnopqrstuvwxyz0123456789/_-";

  /** A specular highlight that follows the pointer across a surface. */
  function glare(surface) {
    if (REDUCED || !surface) return;
    let frame = 0;
    surface.addEventListener("pointermove", (event) => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        const box = surface.getBoundingClientRect();
        surface.style.setProperty("--glare-x", `${event.clientX - box.left}px`);
        surface.style.setProperty("--glare-y", `${event.clientY - box.top}px`);
        surface.style.setProperty("--glare", "1");
      });
    }, { passive: true });
    surface.addEventListener("pointerleave", () => {
      surface.style.setProperty("--glare", "0");
    }, { passive: true });
  }

  /** Controls lean towards the pointer before it reaches them. */
  function magnetise(container, reach = 52, strength = 0.26) {
    if (REDUCED || !container) return;
    const controls = () => container.querySelectorAll(".round, .chip, .key");
    const release = () => controls().forEach((node) => {
      node.style.setProperty("--pull-x", "0px");
      node.style.setProperty("--pull-y", "0px");
    });

    let frame = 0;
    container.addEventListener("pointermove", (event) => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        controls().forEach((node) => {
          const box = node.getBoundingClientRect();
          const dx = event.clientX - (box.left + box.width / 2);
          const dy = event.clientY - (box.top + box.height / 2);
          const distance = Math.hypot(dx, dy);
          if (distance > reach) {
            node.style.setProperty("--pull-x", "0px");
            node.style.setProperty("--pull-y", "0px");
            return;
          }
          // strongest up close, nothing at the edge of the reach
          const fall = 1 - distance / reach;
          node.style.setProperty("--pull-x", `${(dx * strength * fall).toFixed(2)}px`);
          node.style.setProperty("--pull-y", `${(dy * strength * fall).toFixed(2)}px`);
        });
      });
    }, { passive: true });
    container.addEventListener("pointerleave", release, { passive: true });
  }

  /** Resolve text out of noise. Short - it is a cue, not a cutscene. */
  function settle(node, text, duration = 240) {
    const wanted = String(text ?? "");
    if (REDUCED || !wanted) { node.textContent = wanted; return; }

    node.classList.add("settling");
    const started = performance.now();
    const step = (now) => {
      const progress = Math.min(1, (now - started) / duration);
      const resolved = Math.floor(wanted.length * progress);
      let shown = wanted.slice(0, resolved);
      for (let i = resolved; i < wanted.length; i += 1) {
        shown += wanted[i] === " "
          ? " "
          : NOISE[(Math.random() * NOISE.length) | 0];
      }
      node.textContent = shown;
      if (progress < 1) {
        requestAnimationFrame(step);
      } else {
        node.textContent = wanted;
        node.classList.remove("settling");
      }
    };
    requestAnimationFrame(step);
  }

  /** Change a number and let it read as a change rather than a redraw. */
  function roll(node, text) {
    const wanted = String(text);
    if (node.textContent === wanted) return;
    node.textContent = wanted;
    if (REDUCED) return;
    node.classList.remove("rolling");
    void node.offsetWidth;          // restart the animation rather than ignore it
    node.classList.add("rolling");
  }

  /** A deliberately small markdown subset. Everything is escaped first, so no
   *  model output can inject markup. */
  function renderMarkdown(raw) {
    const blocks = [];
    const stripped = String(raw ?? "").replace(/```(\w*)\n?([\s\S]*?)```/g, (_, lang, code) => {
      blocks.push(`<pre><code>${escapeHtml(code.replace(/\n$/, ""))}</code></pre>`);
      return ` BLOCK${blocks.length - 1} `;
    });

    const inline = (text) =>
      escapeHtml(text)
        .replace(/`([^`]+)`/g, "<code>$1</code>")
        .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
        .replace(/(^|[\s(])\*([^*\n]+)\*/g, "$1<em>$2</em>");

    const out = [];
    let list = null;
    const closeList = () => { if (list) { out.push(`</${list}>`); list = null; } };

    for (const line of stripped.split("\n")) {
      const placeholder = line.match(/^ BLOCK(\d+) $/);
      if (placeholder) { closeList(); out.push(blocks[Number(placeholder[1])]); continue; }

      const bullet = line.match(/^\s*[-*•]\s+(.*)$/);
      const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);

      if (bullet) {
        if (list !== "ul") { closeList(); out.push("<ul>"); list = "ul"; }
        out.push(`<li>${inline(bullet[1])}</li>`);
      } else if (numbered) {
        if (list !== "ol") { closeList(); out.push("<ol>"); list = "ol"; }
        out.push(`<li>${inline(numbered[1])}</li>`);
      } else if (!line.trim()) {
        closeList();
      } else {
        closeList();
        out.push(`<p>${inline(line)}</p>`);
      }
    }
    closeList();
    return out.join("");
  }

  function toast(text, kind) {
    const node = document.createElement("div");
    node.className = "toast" + (kind ? " " + kind : "");
    node.textContent = text;
    el.toasts.appendChild(node);
    setTimeout(() => {
      node.classList.add("out");
      setTimeout(() => node.remove(), 240);
    }, 3400);
  }

  const shortPath = (path) => {
    const parts = String(path || "").split(/[\\/]/).filter(Boolean);
    return parts.length <= 2 ? path : "…\\" + parts.slice(-2).join("\\");
  };

  // ------------------------------------------------------------- geometry
  /** Python owns the window shape and tells us which one it moved to; the
   *  front end only has to dress itself to match. */
  function applyShape(resting) {
    state.resting = !!resting;
    el.shell.classList.toggle("resting", state.resting);
    if (!state.resting) setTimeout(() => el.input.focus(), 120);
  }

  function peek() {
    if (!state.resting || state.expanded) return;
    applyShape(false);
    api().peek();
  }

  function expand() {
    if (state.expanded) return;
    state.expanded = true;
    state.resting = false;
    el.shell.classList.add("expanded");
    el.shell.classList.remove("resting");
    api().expand();
  }

  function collapse() {
    if (!state.expanded) return;
    state.expanded = false;
    el.shell.classList.remove("expanded");
    api().collapse();
  }

  // ----------------------------------------------------------------- tabs
  function makeTab(info) {
    const thread = document.createElement("div");
    thread.className = "thread";
    thread.hidden = true;
    el.stream.appendChild(thread);

    const tab = {
      id: info.id, title: info.title, cwd: info.cwd, busy: false,
      thread, plan: null, streamNode: null, streamText: "", atBottom: true, lastTool: null,
    };
    state.tabs.set(info.id, tab);
    return tab;
  }

  function renderTabs() {
    // one session needs no tab strip; it is only clutter
    if (state.tabs.size < 2) { el.tabrow.innerHTML = ""; return; }

    el.tabrow.innerHTML = "";
    for (const tab of state.tabs.values()) {
      const button = document.createElement("button");
      button.className = "tab" + (tab.id === state.active ? " active" : "");
      button.title = tab.title;

      if (tab.busy) {
        const spin = document.createElement("span");
        spin.className = "tab-spin";
        button.appendChild(spin);
      }

      const label = document.createElement("span");
      label.className = "tab-label";
      label.textContent = tab.title;
      button.appendChild(label);

      const close = document.createElement("span");
      close.className = "tab-x";
      close.innerHTML = '<svg viewBox="0 0 12 12"><path d="M3 3l6 6M9 3l-6 6" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
      close.addEventListener("click", (event) => { event.stopPropagation(); closeTab(tab.id); });
      button.appendChild(close);

      button.addEventListener("click", () => activate(tab.id));
      el.tabrow.appendChild(button);
    }
  }

  function activate(tabId) {
    const tab = state.tabs.get(tabId);
    if (!tab) return;
    for (const other of state.tabs.values()) other.thread.hidden = other.id !== tabId;
    state.active = tabId;
    renderTabs();
    renderPlan(tab);
    setBusy(tab.busy);
    el.cwdLabel.textContent = shortPath(tab.cwd);
    el.chipCwd.title = tab.cwd;
    if (tab.thread.childElementCount > 0) expand();
    el.input.focus();
  }

  async function newTab() {
    const info = await api().new_tab("");
    if (info.error) { toast(info.error, "error"); return; }
    makeTab(info);
    activate(info.id);
  }

  async function closeTab(tabId) {
    const tab = state.tabs.get(tabId);
    if (tab) { tab.thread.remove(); state.tabs.delete(tabId); }
    const next = await api().close_tab(tabId);
    for (const info of next.tabs || []) if (!state.tabs.has(info.id)) makeTab(info);
    const first = next.tabs && next.tabs[0];
    activate(state.tabs.has(state.active) ? state.active : (first ? first.id : null));
  }

  // ------------------------------------------------------------- printing
  function appendTo(tab, node) {
    const stick = tab.atBottom;
    tab.thread.appendChild(node);
    if (stick && tab.id === state.active) el.stream.scrollTop = el.stream.scrollHeight;
    return node;
  }

  function addUser(tab, text) {
    const wrap = document.createElement("div");
    wrap.className = "msg you";
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.textContent = text;
    wrap.appendChild(bubble);
    appendTo(tab, wrap);
  }

  function sayNode(tab) {
    if (tab.streamNode) return tab.streamNode;
    const wrap = document.createElement("div");
    wrap.className = "msg say";
    wrap.innerHTML = '<div class="body"></div>';
    tab.streamNode = wrap;
    tab.streamText = "";
    return appendTo(tab, wrap);
  }

  function streamChunk(tab, text) {
    const node = sayNode(tab);
    tab.streamText += text;
    node.querySelector(".body").innerHTML =
      renderMarkdown(tab.streamText) + '<span class="caret"></span>';
    if (tab.atBottom && tab.id === state.active) el.stream.scrollTop = el.stream.scrollHeight;
  }

  function endStream(tab) {
    if (!tab.streamNode) return;
    if (!tab.streamText.trim()) tab.streamNode.remove();
    else tab.streamNode.querySelector(".body").innerHTML = renderMarkdown(tab.streamText);
    tab.streamNode = null;
    tab.streamText = "";
  }

  function addTool(tab, event) {
    endStream(tab);
    const node = document.createElement("div");
    node.className = "tool running " + (event.risk || "moderate");
    node.innerHTML = '<span class="bead"></span><div class="col">'
      + '<span class="name"></span><span class="sub"></span></div>';
    settle(node.querySelector(".name"), event.name);
    node.querySelector(".sub").textContent = event.summary || "";
    tab.lastTool = node;
    appendTo(tab, node);
  }

  function addToolResult(tab, event) {
    const node = tab.lastTool;
    if (!node) return;
    node.classList.remove("running");
    const text = (event.text || "").trim();
    if (!text) return;

    const lines = text.split("\n");
    const preview = lines.slice(0, 5).join("\n");
    const out = document.createElement("div");
    out.className = "out" + (event.ok ? "" : " bad");
    out.textContent = preview;
    node.querySelector(".col").appendChild(out);

    if (lines.length > 5) {
      const more = document.createElement("button");
      more.className = "more";
      more.textContent = `+${lines.length - 5} more lines`;
      let open = false;
      more.addEventListener("click", () => {
        open = !open;
        out.textContent = open ? text : preview;
        more.textContent = open ? "show less" : `+${lines.length - 5} more lines`;
      });
      node.querySelector(".col").appendChild(more);
    }
    if (tab.atBottom && tab.id === state.active) el.stream.scrollTop = el.stream.scrollHeight;
  }

  function addNotice(tab, event) {
    endStream(tab);
    const text = String(event.text || "");

    if (/permanently blocked/i.test(text)) {
      const card = document.createElement("div");
      card.className = "wall";
      card.innerHTML = '<div class="head">blocked · security policy</div><div class="text"></div>';
      card.querySelector(".text").textContent = text;
      appendTo(tab, card);
      return;
    }
    const node = document.createElement("div");
    node.className = "note " + (event.level || "info");
    node.textContent = text;
    appendTo(tab, node);
  }

  // ----------------------------------------------------------------- plan
  function renderPlan(tab) {
    const steps = tab && tab.plan;
    if (!steps || !steps.length) { el.planStrip.hidden = true; return; }
    el.planStrip.hidden = false;

    const done = steps.filter((step) => step.status === "done").length;
    roll(el.planCount, `${done}/${steps.length}`);
    el.planBar.style.width = `${(done / steps.length) * 100}%`;

    el.planList.innerHTML = "";
    steps.forEach((step, index) => {
      const item = document.createElement("li");
      item.className = "plan-item " + (step.status || "todo");
      item.style.animationDelay = `${index * 22}ms`;

      const tick = document.createElement("span");
      tick.className = "tick";
      item.appendChild(tick);

      const lines = document.createElement("span");
      lines.className = "lines";

      const text = document.createElement("span");
      text.className = "txt";
      text.textContent = step.text || "";
      lines.appendChild(text);

      if (step.note) {
        const note = document.createElement("span");
        note.className = "step-note";
        note.textContent = step.note;
        lines.appendChild(note);
      }
      item.appendChild(lines);
      el.planList.appendChild(item);
    });
  }

  // ------------------------------------------------------------ approvals
  function showApproval(event) {
    expand();
    state.pending = event;
    el.risk.textContent = event.risk;
    el.risk.className = "risk " + event.risk;
    el.tool.textContent = event.tool;
    el.summary.textContent = event.summary;
    el.reason.textContent = event.reason || "";

    el.detail.innerHTML = "";
    if (event.detail) {
      for (const line of String(event.detail).split("\n")) {
        const row = document.createElement("div");
        if (line.startsWith("+")) row.className = "add";
        else if (line.startsWith("-")) row.className = "del";
        else if (line.startsWith("@@")) row.className = "meta";
        row.textContent = line;
        el.detail.appendChild(row);
      }
    }

    const oneAtATime = event.risk === "high" || INPUT_CONTROL.has(event.tool);
    el.always.hidden = INPUT_CONTROL.has(event.tool);
    el.always.disabled = oneAtATime;
    el.always.title = oneAtATime
      ? "This is confirmed every time, one action at a time"
      : "Allow this kind of action for the rest of the session";

    el.sheet.hidden = true;
    el.approval.hidden = false;
    el.scrim.hidden = false;
    el.yes.focus();
  }

  async function answer(value) {
    const pending = state.pending;
    if (!pending) return;
    state.pending = null;
    el.scrim.hidden = true;
    await api().answer(pending.tab, pending.request, value);
  }

  // ---------------------------------------------------------------- sheet
  function openSheet(title, build) {
    expand();
    el.sheetTitle.textContent = title;
    el.sheetBody.innerHTML = "";
    build(el.sheetBody);
    el.approval.hidden = true;
    el.sheet.hidden = false;
    el.scrim.hidden = false;
  }

  function closeSheet() {
    el.sheet.hidden = true;
    if (!state.pending) el.scrim.hidden = true;
  }

  async function showModels() {
    openSheet("Models", (body) => { body.innerHTML = '<div class="trow">loading…</div>'; });
    const result = await api().models();
    openSheet("Models", (body) => {
      if (result.error) {
        const row = document.createElement("div");
        row.className = "trow";
        row.textContent = result.error;
        body.appendChild(row);
        return;
      }
      for (const name of result.models || []) {
        const option = document.createElement("button");
        option.className = "pick" + (name === state.model ? " on" : "");
        option.innerHTML = '<span class="name"></span>';
        option.querySelector(".name").textContent = name;
        option.addEventListener("click", async () => {
          const updated = await api().set_model(name);
          state.model = updated.model;
          el.modelLabel.textContent = updated.model;
          if (updated.warning) toast(updated.warning, "error");
          closeSheet();
        });
        body.appendChild(option);
      }
    });
  }

  /* Picking how it thinks. Each option carries its own description, and the
     riskier one repeats its warning when it is chosen - reading it once in a
     list is not the same as being told at the moment you turn it on. */
  function showBrains() {
    openSheet("How Vigil thinks", (body) => {
      for (const brain of state.brains) {
        const option = document.createElement("button");
        option.className = "pick tall" + (brain.key === state.brain ? " on" : "");

        const head = document.createElement("div");
        head.className = "head";
        const title = document.createElement("span");
        title.className = "title";
        title.textContent = brain.name;
        const tag = document.createElement("span");
        tag.className = "tag";
        tag.textContent = brain.tagline;
        const model = document.createElement("span");
        model.className = "model";
        model.textContent = brain.model;
        head.append(title, tag, model);

        const desc = document.createElement("div");
        desc.className = "desc";
        desc.textContent = brain.summary;
        option.append(head, desc);

        if (brain.warning) {
          const warn = document.createElement("div");
          warn.className = "warn";
          warn.textContent = brain.warning;
          option.appendChild(warn);
        }

        option.addEventListener("click", async () => {
          const result = await api().set_brain(brain.key);
          if (result.error) { toast(result.error, "error"); return; }
          applyBrain(result.brain);
          state.model = result.model;
          el.modelLabel.textContent = result.model;
          closeSheet();
          if (brain.warning) toast(brain.warning, "error");
          else toast(brain.name + " - " + brain.tagline.toLowerCase());
        });
        body.appendChild(option);
      }
    });
  }

  function applyBrain(key) {
    state.brain = key;
    const brain = state.brains.find((b) => b.key === key);
    el.brainLabel.textContent = brain ? brain.name : key;
    el.chipBrain.classList.toggle("warn", !!(brain && brain.warning));
    el.chipBrain.title = brain
      ? brain.name + " - " + brain.summary
      : "How Vigil thinks";
  }

  async function showTools() {
    const result = await api().tools(state.active || "");
    openSheet("Tools", (body) => {
      for (const [group, tools] of Object.entries(result.groups || {})) {
        const title = document.createElement("div");
        title.className = "group-title";
        title.textContent = `${group} · ${tools.length}`;
        body.appendChild(title);
        for (const tool of tools) {
          const row = document.createElement("div");
          row.className = "trow";
          row.innerHTML = '<span class="name"></span><span class="risk"></span><span class="d"></span>';
          row.querySelector(".name").textContent = tool.name;
          const risk = row.querySelector(".risk");
          risk.textContent = tool.risk;
          risk.classList.add(tool.risk);
          row.querySelector(".d").textContent = tool.description.split(".")[0];
          body.appendChild(row);
        }
      }
    });
  }

  // ---------------------------------------------------------------- voice
  function applyVoice(event) {
    const listening = event.state === "listening";
    const thinking = event.state === "thinking";

    el.shell.classList.toggle("listening", listening);
    el.shell.classList.toggle("thinking", thinking);
    el.voiceHint.hidden = !(listening || thinking);
    el.pulse.hidden = !listening;

    if (listening) {
      el.voiceText.textContent = "Listening";
      return;
    }
    if (thinking) {
      // the same animation covers "circle something on screen", which arrives
      // with its own word for what is going on
      el.voiceText.textContent = event.label || "Transcribing";
      return;
    }
    if (event.state === "text" && event.text) {
      // drop the words in the box rather than sending them: speech is easy to
      // misrecognise, and this app acts on what it is told
      el.input.value = el.input.value.trim()
        ? el.input.value.trim() + " " + event.text
        : event.text;
      autoGrow();
      api().hold(true);
      el.input.focus();
      el.input.setSelectionRange(el.input.value.length, el.input.value.length);
      return;
    }
    if (event.state === "error") {
      toast(event.text || "could not hear that", "error");
    }
  }

  // ------------------------------------------------------------- composer
  function setBusy(busy) {
    el.send.hidden = busy;
    el.stop.hidden = !busy;
    el.pulse.hidden = !busy;
    el.shell.classList.toggle("working", busy);
    el.send.disabled = !busy && !el.input.value.trim();
  }

  function autoGrow() {
    const input = el.input;
    el.send.disabled = !input.value.trim();

    // An empty box is always one line, and measuring it is actively harmful:
    // during the first layout pass it has almost no width, so the placeholder
    // wraps into a dozen lines and scrollHeight reports the maximum.
    if (!input.value) { input.style.height = ""; return; }

    input.style.height = "0px";
    input.style.height = Math.min(Math.max(input.scrollHeight, INPUT_MIN), INPUT_MAX) + "px";
  }

  /* ------------------------------------------------------------- recall */
  /* Up and Down walk back through what you have asked before, the way a shell
     does. Whatever you had half-typed is kept and comes back when you walk
     forward past the newest entry. */

  const recall = { at: -1, draft: "" };

  function recallable(direction) {
    // Up only from the first line, Down only from the last, so a multi-line
    // prompt can still be edited with the arrow keys.
    const caret = el.input.selectionStart;
    const value = el.input.value;
    if (direction < 0) return !value.slice(0, caret).includes("\n");
    return !value.slice(caret).includes("\n");
  }

  function stepRecall(direction) {
    const items = state.history;
    if (!items.length) return false;

    if (recall.at === -1) {
      if (direction > 0) return false;          // already at the newest
      recall.draft = el.input.value;
      recall.at = items.length;
    }

    const next = recall.at + direction;
    if (next < 0) return true;                  // hold at the oldest
    if (next >= items.length) {                 // back out to what you were typing
      recall.at = -1;
      el.input.value = recall.draft;
    } else {
      recall.at = next;
      el.input.value = items[next];
    }
    autoGrow();
    api().hold(!!el.input.value.trim());
    const end = el.input.value.length;
    el.input.setSelectionRange(end, end);
    return true;
  }

  function leaveRecall() {
    recall.at = -1;
    recall.draft = "";
  }

  function insertAtCaret(text) {
    const start = el.input.selectionStart;
    const end = el.input.selectionEnd;
    const before = el.input.value.slice(0, start);
    const after = el.input.value.slice(end);
    const spacer = before && !before.endsWith(" ") ? " " : "";
    el.input.value = before + spacer + text + after;
    const caret = (before + spacer + text).length;
    el.input.setSelectionRange(caret, caret);
    autoGrow();
    api().hold(true);
    el.input.focus();
  }

  async function submit() {
    const text = el.input.value.trim();
    if (!text) return;

    const tab = state.tabs.get(state.active);

    // Boot is not instant. Typing during it used to hit `return` here and the
    // message just vanished, which looked exactly like a broken app.
    if (!tab) {
      state.queued = text;
      el.input.value = "";
      autoGrow();
      toast("starting up…");
      return;
    }
    if (tab.busy) { toast("still working — press stop first"); return; }

    el.input.value = "";
    autoGrow();
    leaveRecall();
    if (state.history[state.history.length - 1] !== text) state.history.push(text);
    expand();
    const result = await api().send(tab.id, text);
    if (result && result.error) toast(result.error, "error");
  }

  function flushQueued() {
    if (!state.queued) return;
    const text = state.queued;
    state.queued = "";
    el.input.value = text;
    submit();
  }

  async function cycleMode() {
    const next = MODES[(MODES.indexOf(state.mode) + 1) % MODES.length];
    const result = await api().set_mode(next);
    if (result.error) { toast(result.error, "error"); return; }
    applyMode(result.mode);
    toast(result.mode === "yolo"
      ? "yolo — nothing is asked. Blocked actions stay blocked."
      : "approval mode: " + result.mode);
  }

  function applyMode(mode) {
    state.mode = mode;
    el.shell.classList.remove("mode-ask", "mode-auto", "mode-yolo");
    el.shell.classList.add("mode-" + mode);
    el.chipMode.title = "Approval mode: " + mode + " (click to change)";
  }

  // --------------------------------------------------------------- events
  const handlers = {
    user: (tab, event) => addUser(tab, event.text),
    assistant_chunk: (tab, event) => streamChunk(tab, event.text),
    assistant_end: (tab) => endStream(tab),
    assistant_full: (tab, event) => { streamChunk(tab, event.text); endStream(tab); },
    tool: (tab, event) => addTool(tab, event),
    tool_result: (tab, event) => addToolResult(tab, event),
    notice: (tab, event) => addNotice(tab, event),
    plan: (tab, event) => { tab.plan = event.steps; if (tab.id === state.active) renderPlan(tab); },
    approval: (tab, event) => { endStream(tab); showApproval(event); },
    focus: () => el.input.focus(),
    voice: (_tab, event) => applyVoice(event),
    shape: (tab, event) => applyShape(event.resting),
    status: (tab, event) => {
      tab.busy = !!event.busy;
      if (event.title) tab.title = event.title;
      if (event.cwd) {
        tab.cwd = event.cwd;
        if (tab.id === state.active) {
          el.cwdLabel.textContent = shortPath(event.cwd);
          el.chipCwd.title = event.cwd;
        }
      }
      if (!event.busy) {
        endStream(tab);
        api().notify_done(tab.title);
      }
      if (event.tokens && tab.id === state.active) {
        el.tokens.hidden = false;
        roll(el.tokens, event.tokens.toLocaleString() + " tok");
      }
      renderTabs();
      if (tab.id === state.active) setBusy(tab.busy);
    },
    table: (tab, event) => addNotice(tab, { level: "info", text: event.title }),
  };

  // Some events belong to the window rather than to a conversation, and must
  // not be dropped just because the tab list has not been populated yet.
  const WINDOW_EVENTS = new Set(["shape", "focus", "voice"]);

  window.vigil = {
    receive(event) {
      const handler = handlers[event.type];
      if (!handler) return;

      if (WINDOW_EVENTS.has(event.type)) {
        handler(null, event);
        return;
      }
      const tab = state.tabs.get(event.tab);
      if (tab) handler(tab, event);
    },
  };

  // -------------------------------------------------------------- wiring
  el.send.addEventListener("click", submit);
  el.stop.addEventListener("click", () => api().stop(state.active));
  el.newTab.addEventListener("click", newTab);
  el.chipMode.addEventListener("click", cycleMode);
  el.chipModel.addEventListener("click", showModels);
  el.chipBrain.addEventListener("click", showBrains);
  el.setupGo.addEventListener("click", connect);
  el.setupKey.addEventListener("keydown", (event) => {
    if (event.key === "Enter") { event.preventDefault(); connect(); }
  });
  el.setupLink.addEventListener("click", () => {
    if (route && route.link) api().open_url(route.link);
  });
  el.chipTools.addEventListener("click", showTools);
  el.sheetClose.addEventListener("click", closeSheet);
  el.close.addEventListener("click", () => api().hide_window());

  el.chipCwd.addEventListener("click", async () => {
    const result = await api().pick_folder(state.active);
    if (result.error) toast(result.error, "error");
    else if (result.cwd) {
      const tab = state.tabs.get(state.active);
      tab.cwd = result.cwd;
      el.cwdLabel.textContent = shortPath(result.cwd);
      el.chipCwd.title = result.cwd;
      toast(shortPath(result.cwd));
    }
  });

  el.yes.addEventListener("click", () => answer("yes"));
  el.no.addEventListener("click", () => answer("no"));
  el.always.addEventListener("click", () => answer("always"));

  el.scrim.addEventListener("mousedown", (event) => {
    if (event.target === el.scrim && !state.pending) closeSheet();
  });

  el.input.addEventListener("input", () => {
    autoGrow();
    // tell Python whether there is something worth keeping the bar open for
    api().hold(!!el.input.value.trim());
  });
  el.input.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); submit(); }
    if (event.key === "ArrowUp" && recallable(-1) && stepRecall(-1)) event.preventDefault();
    if (event.key === "ArrowDown" && recallable(1) && stepRecall(1)) event.preventDefault();
  });

  /* Pasting. An image goes to the vision model and comes back as words you can
     edit before sending. Files copied in Explorer arrive with no text at all -
     Windows keeps them in a separate clipboard format - so Python is asked for
     the paths. Ordinary copied text is left alone and pasted as text. */
  el.input.addEventListener("paste", async (event) => {
    const data = event.clipboardData;
    if (!data) return;

    const picture = [...(data.items || [])].find((item) =>
      item.type && item.type.startsWith("image/"));
    if (picture) {
      event.preventDefault();
      const file = picture.getAsFile();
      if (!file) return;
      const reader = new FileReader();
      reader.onload = async () => {
        applyVoice({ state: "thinking", label: "Looking" });
        const result = await api().describe_image(String(reader.result));
        applyVoice({ state: "idle" });
        if (result && result.error) toast(result.error, "error");
        else if (result && result.text) insertAtCaret(result.text);
      };
      reader.readAsDataURL(file);
      return;
    }

    if ((data.getData("text") || "").trim()) return;   // a normal paste

    event.preventDefault();
    const found = await api().clipboard_paths();
    if (found && found.text) insertAtCaret(found.text);
  });

  // Only a scroll the user started should detach the view from the bottom -
  // otherwise our own pinning would keep cancelling itself.
  let userScrolling = false;
  const noteUserScroll = () => {
    userScrolling = true;
    setTimeout(() => { userScrolling = false; }, 120);
  };
  el.stream.addEventListener("wheel", noteUserScroll, { passive: true });
  el.stream.addEventListener("keydown", noteUserScroll);
  el.stream.addEventListener("scroll", () => {
    const tab = state.tabs.get(state.active);
    if (!tab || !userScrolling) return;
    const slack = el.stream.scrollHeight - el.stream.scrollTop - el.stream.clientHeight;
    tab.atBottom = slack < 60;
  });

  document.addEventListener("keydown", (event) => {
    if (state.pending) {
      const key = event.key.toLowerCase();
      if (key === "y") { event.preventDefault(); answer("yes"); }
      if (key === "n" || event.key === "Escape") { event.preventDefault(); answer("no"); }
      if (key === "a" && !el.always.disabled && !el.always.hidden) {
        event.preventDefault();
        answer("always");
      }
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      if (!el.sheet.hidden) closeSheet();
      else if (state.expanded) collapse();
      else if (!state.resting) { el.input.value = ""; autoGrow(); api().hold(false); api().rest(); }
      else api().hide_window();
      return;
    }
    if (event.ctrlKey && event.key.toLowerCase() === "t") { event.preventDefault(); newTab(); }
    if (event.ctrlKey && event.key.toLowerCase() === "w") { event.preventDefault(); closeTab(state.active); }
    if (event.ctrlKey && /^[1-9]$/.test(event.key)) {
      event.preventDefault();
      const ids = [...state.tabs.keys()];
      if (ids[Number(event.key) - 1]) activate(ids[Number(event.key) - 1]);
    }
  });

  // Surfaces that catch the light, and controls that lean towards the pointer.
  // Both are no-ops when the system has asked for less movement.
  const footer = document.querySelector(".footer");
  glare(el.bar);
  glare(footer);
  magnetise(el.bar);
  magnetise(footer);
  magnetise(el.approval);

  /* -------------------------------------------------------------- setup */
  /* Vigil is an application, so a missing key is a screen you fill in rather
     than an error printed to a terminal that nobody is looking at. The routes
     come from Python, so the words here and what pressing Connect does cannot
     drift apart. */

  let route = null;
  let routes = [];

  function showSetup(setup) {
    const needed = !!(setup && setup.needed);
    el.shell.classList.toggle("needs-setup", needed);
    el.setup.hidden = !needed;
    if (!needed) return;

    expand();
    routes = (setup.routes && setup.routes.length) ? setup.routes : routes;
    drawTabs();
    pickRoute(setup.provider || (routes[0] && routes[0].key) || "groq");

    if (setup.reason) {
      el.setupError.textContent = setup.reason;
      el.setupError.hidden = false;
    }
    setTimeout(() => el.setupKey.focus(), 260);
  }

  function drawTabs() {
    el.setupTabs.innerHTML = "";
    for (const option of routes) {
      const tab = document.createElement("button");
      tab.className = "setup-tab";
      tab.dataset.route = option.key;

      const name = document.createElement("span");
      name.textContent = option.name;
      const badge = document.createElement("span");
      badge.className = "badge";
      badge.textContent = option.badge;
      tab.append(name, badge);

      tab.addEventListener("click", () => pickRoute(option.key));
      el.setupTabs.appendChild(tab);
    }
  }

  function pickRoute(key) {
    route = routes.find((option) => option.key === key) || routes[0];
    if (!route) return;

    for (const tab of el.setupTabs.querySelectorAll(".setup-tab")) {
      tab.classList.toggle("on", tab.dataset.route === route.key);
    }

    el.setupSub.textContent = route.blurb;

    el.setupModels.innerHTML = "";
    el.setupModels.hidden = !(route.models && route.models.length);
    for (const model of route.models || []) {
      const row = document.createElement("li");
      const id = document.createElement("span");
      id.className = "id";
      id.textContent = model.id;
      const note = document.createElement("span");
      note.className = "note";
      note.textContent = model.note;
      row.append(id, note);
      el.setupModels.appendChild(row);
    }

    el.setupLink.textContent = route.link_text + " →";
    el.setupLink.hidden = !route.link;

    el.setupKey.value = "";
    el.setupKey.type = route.needs_key ? "password" : "text";
    el.setupKey.placeholder = route.placeholder;
    el.setupError.hidden = true;
  }

  async function connect() {
    if (!route) return;
    el.setupError.hidden = true;
    el.setupGo.disabled = true;
    el.setupGo.textContent = "Checking…";

    const typed = el.setupKey.value.trim();
    const result = await api().connect(
      route.key,
      route.needs_key ? typed : "",
      route.needs_key ? "" : typed,
    );

    el.setupGo.disabled = false;
    el.setupGo.textContent = "Connect";

    if (!result || result.error) {
      el.setupError.textContent = (result && result.error) || "That did not work.";
      el.setupError.hidden = false;
      return;
    }

    el.setupKey.value = "";
    applyState(result.state);
    toast("connected");
  }

  /** Draw everything the Python side just told us. Used at boot, and again
   *  the moment a key is accepted, so the app fills in without a restart. */
  function applyState(initial) {
    if (!initial) return;
    applyMode(initial.mode);
    state.brains = initial.brains || [];
    state.history = initial.history || [];
    applyBrain(initial.brain || "direct");
    state.model = initial.model;
    el.modelLabel.textContent = initial.model;
    showSetup(initial.setup);

    for (const info of initial.tabs || []) {
      if (state.tabs.has(info.id)) continue;
      makeTab(info);
      roll(el.toolsLabel, info.tools + " tools");
    }
    const first = initial.tabs && initial.tabs[0];
    if (first && !state.active) {
      state.active = first.id;
      const tab = state.tabs.get(first.id);
      tab.thread.hidden = false;
      el.cwdLabel.textContent = shortPath(tab.cwd);
      el.chipCwd.title = tab.cwd;
      renderTabs();
      setBusy(false);
    }

    const hints = [];
    if (initial.hotkey) hints.push("Ctrl+Shift+Space");
    if (initial.voice) hints.push("hold " + (initial.voice_key || "right ctrl") + " to talk");
    el.input.placeholder = hints.length
      ? "Ask Vigil anything…   (" + hints.join(" · ") + ")"
      : "Ask Vigil anything…";
  }

  // ---------------------------------------------------------------- boot
  async function boot() {
    // the initial window size is not honoured for a frameless window; one
    // resize after boot snaps it to the bar
    await api().fit();
    const initial = await api().ready();
    applyState(initial);
    if (initial.warning) toast(initial.warning, "error");
    flushQueued();
    requestAnimationFrame(autoGrow);
  }

  if (window.pywebview && window.pywebview.api) boot();
  else window.addEventListener("pywebviewready", boot);
})();

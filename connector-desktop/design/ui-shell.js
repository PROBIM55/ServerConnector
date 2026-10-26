/* Platform Connector UI: window frame (title bar, menu, account), page frame, dialogs, command palette and
   notifications. Pure renderers; app.js owns the DOM. */
(() => {
  const PC = window.PC, M = PC.model, S = PC.sections;
  const { esc, button, icon } = PC;

  const noteTone = { progress: ['accentSoft', 'accentInk'], ok: ['okSoft', 'okInk'], error: ['badSoft', 'badInk'], warn: ['warnSoft', 'warnInk'], info: ['hover', 'text2'] };
  const toastGlyph = { ok: ['✓', 't-ok'], error: ['!', 't-bad'], warn: ['!', 't-warn'], info: ['i', 't-idle'] };
  const NOTE_TTL = 120000;

  const titlebar = state => {
    const conn = M.connection(state);
    const signedIn = state.snapshotSeen && !conn.showSignIn;
    const [label, tone, dotTone] = conn.chip;
    const dotColor = { ok: 'var(--ok)', run: 'var(--accent)', warn: 'var(--warn)', bad: 'var(--bad)' }[dotTone];
    const maxIcon = state.maximized
      ? '<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="1.5" y="3.5" width="7" height="7" fill="none" stroke="currentColor" stroke-width="1.2"></rect><path d="M3.5 3.5v-2h7v7h-2" fill="none" stroke="currentColor" stroke-width="1.2"></path></svg>'
      : '<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="1.5" y="1.5" width="9" height="9" fill="none" stroke="currentColor" stroke-width="1.2"></rect></svg>';
    // With the system caption (old WebView2 runtime) the page draws neither window buttons nor a drag region.
    const windowButtons = state.nativeCaption ? '' : `<div style="display: flex">
<button type="button" class="wc" data-cmd="window-minimize" aria-label="Свернуть"><svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M1 6h10" stroke="currentColor" stroke-width="1.2"></path></svg></button>
<button type="button" class="wc" data-cmd="window-maximize" aria-label="${state.maximized ? 'Восстановить' : 'Развернуть'}">${maxIcon}</button>
<button type="button" class="wc wc-x" data-cmd="window-close" aria-label="Свернуть в трей"><svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="m1.5 1.5 9 9m0-9-9 9" stroke="currentColor" stroke-width="1.2"></path></svg></button>
</div>`;
    return `<header class="titlebar${state.nativeCaption ? ' native' : ''}" data-ui="titlebar">
${PC.logo(20)}<span class="brand">Платформа Коннектор</span>
<div style="flex: 1"></div>
${signedIn ? `<button type="button" data-ui="palette" class="search-btn">${PC.searchIcon(14)}<span style="flex: 1; text-align: left">Найти действие, папку, задание</span><span class="mono kbd">Ctrl K</span></button><div style="flex: 1"></div>` : ''}
${state.snapshotSeen ? `<span class="chip ${tone}" style="align-self: center${state.nativeCaption ? '; margin-right: 14px' : ''}"><span style="width: 7px; height: 7px; border-radius: 50%; background: ${dotColor}"></span>${esc(label)}</span>` : ''}
${windowButtons}</header>`;
  };

  const navButton = (state, entry) => {
    const badge = entry.badge ? entry.badge(state) : null;
    const badgeHtml = !badge ? '' : badge.dot ? `<span style="width: 7px; height: 7px; border-radius: 50%; background: var(--ok)" title="${esc(badge.title || '')}"></span>`
      : `<span class="chip ${badge.tone}" style="align-self: center; padding: 0 7px">${esc(badge.text)}</span>`;
    return `<button type="button" data-ui="go" data-page="${entry.id}" class="nav${state.ui.page === entry.id ? ' on' : ''}"${state.ui.page === entry.id ? ' aria-current="page"' : ''}>${icon(entry.icon)}<span style="flex: 1">${esc(entry.title)}</span>${badgeHtml}</button>`;
  };

  const accessText = state => S.visible(state).filter(s => s.module).map(s => s.title).join(', ') || 'Нет доступных разделов';

  const sidebar = state => {
    const visible = S.visible(state);
    const group = name => visible.filter(entry => entry.group === name).map(entry => navButton(state, entry)).join('');
    const conn = M.connection(state);
    const tools = group('tools');
    const account = state.account;
    const net = M.network(state);
    const fact = (label, value) => value ? `<span class="lbl">${label}</span><span>${esc(value)}</span>` : '';
    const popover = state.ui.accountOpen ? `<div class="card popover" data-ui="popover">
<div style="display: flex; align-items: center; gap: 8px"><span style="width: 8px; height: 8px; border-radius: 50%; background: ${conn.ready ? 'var(--ok)' : 'var(--warn)'}"></span><strong>${esc(conn.chip[0])}</strong></div>
<div style="display: grid; grid-template-columns: 86px minmax(0, 1fr); gap: 6px 10px; font-size: 12.5px">${fact('Устройство', account?.device)}${fact('Сеть', net.route ? 'NetBird · ' + net.route : account?.address ? 'NetBird' : '')}${fact('Адрес', account?.address)}<span class="lbl">Доступ</span><span>${esc(accessText(state))}</span></div>
<div style="display: flex; gap: 8px">${conn.signingOut ? '' : button('Переподключить', 'b b-sec b-sm', { cmd: 'reconnect', style: 'flex: 1', data: { section: 'settings' } }) + button('Выйти', 'b b-danger b-sm', { cmd: 'signout', style: 'flex: 1', data: { section: 'settings' } })}</div>
</div>` : '';
    return `<aside class="side" aria-label="Разделы">
${group('main')}
${tools ? '<div class="sep"></div>' + tools : ''}
<div class="grow"></div>
${group('bottom')}
<button type="button" data-ui="account" aria-expanded="${state.ui.accountOpen}" class="account row">
<span class="avatar">${account?.device ? esc(account.device.replace(/[^\p{L}\p{N}]/gu, '').slice(0, 2).toUpperCase()) || icon('user', 16) : icon('user', 16)}</span>
<span style="display: flex; flex-direction: column; min-width: 0; flex: 1"><span class="ellipsis" style="font-weight: 650; font-size: 13px">${esc(account?.device || 'Учётная запись')}</span><span class="lbl ellipsis">${esc(account?.device && conn.ready && account.address ? account.address + (net.route ? ' · ' + net.route : '') : conn.chip[0])}</span></span>
<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true" style="color: var(--dim)"><path d="m7 15 5-5 5 5"></path></svg>
</button>
${popover}
</aside>`;
  };

  const pageNote = state => {
    const now = Date.now();
    // While the connection banner is shown it already carries the connection message.
    const bannerOwnsConnection = M.connection(state).offline;
    const note = state.notices.find(n => n.page === state.ui.page && !state.ui.dismissed[n.seq] && (n.kind === 'progress' || now - (n.at || now) < NOTE_TTL) &&
      !(bannerOwnsConnection && M.CONNECTION_COMMANDS.includes(n.command)));
    if (!note) return '';
    const [bg, ink] = noteTone[note.kind] || noteTone.info;
    return `<div class="page-note" role="status" style="background: var(--${bg}); color: var(--${ink})"><span>${esc(note.message)}</span><button type="button" data-ui="note-close" data-id="${esc(note.seq)}" aria-label="Скрыть сообщение">×</button></div>`;
  };

  /** Recovery or a failed connection keeps the main window; this banner says so and offers the retry. */
  const connectionBanner = state => {
    const conn = M.connection(state);
    if (!conn.offline) return '';
    if (conn.busy) return `<section class="banner" role="status" style="background: var(--accentSoft); border: 1px solid var(--accent)"><div style="flex: 1; min-width: 260px"><div style="font-weight: 750; color: var(--accentInk)">${conn.signingOut ? 'Выходим из учётной записи' : 'Подключаемся к Платформе'}</div><div style="color: var(--text2)">${esc(state.progress || (conn.signingOut ? 'Выполняем выход…' : 'Восстанавливаем вход и защищённое соединение…'))}</div></div></section>`;
    return `<section class="banner banner-warn" role="status"><div style="flex: 1; min-width: 260px"><div style="font-weight: 750; color: var(--warnInk)">Нет связи с Платформой</div><div style="color: var(--text2)">${esc(conn.errorText || (state.status || 'Не подключено') + '.')} Разделы, которым нужен сервер, вернутся после подключения; задания на этом компьютере, журнал и настройки доступны.</div></div>${button('Повторить подключение', 'b b-pri', { cmd: 'reconnect', data: { section: 'settings' } })}</section>`;
  };

  const main = state => {
    const entry = S.byId(state.ui.page) || S.byId('home');
    const actions = entry.headerActions ? entry.headerActions(state) : [];
    return `<main class="main"><div class="main-inner">
<div class="page-head"><div style="flex: 1; min-width: 240px"><h1>${esc(entry.title)}</h1><div class="hint">${esc(entry.hint || '')}</div></div>${actions.map(a => button(a.label, 'b ' + a.cls, { cmd: a.cmd })).join('')}</div>
${connectionBanner(state)}
${pageNote(state)}
${entry.page(state)}
</div></main>`;
  };

  /* ---- confirmation dialogs (Main.dc.html dialogs) ------------------------------------------------- */
  // One confirmation per action. A native action that needs the user's consent (drive disconnection, transfer
  // and return of the previous connector, Model Sharing, IFC patch) asks through the snapshot «confirmation»;
  // the answer is the explicit «confirm-answer» command with its id. The update is confirmed here before
  // «update-apply», which then runs without another question.
  const DIALOGS = {
    update: state => {
      const running = M.runningJob(state);
      const version = state.update?.version;
      return { title: version ? `Установить версию ${version}?` : 'Установить новую версию?', text: running ? `Коннектор дождётся окончания задания «${running.title}», перезапустится и подключится сам.` : 'Коннектор перезапустится и подключится сам.',
        items: ['Учётная запись и настройки сохранятся.', 'Windows может спросить разрешение на установку.'], note: '', primary: 'Установить', primaryCls: 'b-pri', cancel: 'Позже',
        confirm: { cmd: 'update-apply', section: 'settings' } };
    }
  };
  /** The native confirmation waiting for an answer takes precedence over a page dialog. */
  const dialogModel = state => {
    const c = state.confirmation;
    if (c) return { title: c.title, text: c.text, items: c.items, note: c.note, primary: c.primary, primaryCls: c.danger ? 'b-danger' : 'b-pri', cancel: c.cancel,
      native: true, answer: accept => ({ cmd: 'confirm-answer', target: c.id, 'field-accept': accept }) };
    return state.ui.dialog && DIALOGS[state.ui.dialog.name] ? DIALOGS[state.ui.dialog.name](state, state.ui.dialog.args || {}) : null;
  };

  const dialog = state => {
    const d = dialogModel(state);
    if (!d) return '';
    const cancel = d.native ? button(d.cancel, 'b b-sec', { cmd: 'confirm-answer', data: { target: d.answer(false).target, 'field-accept': 'false' } }) : button(d.cancel, 'b b-sec', { ui: 'close-dialog' });
    const primary = d.native ? button(d.primary, 'b ' + d.primaryCls, { cmd: 'confirm-answer', data: { target: d.answer(true).target, 'field-accept': 'true' } }) : button(d.primary, 'b ' + d.primaryCls, { ui: 'confirm' });
    return `<div class="scrim" data-ui="scrim-dialog"><div class="card dialog" role="dialog" aria-modal="true" aria-label="${esc(d.title)}">
<h2>${esc(d.title)}</h2>
${d.text ? `<div style="color: var(--text2)">${esc(d.text)}</div>` : ''}
${d.items.length ? `<ul style="margin: 0; padding-left: 18px; color: var(--text2); display: flex; flex-direction: column; gap: 4px">${d.items.map(t => `<li style="overflow-wrap: anywhere">${esc(t)}</li>`).join('')}</ul>` : ''}
${d.note ? `<div class="note" style="background: var(--warnSoft); color: var(--warnInk)">${esc(d.note)}</div>` : ''}
<div style="display: flex; justify-content: flex-end; gap: 8px; margin-top: 4px">${cancel}${primary}</div>
</div></div>`;
  };

  const PALETTE_LIMIT = 8;
  const paletteItems = state => S.palette(state, state.ui.palette.query).slice(0, PALETTE_LIMIT);
  const paletteList = state => {
    const items = paletteItems(state);
    if (!items.length) return '<div class="lbl" style="padding: 14px 16px">Ничего не найдено.</div>';
    return items.map((item, i) => `<button type="button" data-ui="palette-run" data-id="${i}" class="palette-item row${i === state.ui.palette.index ? ' active' : ''}"><span class="chip t-idle" style="min-width: 74px; justify-content: center">${esc(item.kind)}</span><span style="flex: 1">${esc(item.label)}</span><span class="lbl ellipsis" style="max-width: 45%">${esc(item.where)}</span></button>`).join('');
  };
  const palette = state => !state.ui.palette.open ? '' : `<div class="scrim top" data-ui="scrim-palette"><div class="card palette" role="dialog" aria-modal="true" aria-label="Поиск по действиям">
<div style="display: flex; align-items: center; gap: 10px; padding: 14px 16px; border-bottom: 1px solid var(--line)"><span style="color: var(--dim); display: flex">${PC.searchIcon(18)}</span><input id="palette-input" type="search" value="${esc(state.ui.palette.query)}" placeholder="Что сделать?" aria-label="Что сделать" autocomplete="off" style="flex: 1; border: 0; background: transparent; color: var(--text); font-size: 15px; outline: none"><button type="button" data-ui="palette-close" class="mono" style="border: 1px solid var(--line2); border-radius: 4px; background: transparent; color: var(--dim); font-size: 11px; padding: 1px 6px; cursor: pointer">Esc</button></div>
<div id="palette-list">${paletteList(state)}</div>
<div class="lbl" style="padding: 10px 16px; border-top: 1px solid var(--line)">↑ ↓ — выбрать · Enter — выполнить · Esc — закрыть</div>
</div></div>`;

  const toasts = state => !state.toasts.length ? '' : `<div class="toasts">${state.toasts.map(t => {
    const [glyph, tone] = toastGlyph[t.kind] || toastGlyph.info;
    return `<div role="${t.kind === 'error' ? 'alert' : 'status'}" class="card toast ${t.kind}"><span class="chip ${tone}">${glyph}</span><span>${esc(t.message)}</span><button type="button" data-ui="toast-close" data-id="${esc(t.id)}" aria-label="Закрыть уведомление">×</button></div>`;
  }).join('')}</div>`;

  const app = state => {
    const conn = M.connection(state);
    const shell = state.snapshotSeen && !conn.showSignIn;
    const body = !state.snapshotSeen ? '<div class="body"></div>'
      : shell ? `<div class="body">${sidebar(state)}${main(state)}</div>` : PC.pages.signin(state);
    return titlebar(state) + body + dialog(state) + (shell ? palette(state) : '') + toasts(state);
  };

  PC.shell = { app, titlebar, sidebar, main, dialog, dialogModel, palette, paletteList, paletteItems, toasts, DIALOGS };
})();

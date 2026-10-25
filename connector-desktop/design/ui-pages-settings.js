/* Platform Connector UI: «Настройки» and the sign-in screen (Main.dc.html, SignIn.dc.html). */
(() => {
  const PC = window.PC, M = PC.model;
  const { esc, button, chip } = PC;
  PC.pages = PC.pages || {};

  const version = state => String(state.module['app-version'] || '').trim() || '—';

  PC.pages.settings = state => {
    const conn = M.connection(state);
    const legacy = state.legacy || { status: '', transferred: false, canTransfer: false, canRestore: false };
    const sections = PC.sections.visible(state).filter(s => s.module).map(s => s.title).join(', ') || 'Нет доступных разделов';
    const autostart = state.fields.autostart !== false;
    const legacyButton = legacy.canRestore
      ? button('Вернуть прежний коннектор', 'b b-sec', { cmd: 'legacy-restore', style: 'align-self: flex-start' })
      : legacy.canTransfer ? button('Перевести компьютер на Платформа Коннектор', 'b b-pri', { cmd: 'legacy-transfer', style: 'align-self: flex-start' }) : '';
    return `<div style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px">
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 14px">
<h2 class="h2">Обновление</h2>
<div style="display: flex; align-items: center; gap: 12px"><span class="mono" style="padding: 3px 8px; border-radius: 6px; background: var(--hover)">${esc(version(state))}</span>${state.updateAvailable ? `<span style="color: var(--dim)">→</span><span class="mono" style="padding: 3px 8px; border-radius: 6px; background: var(--okSoft); color: var(--okInk)">новая версия</span>` : `<span class="lbl">установлена эта версия</span>`}</div>
${state.updateAvailable ? '<span style="color: var(--text2)">Обновление установится после текущего задания; коннектор перезапустится и подключится сам.</span>' : ''}
<div class="acts">${state.updateAvailable ? button('Установить после текущего задания', 'b b-pri', { ui: 'dialog', data: { dialog: 'update' } }) : ''}${button('Проверить обновления', 'b b-sec', { cmd: 'update-check' })}</div>
</section>
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 14px">
<h2 class="h2">Оформление и запуск</h2>
<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 12px"><span style="width: 110px" class="lbl">Тема</span><div class="seg" role="tablist" aria-label="Тема"><button type="button" role="tab" data-ui="theme" data-id="dark" aria-selected="${state.theme !== 'light'}">Тёмная</button><button type="button" role="tab" data-ui="theme" data-id="light" aria-selected="${state.theme === 'light'}">Светлая</button></div></div>
<label style="display: flex; align-items: center; gap: 10px; cursor: pointer"><input type="checkbox" data-ui="autostart"${autostart ? ' checked' : ''} style="width: 18px; height: 18px; accent-color: var(--accent)"><span>Запускать вместе с Windows и работать в трее</span></label>
<span class="lbl">Закрытие окна сворачивает коннектор в трей. Полный выход — через меню значка в трее.</span>
</section>
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 12px">
<h2 class="h2">Учётная запись и устройство</h2>
<div style="display: grid; grid-template-columns: 120px minmax(0, 1fr); gap: 7px 12px"><span class="lbl">Состояние</span><span>${esc(conn.chip[0])}</span><span class="lbl">Разделы</span><span>${esc(sections)}</span></div>
<div style="display: flex; gap: 8px">${button('Переподключить', 'b b-sec b-sm', { cmd: 'reconnect' })}${button('Выйти', 'b b-danger b-sm', { cmd: 'signout' })}</div>
</section>
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 12px">
<h2 class="h2">Прежний Structura Connector</h2>
<div style="color: var(--text2)">${esc(legacy.status || 'Сведения о прежнем коннекторе ещё не получены.')}</div>
<div class="lbl">Перевод: прежний коннектор закрывается и не запускается с Windows, его VPN остаётся. BIM_Models открывается здесь. Вернуть можно одной кнопкой.</div>
${legacyButton}
</section>
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 12px">
<h2 class="h2">Журнал и диагностика</h2>
<span style="color: var(--text2)">Журнал хранит события этого сеанса: вход, задания, ошибки. Диагностика — файл для администратора без паролей и токенов.</span>
<div class="acts">${button('Открыть журнал', 'b b-sec b-sm', { cmd: 'settings-log' })}${button('Сохранить диагностику', 'b b-sec b-sm', { cmd: 'diagnostics' })}${button('Очистить журнал', 'b b-danger b-sm', { cmd: 'log-clear' })}</div>
</section>
<section class="card" style="padding: 20px; display: flex; flex-direction: column; gap: 12px">
<h2 class="h2">О программе</h2>
<div style="display: grid; grid-template-columns: 120px minmax(0, 1fr); gap: 7px 12px"><span class="lbl">Версия</span><span>${esc(version(state))}</span><span class="lbl">Сервер</span><span>bimplatforma.ru</span></div>
${button('Лицензии сторонних компонентов', 'b-ghost', { cmd: 'licenses-open', style: 'align-self: flex-start; padding: 0' })}
</section>
</div>`;
  };

  /* ---- sign-in -------------------------------------------------------------------------------------- */
  const STEPS = [
    ['Вход в браузере', 'Учётная запись Structura и выбор компании'],
    ['Защищённое соединение', 'NetBird ставится вместе с коннектором'],
    ['Сертификат устройства', 'Компьютер получает собственный ключ доступа'],
    ['Права и разделы', 'Tekla, AutoCAD, папки — по назначению администратора'],
    ['Готово', 'Коннектор работает в фоне и в трее']
  ];

  /** The single place that maps the connection state to the sign-in screen (S01–S05). */
  PC.pages.signinModel = state => {
    const conn = M.connection(state);
    const working = conn.waiting || conn.busy;
    let primary;
    if (conn.variant === 'pending') primary = { label: 'Ожидаем выдачи доступа…', waiting: true };
    else if (working) primary = { label: conn.signingOut ? 'Выходим из учётной записи…' : conn.waiting ? 'Ожидаем вход в браузере…' : 'Настраиваем подключение…', waiting: true };
    else if (conn.variant === 'error' && !conn.needsSignIn) primary = { label: 'Повторить подключение', cmd: conn.hasCredential ? 'reconnect' : 'connect' };
    else if (conn.hasCredential && !conn.needsSignIn) primary = { label: 'Повторить подключение', cmd: 'reconnect' };
    else primary = { label: 'Войти через Structura', cmd: 'connect' };
    // Cancelling before enrollment only abandons the browser request; with a stored device it is a sign-out.
    const secondary = conn.signingOut ? null
      : working || conn.variant === 'pending' ? { label: conn.hasCredential && !conn.waiting ? 'Выйти' : 'Отменить', cmd: 'signout' }
      : conn.hasCredential ? { label: 'Выйти', cmd: 'signout' } : null;
    return { conn, primary, secondary, phase: conn.phase };
  };

  PC.pages.signin = state => {
    const view = PC.pages.signinModel(state);
    const { conn, primary, secondary, phase } = view;
    const steps = STEPS.map(([title, hint], i) => {
      const done = phase > i + 1;
      const active = phase === i + 1;
      return `<li style="display: flex; gap: 12px; align-items: flex-start"><span class="si-step${done ? ' done' : active ? ' active' : ''}">${done ? '✓' : i + 1}</span><span style="display: flex; flex-direction: column"><span style="color: var(--siHead); font-weight: 600">${title}</span><span style="color: var(--siDim)">${hint}</span></span></li>`;
    }).join('');
    return `<div class="signin">
<section style="flex: 0 1 440px; display: flex; flex-direction: column; gap: 22px">
<div style="display: flex; align-items: center; gap: 12px">${PC.logo(40, 2.4, 'var(--siPriInk)')}<div style="display: flex; flex-direction: column"><strong style="font-size: 18px; color: var(--siHead)">Платформа Коннектор</strong><span style="color: var(--siDim)">Связывает этот компьютер с Платформой и Structura</span></div></div>
<h1>Войдите, чтобы подключить компьютер</h1>
<p style="margin: 0; color: var(--siSub); font-size: 14px">Откроется браузер со страницей входа Structura. Пароль вводится там — коннектор его не видит. После входа выберите компанию, и всё настроится само.</p>
${conn.variant === 'error' ? `<div style="padding: 12px 14px; border-radius: 10px; background: var(--badSoft); color: var(--badInk)" role="alert">${esc(conn.errorText)}</div>` : ''}
${conn.variant === 'pending' ? `<div style="padding: 12px 14px; border-radius: 10px; background: var(--warnSoft); color: var(--warnInk)">Вход выполнен. Администратор ещё не выдал доступ к разделам — коннектор подключится сам, как только доступ появится.</div>` : ''}
<div style="display: flex; flex-wrap: wrap; gap: 10px">
${primary.waiting ? `<button type="button" class="si-btn waiting" disabled aria-busy="true">${esc(primary.label)}</button>` : button(primary.label, 'si-btn', { cmd: primary.cmd })}
${secondary ? button(secondary.label, 'si-sec', { cmd: secondary.cmd }) : ''}
</div>
<span style="color: var(--siDim); font-size: 12px">Прежний Structura Connector можно не удалять: он продолжит работать рядом.</span>
<div class="si-links">${button('Журнал', 'linklike', { cmd: 'settings-log', data: { section: 'settings' } })}${button('Сохранить диагностику', 'linklike', { cmd: 'diagnostics', data: { section: 'settings' } })}${state.legacy?.canRestore ? button('Вернуть прежний Structura Connector', 'linklike', { cmd: 'legacy-restore', data: { section: 'settings' } }) : ''}</div>
</section>
<section style="flex: 0 1 420px; padding: 22px; border-radius: 12px; border: 1px solid var(--siLine); background: var(--siPanel); display: flex; flex-direction: column; gap: 14px">
<h2 style="margin: 0; font-size: 15px; color: var(--siHead)">Что произойдёт после входа</h2>
<ol style="margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 14px">${steps}</ol>
</section></div>`;
  };
})();

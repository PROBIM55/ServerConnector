/* Platform Connector UI: «Главная» and «Задания». Markup follows Main.dc.html; data comes from the snapshot only. */
(() => {
  const PC = window.PC, M = PC.model;
  const { esc, button, chip, tones } = PC;
  PC.pages = PC.pages || {};

  const dotStyle = color => `width: 9px; height: 9px; flex: 0 0 9px; border-radius: 50%; background: ${color};`;
  const toneColor = { ok: 'var(--ok)', run: 'var(--accent)', bad: 'var(--bad)', warn: 'var(--warn)', idle: 'var(--line2)' };
  const toneVars = { run: ['accentSoft', 'accentInk'], ok: ['okSoft', 'okInk'], bad: ['badSoft', 'badInk'], warn: ['warnSoft', 'warnInk'], idle: ['hover', 'text2'] };
  const jobSub = job => 'На этом ПК' + (job.output ? ' · ' + job.output : '');

  /* Attention items are derived from facts in the snapshot (see PC.sections for the owning section). */
  const attention = state => {
    const items = [];
    const agr = M.agr(state);
    if (M.hasAny(state, 'agr') && agr.state === 'missing')
      items.push({ icon: '!', tone: 't-bad', title: 'Blender для АГР не готов', text: 'Публикация АГР ждёт установки Blender' + (state.agrRuntime ? ` ${state.agrRuntime.version}. Установка ${PC.formatSize(state.agrRuntime.sizeBytes)}, один раз.` : '.'), btn: 'Установить', cls: 'b-sec', ui: 'go', data: { page: 'agr' } });
    const failed = M.jobs(state).find(job => job.tone === 'bad');
    if (failed) items.push({ icon: '!', tone: 't-bad', title: failed.title + ' — ошибка', text: jobSub(failed) + (failed.hasReport ? '. Причина — в отчёте задания.' : '. Подробности доступны в журнале.'), btn: 'Открыть', cls: 'b-sec', ui: 'job', data: { id: failed.id } });
    const folders = M.folders(state);
    if (M.hasAny(state, 'folders') && folders.neighbour && folders.legacy.canTransfer) {
      const names = folders.viaLegacy.map(f => f.displayName).join(', ');
      items.push({ icon: 'i', tone: 't-warn', title: names + ' — через прежний коннектор', text: 'Он работает на этом компьютере. Остальные папки доступны здесь.', btn: 'Подробнее', cls: 'b-sec', ui: 'go', data: { page: 'folders' } });
    }
    if (state.updateAvailable)
      items.push({ icon: '↑', tone: 't-ok', title: 'Доступна новая версия коннектора', text: 'Установится после текущего задания.', btn: 'Что нового', cls: 'b-sec', ui: 'go', data: { page: 'settings' } });
    return items;
  };

  PC.pages.home = state => {
    const items = attention(state);
    const running = M.runningJob(state);
    const tiles = PC.sections.tiles(state).slice(0, 4);
    const conn = M.connection(state);
    const summary = (running ? 'Одно задание выполняется. ' : 'Активных заданий нет. ') + (items.length ? 'Требуют решения: ' + items.length + '.' : 'Решений не требуется.');
    const head = conn.ready ? { title: 'Компьютер на связи с Платформой', bg: 'okSoft', ink: 'ok', path: 'm5 12 4.5 4.5L19 7' }
      : conn.busy ? { title: 'Подключаемся к Платформе', bg: 'accentSoft', ink: 'accentInk', path: 'M12 6v6l4 2' }
      : { title: 'Нет связи с Платформой', bg: 'warnSoft', ink: 'warnInk', path: 'M12 7v6M12 17h.01' };
    const runningCard = running ? `<button type="button" data-ui="job" data-id="${esc(running.id)}" class="row" style="flex: 0 1 360px; display: flex; flex-direction: column; gap: 8px; padding: 12px 14px; border-radius: 10px; border: 1px solid var(--line); background: var(--raised); color: inherit; text-align: left; cursor: pointer; font: inherit">
<span style="display: flex; justify-content: space-between; gap: 10px"><span style="font-weight: 650">${esc(running.title)}</span><span style="color: var(--accentInk); font-weight: 700">${running.progress} %</span></span>
<span class="bar"><span style="width: ${running.progress}%"></span></span>
<span class="lbl">${esc(jobSub(running))}</span></button>` : '';
    const recent = M.jobs(state).slice(0, 4);
    return `<section class="card" style="display: flex; flex-wrap: wrap; align-items: center; gap: 20px; padding: 18px 20px">
<span style="width: 44px; height: 44px; border-radius: 12px; background: var(--${head.bg}); color: var(--${head.ink}); display: grid; place-items: center"><svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${head.path}"></path></svg></span>
<div style="flex: 1 1 260px"><div style="font-size: 17px; font-weight: 750">${head.title}</div><div style="color: var(--dim)">${esc(summary)}</div></div>
${runningCard}</section>
${tiles.length ? `<div style="display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 14px">${tiles.map(tile => `<button type="button" data-ui="go" data-page="${esc(tile.page)}" class="tile"><span style="display: flex; align-items: center; justify-content: space-between"><span class="lbl" style="font-size: 12.5px">${esc(tile.label)}</span><span style="${dotStyle(toneColor[tile.tone] || toneColor.idle)}"></span></span><span style="font-size: 17px; font-weight: 750">${esc(tile.value)}</span><span class="lbl ellipsis">${esc(tile.meta)}</span></button>`).join('')}</div>` : ''}
<div class="split">
<section class="card" style="flex: 3 1 420px; min-width: 0; overflow: hidden">
<h2 class="h2" style="padding: 16px 18px 10px">Требует внимания</h2>
${items.length ? items.map(item => `<div class="row" style="display: flex; gap: 14px; align-items: center; padding: 12px 18px; border-top: 1px solid var(--line)">
<span class="chip ${item.tone}" style="align-self: center; width: 30px; height: 30px; padding: 0; justify-content: center; border-radius: 9px; font-size: 14px">${esc(item.icon)}</span>
<div style="flex: 1; min-width: 0"><div style="font-weight: 650">${esc(item.title)}</div><div style="color: var(--dim)">${esc(item.text)}</div></div>
${button(item.btn, 'b b-sm ' + item.cls, { ui: item.ui, data: item.data })}</div>`).join('') : `<div class="empty" style="border-top: 1px solid var(--line)">Сейчас ничего не требует вашего решения.</div>`}
</section>
<section class="card" style="flex: 2 1 300px; min-width: 0; overflow: hidden">
<div style="display: flex; align-items: center; padding: 16px 18px 10px"><h2 class="h2" style="flex: 1">Последние задания</h2>${button('Все задания', 'b-ghost', { ui: 'go', data: { page: 'jobs' } })}</div>
${recent.length ? recent.map(job => `<button type="button" data-ui="job" data-id="${esc(job.id)}" class="row" style="display: flex; width: 100%; align-items: center; gap: 12px; padding: 10px 18px; border: 0; border-top: 1px solid var(--line); background: transparent; color: inherit; text-align: left; cursor: pointer; font: inherit"><span style="${dotStyle(toneColor[job.tone])}"></span><span style="flex: 1; min-width: 0"><span style="display: block; font-weight: 600">${esc(job.title)}</span><span class="lbl ellipsis" style="display: block">${esc(jobSub(job))}</span></span><span class="lbl">${esc(job.statusText)}</span></button>`).join('') : `<div class="empty" style="border-top: 1px solid var(--line)">Заданий пока нет.</div>`}
</section></div>`;
  };
  PC.pages.home.attention = attention;

  const FILTERS = [['all', 'Все', () => true], ['running', 'Выполняются', job => job.running], ['errors', 'Ошибки', job => job.tone === 'bad']];

  PC.pages.jobs = state => {
    const all = M.jobs(state);
    const query = state.ui.jobQuery.trim().toLowerCase();
    const pass = (FILTERS.find(f => f[0] === state.ui.jobFilter) || FILTERS[0])[2];
    const rows = all.filter(pass).filter(job => !query || (job.title + ' ' + job.output).toLowerCase().includes(query));
    const sel = all.find(job => job.id === state.ui.jobId) || rows[0] || null;
    const hasFinished = all.some(job => !job.running && job.status !== '—');
    const head = `<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 12px">
<div role="tablist" aria-label="Фильтр заданий" class="seg seg-sm" style="flex-wrap: wrap">${FILTERS.map(([id, label, f]) => `<button type="button" role="tab" data-ui="filter" data-id="${id}" aria-selected="${state.ui.jobFilter === id}">${label}<span style="opacity: .65; margin-left: 6px">${all.filter(f).length}</span></button>`).join('')}</div>
<div style="flex: 1"></div>
<label style="display: flex; align-items: center; gap: 8px; height: 34px; width: 220px; padding: 0 10px; border-radius: 8px; border: 1px solid var(--line2); background: var(--field); color: var(--dim)">${PC.searchIcon(14)}<input id="job-query" type="search" placeholder="Задание или файл" aria-label="Найти задание" value="${esc(state.ui.jobQuery)}" style="flex: 1; min-width: 0; border: 0; background: transparent; color: var(--text); font-size: 13px; outline: none"></label>
${button('Очистить завершённые', 'b b-sec', { cmd: 'history-clear', disabled: !hasFinished, data: { section: 'converters' } })}
</div>`;
    if (!all.length) return head + `<section class="card"><div class="empty">Заданий пока нет. Здесь появятся конвертации IFC и FBX, запущенные на этом компьютере.</div></section>`;
    const list = rows.map(job => {
      const [bg, ink] = toneVars[job.tone];
      const on = sel && sel.id === job.id;
      return `<button type="button" data-ui="job" data-id="${esc(job.id)}" class="row" style="display: flex; align-items: center; gap: 14px; width: 100%; padding: 13px 18px; border: 0; border-bottom: 1px solid var(--line); color: inherit; text-align: left; cursor: pointer; font: inherit; ${on ? 'background: var(--accentSoft); box-shadow: inset 3px 0 0 var(--accent);' : 'background: transparent;'}">
<span style="width: 36px; height: 36px; flex: 0 0 36px; border-radius: 10px; display: grid; place-items: center; font-weight: 800; background: var(--${bg}); color: var(--${ink});">${job.glyph}</span>
<span style="flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px">
<span style="font-weight: 650" class="ellipsis">${esc(job.title)}</span>
<span class="lbl ellipsis">${esc(jobSub(job))}</span>
${job.running ? `<span style="margin-top: 6px; height: 4px; border-radius: 2px; background: var(--hover); overflow: hidden; display: block"><span style="display: block; width: ${job.progress}%; height: 4px; background: var(--accent)"></span></span>` : ''}
</span>${chip(job.statusText, tones[job.tone], 'align-self: center')}</button>`;
    }).join('') || `<div class="empty">Нет заданий с таким условием.</div>`;
    let detail = '';
    if (sel) {
      const acts = [];
      if (sel.running) acts.push(button('Отменить', 'b b-danger', { cmd: 'cancel', data: { section: 'converters', target: sel.id } }));
      if (sel.hasResult) acts.push(button('Открыть результат', 'b b-pri', { cmd: 'results', data: { section: 'converters', target: sel.id } }));
      if (sel.hasReport) acts.push(button(sel.kind === 'ifc' ? 'Отчёт JSON' : 'Отчёт', 'b b-sec', { cmd: 'report', data: { section: 'converters', target: sel.id } }));
      if (sel.hasValidation) acts.push(button('Проверить результат', 'b b-sec', { cmd: 'ifc-validation', data: { section: 'converters', target: sel.id } }));
      detail = `<aside class="card" style="flex: 2 1 320px; min-width: 0; padding: 20px; display: flex; flex-direction: column; gap: 14px; box-sizing: border-box">
<div style="display: flex; flex-direction: column; gap: 4px"><span class="lbl">Конвертер на этом ПК</span><h2 style="margin: 0; font-size: 18px; font-weight: 800">${esc(sel.title)}</h2><span style="color: var(--text2)">${esc(jobSub(sel))}</span></div>
${chip(sel.running ? 'Выполняется · ' + sel.progress + ' %' : sel.statusText, tones[sel.tone])}
${sel.tone === 'bad' ? `<div class="note" style="background: var(--badSoft); color: var(--badInk)">Задание завершилось с ошибкой. ${sel.hasReport ? 'Причина записана в отчёте задания.' : 'Подробности доступны в журнале.'}</div>` : ''}
<div style="display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 6px 12px; font-size: 13px"><span class="lbl">Выполняется</span><span>На этом компьютере</span>${sel.output ? `<span class="lbl">Результат</span><span class="ellipsis">${esc(sel.output)}</span>` : ''}</div>
${acts.length ? `<div class="acts">${acts.join('')}</div>` : ''}
</aside>`;
    }
    return head + `<div class="split"><section class="card" style="flex: 3 1 460px; min-width: 0; overflow: hidden">${list}</section>${detail}</div>`;
  };
})();

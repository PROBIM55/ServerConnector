/* Platform Connector UI: Tekla, AutoCAD, Папки, Конвертеры, АГР. Markup follows Main.dc.html. */
(() => {
  const PC = window.PC, M = PC.model;
  const { esc, button, chip, tones } = PC;
  PC.pages = PC.pages || {};

  const dot9 = color => `<span style="width: 9px; height: 9px; border-radius: 50%; background: ${color}"></span>`;
  const dot8 = color => `<span style="width: 8px; height: 8px; border-radius: 50%; background: ${color}; flex: 0 0 8px"></span>`;
  const field = (state, id, empty) => String(state.fields[id] ?? '').trim() || empty;
  const choose = (target, label = 'Изменить', style = 'padding: 0') => button(label, 'b-ghost', { cmd: 'choose-folder', style, data: { target } });
  const card = (cls, html, style = '') => `<section class="card ${cls}" style="${style}">${html}</section>`;
  const fileTone = status => /Готово/.test(status) ? 't-ok' : /Ошибка|Превышено|Прервано/.test(status) ? 't-bad'
    : /Выполняется|Принято/.test(status) ? 't-run' : 't-idle';
  const size = bytes => {
    if (!bytes) return '—';
    if (bytes < 1024) return bytes.toLocaleString('ru-RU') + ' Б';
    if (bytes < 1024 * 1024) return (bytes / 1024).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' КБ';
    return (bytes / 1024 / 1024).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' МБ';
  };
  PC.formatSize = size;

  /* ---- Tekla ---------------------------------------------------------------------------------------- */
  const TEKLA_TABS = [['standard', 'Стандарт фирмы'], ['sharing', 'Model Sharing'], ['ifc', 'Патч IFC-экспорта']];
  const STANDARD = [
    ['xs', 'Папка фирмы', 'Настройки и стандарты компании', 'firm-version'],
    ['ext', 'Расширения', 'Дополнительные инструменты Tekla', 'extensions-version'],
    ['lib', 'Библиотеки Grasshopper', 'Общие библиотеки компонентов', 'libraries-version']
  ];
  const teklaName = state => 'Tekla Structures' + (state.tekla?.version ? ' ' + state.tekla.version : '');

  const bridgeBlock = state => {
    const br = M.bridge(state);
    if (br.state === 'down') return `<section style="display: flex; flex-wrap: wrap; align-items: center; gap: 16px; padding: 16px 18px; border-radius: 12px; background: var(--warnSoft); border: 1px solid var(--warn)">
<span style="width: 40px; height: 40px; border-radius: 10px; background: var(--raised); color: var(--warnInk); display: grid; place-items: center; font-weight: 800">!</span>
<div style="flex: 1; min-width: 260px"><div style="font-weight: 750; color: var(--warnInk)">Tekla не отвечает</div><div style="color: var(--text2)">Откройте модель в ${esc(teklaName(state))} и нажмите «Подключить». Задания Платформы для Tekla подождут.</div></div>
${button('Подключить Tekla', 'b b-pri', { cmd: 'bridge-start' })}</section>`;
    const ok = br.state === 'ok';
    const running = M.jobs(state).find(job => job.running && job.kind === 'tekla' && job.source === 'platform');
    const bridgeMeta = [br.address, br.checkedAt ? M.time.checkedText(br.checkedAt) : ''].filter(Boolean).join(' · ') || br.text;
    return `<section class="card" style="display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); overflow: hidden">
<div style="padding: 16px 18px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--line); min-width: 0"><span class="lbl">Открытая модель</span><span style="font-size: 16px; font-weight: 750" class="ellipsis">${esc(br.model || (ok ? 'Модель не определена' : 'Не проверено'))}</span><span class="lbl">${esc(teklaName(state))}</span></div>
<div style="padding: 16px 18px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--line); min-width: 0"><span class="lbl">Мост к Tekla</span><span style="display: flex; align-items: center; gap: 8px; font-size: 16px; font-weight: 750">${dot9(ok ? 'var(--ok)' : 'var(--dim)')}${ok ? 'Работает' : 'Не проверено'}</span><span class="lbl ellipsis">${esc(bridgeMeta)}</span></div>
<div style="padding: 16px 18px; display: flex; flex-direction: column; gap: 4px; min-width: 0"><span class="lbl">Из Платформы сейчас</span><span style="font-size: 16px; font-weight: 750" class="ellipsis">${esc(running ? running.title + ' · ' + running.progress + ' %' : 'Заданий нет')}</span>${running ? button('Открыть задание', 'b-ghost', { ui: 'job', style: 'align-self: flex-start; padding: 0', data: { id: running.id } }) : '<span class="lbl">Задания появятся, когда Платформа их отправит</span>'}</div>
</section>`;
  };

  const standardTab = state => {
    const std = M.standard(state);
    const byId = Object.fromEntries(std.sections.map(s => [s.id, s]));
    const source = std.source ? `Источник: ${std.source}${std.checkedAt ? ' · ' + M.time.checkedText(std.checkedAt) : ''}. ` : '';
    return `<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 12px">
<span style="flex: 1; min-width: 260px; color: var(--dim)">${esc(source)}Перед обновлением закройте Tekla — иначе файлы заняты.</span>
${button('Журнал синхронизации', 'b-ghost', { cmd: 'firm-log' })}
${button('Обновить всё', 'b b-pri', { cmd: 'firm-sync-all' })}
</div>
<div style="display: flex; flex-direction: column; gap: 10px">${STANDARD.map(([id, name, desc, versionKey]) => {
    const section = byId[id];
    const path = section ? section.path : field(state, id + '-path', '');
    const installed = section ? section.installed || 'не установлено' : String(state.module[versionKey] || '').trim() || 'Не синхронизировано';
    const synced = section ? !!section.installed : installed !== 'Не синхронизировано';
    const update = section?.updateAvailable === true;
    const status = !path ? ['Нужна папка', 't-idle'] : section?.failed ? ['Ошибка обновления', 't-bad'] : update ? ['Есть обновление', 't-warn']
      : synced ? ['Актуально', 't-ok'] : ['Не синхронизировано', 't-warn'];
    const action = !path ? button('Выбрать папку', 'b b-sm b-sec', { cmd: 'choose-folder', style: 'min-width: 116px', data: { target: id + '-path' } })
      : update || !synced || section?.failed ? button('Обновить', 'b b-sm b-pri', { cmd: id + '-sync', style: 'min-width: 116px' })
      : button('Открыть папку', 'b b-sm b-sec', { cmd: id + '-open', style: 'min-width: 116px' });
    return `<section class="card" style="display: flex; flex-wrap: wrap; align-items: center; gap: 18px; padding: 16px 18px">
<div style="flex: 1 1 200px; min-width: 0"><div style="font-weight: 750; font-size: 14.5px">${name}</div><div class="lbl">${desc}</div></div>
<div style="flex: 0 0 auto; display: flex; align-items: center; gap: 8px; white-space: nowrap"><span class="mono" style="padding: 3px 8px; border-radius: 6px; background: var(--hover)">${esc(installed)}</span>${update ? `<span style="color: var(--dim)">→</span><span class="mono" style="padding: 3px 8px; border-radius: 6px; background: var(--warnSoft); color: var(--warnInk)">${esc(section.available)}</span>` : ''}</div>
<div style="flex: 2 1 160px; min-width: 0; display: flex; flex-direction: column; gap: 2px"><span class="mono ellipsis" style="display: block; color: var(--text2)" title="${esc(path)}">${esc(path || 'Папка не выбрана')}</span><span style="display: flex; gap: 14px">${choose(id + '-path', 'Изменить папку')}</span></div>
${chip(status[0], status[1], 'align-self: center')}
${action}
</section>`;
  }).join('')}</div>`;
  };

  const sharingTab = state => {
    const status = String(state.module['sharing-status'] || 'Не проверено');
    const color = /выполняется/i.test(status) ? 'var(--accent)' : /Доступно для настройки/.test(status) ? 'var(--warn)' : 'var(--dim)';
    return `<div class="split">
<section class="card" style="flex: 3 1 440px; padding: 20px; display: flex; flex-direction: column; gap: 16px">
<h2 class="h2">Tekla Model Sharing</h2>
<div class="kv"><span class="lbl">Установка Tekla</span><span style="display: flex; flex-wrap: wrap; gap: 10px; align-items: center"><span class="mono">${esc(field(state, 'sharing-bin', 'Не выбрана'))}</span>${choose('sharing-bin')}</span>${state.tekla?.version ? `<span class="lbl">Версия</span><span>${esc(teklaName(state))}</span>` : ''}<span class="lbl">Совместная работа</span><span style="display: flex; align-items: center; gap: 8px">${dot8(color)}${esc(status)}</span></div>
<div class="note" style="background: var(--warnSoft); color: var(--warnInk)">Перед настройкой закройте Tekla Structures. Коннектор предупредит, если она открыта.</div>
<div class="acts">${button('Настроить Model Sharing', 'b b-pri', { cmd: 'sharing-config' })}${button('Проверить', 'b b-sec', { cmd: 'sharing-check' })}${button('Журнал Model Sharing', 'b-ghost', { cmd: 'sharing-log' })}</div>
</section>
<section class="card" style="flex: 2 1 280px; padding: 20px; display: flex; flex-direction: column; gap: 10px">
<h2 class="h2">Что делает настройка</h2>
<ul class="bullets"><li>Подключает службу кэша Model Sharing к выбранной установке Tekla.</li><li>Проверяет, что Tekla видит службу после настройки.</li><li>Ход и итог видны в «Заданиях».</li></ul>
</section></div>`;
  };

  const patchTab = state => {
    const status = String(state.module['ifc-patch-status'] || 'Не проверено');
    const color = /установлен$/i.test(status.replace(/\.$/, '')) && !/не установлен/i.test(status) ? 'var(--ok)' : /выполняется/i.test(status) ? 'var(--accent)' : 'var(--dim)';
    return `<div class="split">
<section class="card" style="flex: 3 1 440px; padding: 20px; display: flex; flex-direction: column; gap: 16px">
<h2 class="h2">Патч IFC-экспорта</h2>
<div class="kv"><span class="lbl">Установка Tekla</span><span style="display: flex; flex-wrap: wrap; gap: 10px; align-items: center"><span class="mono">${esc(field(state, 'ifc-bin', 'Не выбрана'))}</span>${choose('ifc-bin')}</span><span class="lbl">Патч</span><span style="display: flex; align-items: center; gap: 8px">${dot8(color)}${esc(status)}</span><span class="lbl">Набор патча</span><span style="display: flex; flex-wrap: wrap; gap: 10px; align-items: center"><span class="mono">${esc(field(state, 'patch-folder', 'Встроенный, из коннектора'))}</span>${choose('patch-folder', 'Указать свой набор')}</span></div>
<div class="acts">${button('Установить патч', 'b b-pri', { cmd: 'ifc-apply' })}${button('Проверить установку', 'b b-sec', { cmd: 'ifc-detect' })}${button('Вернуть исходные файлы', 'b b-danger', { cmd: 'ifc-rollback' })}${button('Журнал', 'b-ghost', { cmd: 'ifc-log' })}</div>
</section>
<section class="card" style="flex: 2 1 280px; padding: 20px; display: flex; flex-direction: column; gap: 10px">
<h2 class="h2">Как это работает</h2>
<ul class="bullets"><li>Заменяет файлы IFC-экспорта Tekla на исправленные.</li><li>Исходные файлы сохраняются — их можно вернуть одной кнопкой.</li><li>Tekla должна быть закрыта на время установки.</li></ul>
</section></div>`;
  };

  PC.pages.tekla = state => {
    const platform = M.has(state, 'platform', 'tekla'), structura = M.has(state, 'structura', 'tekla');
    let html = platform ? bridgeBlock(state) : '';
    if (structura) {
      const tab = TEKLA_TABS.some(t => t[0] === state.ui.teklaTab) ? state.ui.teklaTab : 'standard';
      const badge = { standard: M.standard(state).updates.length > 0 };
      html += `<div role="tablist" aria-label="Разделы Tekla" class="tabs">${TEKLA_TABS.map(([id, label]) => `<button type="button" role="tab" data-ui="tab" data-id="${id}" aria-selected="${tab === id}">${label}${badge[id] ? '<span class="tab-badge" title="Есть обновление"></span>' : ''}</button>`).join('')}</div>`;
      html += ({ standard: standardTab, sharing: sharingTab, ifc: patchTab })[tab](state);
    }
    return html;
  };

  /* ---- AutoCAD -------------------------------------------------------------------------------------- */
  PC.pages.autocad = state => {
    const cad = M.cad(state);
    const rows = cad.sessions.map(session => {
      const on = cad.selected && cad.selected.key === session.key;
      return `<button type="button" data-ui="cad" data-id="${esc(session.key)}" class="row" style="display: flex; flex-wrap: wrap; align-items: center; gap: 14px; width: 100%; padding: 14px 18px; border: 0; border-top: 1px solid var(--line); color: inherit; text-align: left; cursor: pointer; font: inherit; ${on ? 'background: var(--accentSoft); box-shadow: inset 3px 0 0 var(--accent)' : 'background: transparent'}">
<span style="width: 40px; height: 40px; border-radius: 10px; background: var(--badSoft); color: var(--badInk); display: grid; place-items: center; font-weight: 800">A</span>
<div style="flex: 1; min-width: 200px"><div style="font-weight: 750">${esc(session.document || session.name)}</div><div class="lbl">${esc([session.name, session.version].filter(Boolean).join(' · '))}</div></div>
${on ? chip('Выбран', 't-ok', 'align-self: center') : ''}</button>`;
    }).join('');
    const sel = cad.selected;
    return `<div class="split">
<section class="card" style="flex: 3 1 440px; min-width: 0; overflow: hidden">
<h2 class="h2" style="padding: 16px 18px 10px">Найденные сеансы</h2>
${rows || `<div style="padding: 14px 18px; border-top: 1px solid var(--line)">Сеансы AutoCAD не найдены.</div>`}
<div style="padding: 12px 18px; border-top: 1px solid var(--line); color: var(--dim)">Не видите нужный чертёж? Откройте его в AutoCAD и нажмите «Найти сеансы».</div>
</section>
<section class="card" style="flex: 2 1 300px; padding: 20px; display: flex; flex-direction: column; gap: 14px">
<h2 class="h2">Выбранный чертёж</h2>
<div style="display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 8px 12px"><span class="lbl">Чертёж</span><span>${esc(sel?.document || '—')}</span><span class="lbl">AutoCAD</span><span>${esc(sel ? [sel.name, sel.version].filter(Boolean).join(' · ') : '—')}</span><span class="lbl">Связь</span><span>${esc(cad.text)}</span></div>
<div class="acts">${button('Проверить связь', 'b b-sec', { cmd: 'cad-ping', disabled: !sel, data: { 'field-cad-session': sel?.key || '' } })}${button('Журнал', 'b-ghost', { cmd: 'cad-log' })}${button('Сохранить диагностику', 'b-ghost', { cmd: 'diagnostics' })}</div>
<div class="lbl" style="font-size: 12.5px">Платформа передаёт графику в выбранный чертёж без перезапуска AutoCAD. Если AutoCAD занят командой или открыт диалог, задание подождёт и повторит попытку.</div>
</section></div>`;
  };

  /* ---- Папки ---------------------------------------------------------------------------------------- */
  const folderIcon = `<span style="width: 40px; height: 40px; flex: 0 0 40px; border-radius: 10px; background: var(--accentSoft); color: var(--accentInk); display: grid; place-items: center"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2h8.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z"></path></svg></span>`;
  const DRIVES = ['Z:', 'Y:', 'X:'];

  PC.pages.folders = state => {
    const info = M.folders(state);
    const legacy = info.legacy;
    let html = '';
    if (info.neighbour && legacy.canTransfer) {
      const names = info.viaLegacy.map(f => f.displayName).join(', ');
      html += `<section class="banner banner-warn">
<div style="flex: 1; min-width: 280px"><div style="font-weight: 750; color: var(--warnInk)">На компьютере работает прежний Structura Connector</div><div style="color: var(--text2)">${esc(names)} открывается через него, поэтому Revit и Tekla видят привычные пути.</div></div>
${button('Перевести компьютер', 'b b-sec', { cmd: 'legacy-transfer' })}</section>`;
    }
    if (legacy.transferred) html += `<section class="banner banner-ok">
<div style="flex: 1; min-width: 280px"><div style="font-weight: 750; color: var(--okInk)">${esc(legacy.status || 'Компьютер переведён на Платформа Коннектор.')}</div><div style="color: var(--text2)">BIM_Models открывается здесь. VPN прежнего коннектора работает — через него идут Tekla MultiUser и SMB.</div></div>
${button('Вернуть прежний коннектор', 'b b-sec', { cmd: 'legacy-restore', disabled: !legacy.canRestore })}</section>`;
    if (!info.list.length) html += card('', `<div style="display: flex; align-items: flex-start; gap: 12px">${folderIcon}<div style="flex: 1"><div style="font-weight: 750; font-size: 15px">Папки ещё не назначены</div><div class="lbl">${esc(state.folderStatus || 'Папки и доступ к ним назначает администратор компании.')}</div></div></div>`, 'padding: 18px');
    else html += `<div style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px">${info.list.map(folder => {
      const drive = state.ui.picks.drives[folder.resourceId] || 'Z:';
      const fields = { 'field-share-resource': folder.resourceId };
      const state3 = folder.drive ? ['Диск ' + folder.drive, 't-ok'] : folder.viaLegacyConnector ? ['Через прежний', 't-warn'] : ['Не подключена', 't-idle'];
      let actions;
      if (folder.drive) actions = button('Открыть', 'b b-pri', { cmd: 'models-open', data: fields })
        + button('Отключить диск', 'b b-sec', { cmd: 'models-unmount', data: { ...fields, 'field-drive': folder.drive } });
      else if (folder.viaLegacyConnector) actions = legacy.canTransfer ? button('Перевести компьютер', 'b b-sec', { cmd: 'legacy-transfer' }) : '';
      else actions = `<select aria-label="Буква диска" class="sel" data-ui="drive" data-id="${esc(folder.resourceId)}">${DRIVES.map(d => `<option${d === drive ? ' selected' : ''}>${d}</option>`).join('')}</select>`
        + button('Подключить диск', 'b b-pri', { cmd: 'models-mount', data: { ...fields, 'field-drive': drive } })
        + button('Открыть', 'b b-sec', { cmd: 'models-open', data: fields });
      const route = folder.viaLegacyConnector ? 'прежний Structura Connector' : 'защищённое соединение NetBird';
      return `<section class="card" style="padding: 18px; display: flex; flex-direction: column; gap: 14px">
<div style="display: flex; align-items: flex-start; gap: 12px">${folderIcon}<div style="flex: 1; min-width: 0"><div style="font-weight: 750; font-size: 15px" class="ellipsis">${esc(folder.displayName)}</div><div class="lbl">Назначена администратором</div></div>${chip(state3[0], state3[1])}</div>
${folder.unc ? `<div class="mono ellipsis" style="padding: 9px 12px; border-radius: 8px; background: var(--field); border: 1px solid var(--line); color: var(--text2)" title="${esc(folder.unc)}">${esc(folder.unc)}</div>` : ''}
<div style="display: grid; grid-template-columns: 90px minmax(0, 1fr); gap: 6px 12px; font-size: 13px"><span class="lbl">Через</span><span>${route}</span>${folder.unc ? `<span class="lbl">Права</span><span>${folder.canWrite ? 'чтение и запись' : 'только чтение'}</span>` : ''}</div>
${actions ? `<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 8px">${actions}</div>` : ''}
</section>`;
    }).join('')}</div>`;
    const conn = M.connection(state);
    const net = state.account;
    const route = net?.route === 'direct' ? 'напрямую' : net?.route === 'relay' ? 'через relay' : '';
    const detail = conn.ready ? [route, net?.gateway ? 'шлюз ' + net.gateway : ''].filter(Boolean).join(', ') || 'подключено' : state.status;
    html += `<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 12px; color: var(--dim)">${dot8(conn.ready ? 'var(--ok)' : 'var(--warn)')}<span style="flex: 1; min-width: 260px">Защищённое соединение NetBird: ${esc(detail)}. Папки назначает администратор компании.</span>${button('Проверить соединение', 'b-ghost', { cmd: 'vpn-check' })}</div>`;
    return html;
  };

  /* ---- Конвертеры ----------------------------------------------------------------------------------- */
  const lastJob = (state, kind) => { const own = M.jobs(state).filter(job => job.kind === kind); return own.find(job => !job.running) || own[0] || null; };
  /** IFC: size before → after from the source and the published result; FBX: the conversion time. */
  const resultFact = job => job.kind === 'ifc'
    ? (job.inputBytes ? `<span class="lbl">Размер</span><span>${esc(size(job.inputBytes) + (job.outputBytes ? ' → ' + size(job.outputBytes) : ''))}</span>` : '')
    : (job.duration && !job.running ? `<span class="lbl">Время</span><span>${esc(job.duration)}</span>` : '');
  const uploadIcon = `<svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" style="color: var(--accentInk)"><path d="M12 15V4M7.5 8.5 12 4l4.5 4.5M4 15v3.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V15"></path></svg>`;

  PC.pages.converters = state => {
    const ifc = state.ui.conv !== 'fbx';
    const kind = ifc ? 'ifc' : 'fbx';
    const job = lastJob(state, kind);
    const running = M.jobs(state).find(j => j.running && j.kind === kind);
    const files = ifc ? state.ifcFiles : state.files;
    const conv = ifc
      ? { listTitle: 'Файлы IFC', tools: [button('Добавить файлы', 'b b-sm b-sec', { cmd: 'pick-input', data: { target: 'ifc-input' } }), button('Очистить завершённые', 'b b-sm b-sec', { cmd: 'ifc-clear-finished', disabled: !files.some(f => /Готово|Ошибка|Отменено/.test(f.status)) })],
          dropTitle: 'Перетащите IFC-файлы сюда', dropHint: 'Или нажмите, чтобы выбрать. До 128 файлов за раз. Исходники не меняются.', pick: 'ifc-input', output: 'ifc-output' }
      : { listTitle: 'Исходные модели', tools: [button('Добавить ZIP', 'b b-sm b-sec', { cmd: 'pick-input', data: { target: 'zip-input' } }), button('Добавить папку', 'b b-sm b-sec', { cmd: 'pick-input', data: { target: 'folder-input' } })],
          dropTitle: 'Перетащите ZIP или папку с FBX', dropHint: 'Или нажмите, чтобы выбрать. Текстуры и сопутствующие файлы берутся из той же папки.', pick: 'zip-input', output: 'output-root' };
    const rows = files.map((file, index) => `<div class="row" style="display: flex; flex-wrap: wrap; align-items: center; gap: 14px; padding: 12px 18px; border-top: 1px solid var(--line)"><span style="flex: 1; min-width: 200px; font-weight: 650" class="ellipsis">${esc(file.name)}</span><span class="lbl" style="min-width: 70px">${esc(size(file.size))}</span>${chip(file.status || 'Выбран', fileTone(file.status), 'align-self: center')}${button('×', '', { cmd: ifc ? 'ifc-remove' : 'files-clear', aria: 'Убрать из списка', disabled: !!running, data: ifc ? { index: String(index) } : {}, style: 'width: 28px; height: 28px; border-radius: 6px; border: 0; background: transparent; color: var(--dim); cursor: pointer; font-size: 16px' })}</div>`).join('');
    const profile = state.ui.picks['ifc-profile'] === 'compact' ? 'compact' : 'exact';
    const radio = (id, title, hint) => `<label class="radio${profile === id ? ' on' : ''}"><input type="radio" name="profile" data-ui="profile" value="${id}"${profile === id ? ' checked' : ''} style="accent-color: var(--accent); margin-top: 3px"><span><span style="display: block; font-weight: 650">${title}</span><span class="lbl">${hint}</span></span></label>`;
    const run = running ? button('Отменить', 'b b-danger', { cmd: 'cancel' })
      : ifc ? button('Оптимизировать', 'b b-pri', { cmd: 'ifc-optimize', disabled: !files.length, data: { 'field-ifc-profile': profile } }) + button('Только анализ', 'b b-sec', { cmd: 'ifc-analyze', disabled: !files.length, data: { 'field-ifc-profile': profile } })
      : button('Конвертировать', 'b b-pri', { cmd: 'start', disabled: !files.length });
    const resultActions = job ? [
      button('Открыть результат', 'b b-sm b-pri', { cmd: 'results', disabled: !job.hasResult, data: { target: job.id } }),
      button(ifc ? 'Отчёт JSON' : 'Отчёт', 'b b-sm b-sec', { cmd: 'report', disabled: !job.hasReport, data: { target: job.id } }),
      ifc ? button('Проверить результат', 'b b-sm b-sec', { cmd: 'ifc-validation', disabled: !job.hasValidation, data: { target: job.id } }) : ''
    ].join('') : '';
    return `<div role="tablist" aria-label="Конвертер" class="seg" style="align-self: flex-start">
<button type="button" role="tab" data-ui="conv" data-id="ifc" aria-selected="${ifc}">Оптимизация IFC</button>
<button type="button" role="tab" data-ui="conv" data-id="fbx" aria-selected="${!ifc}">FBX → GLB</button>
</div>
<div class="split">
<section class="card" style="flex: 3 1 460px; min-width: 0; overflow: hidden">
<div style="display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 14px 18px"><h2 class="h2" style="flex: 1">${conv.listTitle}</h2>${conv.tools.join('')}</div>
<button type="button" data-cmd="pick-input" data-target="${conv.pick}" data-drop="${conv.pick}" class="dropzone" style="margin: 0 18px 14px; width: calc(100% - 36px); padding: 18px; border-radius: 12px; border: 1.5px dashed var(--line2); background: var(--field); display: flex; align-items: center; gap: 14px; color: inherit; text-align: left; cursor: pointer; font: inherit; box-sizing: border-box">
${uploadIcon}<div><div style="font-weight: 700">${conv.dropTitle}</div><div class="lbl">${conv.dropHint}</div></div></button>
${rows}
</section>
<div style="flex: 1 1 300px; min-width: 0; display: flex; flex-direction: column; gap: 16px">
<section class="card" style="padding: 18px; display: flex; flex-direction: column; gap: 12px">
<h2 class="h2">Параметры</h2>
${ifc ? `<div style="display: flex; flex-direction: column; gap: 8px">${radio('exact', 'Точный', 'Без потери данных: только сжатие и очистка дублей')}${radio('compact', 'Компактный', 'Сильнее уменьшает файл, упрощает геометрию')}</div>` : ''}
<div style="display: grid; grid-template-columns: 90px minmax(0, 1fr); gap: 8px 12px; align-items: baseline"><span class="lbl">Результат</span><span style="display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; min-width: 0"><span class="mono" style="overflow-wrap: anywhere">${esc(field(state, conv.output, 'Папка по умолчанию'))}</span>${choose(conv.output)}</span>${ifc ? (state.about?.ifcEngine ? `<span class="lbl">Движок</span><span>IFC ${esc(state.about.ifcEngine)}</span>` : '') : '<span class="lbl">Формат</span><span>GLB + текстуры в ZIP, исходники не меняются</span>'}</div>
<div class="acts">${run}</div>
</section>
<section class="card" style="padding: 18px; display: flex; flex-direction: column; gap: 10px">
<h2 class="h2">Результат</h2>
${job ? `<div style="display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 6px 12px"><span class="lbl">Файл</span><span class="ellipsis">${esc(job.output || job.subject || '—')}</span>${resultFact(job)}<span class="lbl">Состояние</span><span>${esc(job.statusText)}</span></div>
<div class="acts">${resultActions}</div>` : `<div class="lbl" style="font-size: 13px">Результат появится после обработки.</div>`}
</section></div></div>`;
  };

  /* ---- АГР ------------------------------------------------------------------------------------------ */
  const blenderName = state => 'Blender' + (state.agrRuntime?.version ? ' ' + state.agrRuntime.version + ' LTS' : '');
  const AGR = {
    missing: { title: 'Нужен {B}', hint: 'Публикация АГР ждёт установки', icon: ['warnSoft', 'warnInk'],
      actions: [['Установить Blender', 'b-pri', 'agr-install'], ['Проверить готовность', 'b-sec', 'agr-probe']] },
    installing: { title: 'Устанавливаем {B}', hint: 'Можно продолжать работу — установка идёт в фоне', icon: ['accentSoft', 'accentInk'],
      actions: [['Отменить установку', 'b-danger', 'agr-install-cancel']] },
    ready: { title: '{B} готов', hint: 'Публикация АГР доступна', icon: ['okSoft', 'okInk'],
      actions: [['Проверить готовность', 'b-sec', 'agr-probe']] },
    unknown: { title: '{B}', hint: 'Готовность ещё не проверена', icon: ['hover', 'text2'],
      actions: [['Проверить готовность', 'b-pri', 'agr-probe'], ['Установить Blender', 'b-sec', 'agr-install']] }
  };

  /** Download progress in megabytes reported by the installer; afterwards its phase (verification, unpacking, trial run). */
  const installProgress = (state, info) => {
    const p = state.agrInstall;
    const mb = bytes => Math.floor(bytes / (1024 * 1024)).toLocaleString('ru-RU');
    const download = p && p.phase === 'download' && p.total;
    const label = !p ? 'Загрузка и установка' : download ? 'Загрузка архива' : p.phase === 'extract' ? 'Распаковка' : p.phase === 'preflight' ? 'Пробный запуск' : 'Установка';
    const value = download ? `${mb(p.done)} из ${mb(p.total)} МБ` : info.text;
    const bar = download ? `<span class="bar"><span style="width: ${Math.min(100, Math.round(p.done * 100 / p.total))}%"></span></span>` : '<span class="bar indeterminate"><span></span></span>';
    return `<div style="display: flex; flex-direction: column; gap: 6px"><span style="display: flex; justify-content: space-between; gap: 10px"><span>${label}</span><span class="lbl">${esc(value)}</span></span>${bar}<span class="lbl">После загрузки — проверка подлинности, распаковка и пробный запуск.</span></div>`;
  };

  PC.pages.agr = state => {
    const info = M.agr(state);
    const a = AGR[info.state];
    const ready = info.state === 'ready';
    const runtime = state.agrRuntime;
    // Facts of the pinned runtime come from its bundled manifest (snapshot agrRuntime); without it the rows are hidden.
    const facts = runtime ? `<span class="lbl">Источник</span><span>${esc(runtime.sourceHost)}, официальный архив</span><span class="lbl">Размер</span><span>${esc(size(runtime.sizeBytes))} · подлинность проверяется перед установкой</span><span class="lbl">Куда</span><span class="mono">%LOCALAPPDATA%\\Platform\\Runtimes\\Blender\\${esc(runtime.version)}</span>` : '';
    return `<section class="card" style="display: flex; flex-wrap: wrap; gap: 24px; padding: 22px; align-items: flex-start">
<div style="flex: 2 1 380px; display: flex; flex-direction: column; gap: 14px">
<div style="display: flex; align-items: center; gap: 14px"><span style="width: 44px; height: 44px; border-radius: 12px; display: grid; place-items: center; font-weight: 800; font-size: 18px; background: var(--${a.icon[0]}); color: var(--${a.icon[1]});">B</span><div><div style="font-size: 17px; font-weight: 800">${esc(a.title.replace('{B}', blenderName(state)))}</div><div class="lbl" style="font-size: 13px">${esc(a.hint)}</div></div></div>
${info.state === 'installing' ? installProgress(state, info) : ''}
<div style="display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 8px 14px"><span class="lbl">Состояние</span><span>${esc(info.text)}</span>${facts}</div>
<div class="acts">${a.actions.map(([label, cls, cmd]) => button(label, 'b ' + cls, { cmd })).join('')}</div>
</div>
<div style="flex: 1 1 220px; display: flex; flex-direction: column; gap: 10px; padding: 16px; border-radius: 10px; background: var(--field); border: 1px solid var(--line)">
<span class="lbl">Публикация из Платформы</span>
${['НПМ', 'ВПМ', 'FBX'].map(name => `<span style="display: flex; justify-content: space-between; align-items: center"><span>${name}</span>${chip(ready ? 'готово' : 'ждёт Blender', ready ? 't-ok' : 't-idle', 'align-self: center')}</span>`).join('')}
${button('Открыть Платформу', 'linklike', { cmd: 'platform-open' })}
</div></section>
<div class="acts">${button('Журнал АГР', 'b-ghost', { cmd: 'agr-log' })}${button('Сохранить диагностику', 'b-ghost', { cmd: 'diagnostics' })}</div>`;
  };
})();

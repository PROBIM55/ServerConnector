(() => {
  const $=s=>document.querySelector(s), q=new URLSearchParams(location.search);
  const desktop=window.CONNECTOR_DESKTOP===true;
  const state={mode:q.get('mode')==='platform'?'platform':'structura',page:q.get('page')||'overview',last:{structura:'overview',platform:'overview'},tekla:q.get('sub')||'standard',conv:q.get('sub')||'catalog',job:null,progress:0,notices:{},fields:{},files:[],ifcFiles:[],brand:window.CONNECTOR_BRAND_CONCEPTS.some(concept=>concept.key===q.get("logo"))?q.get("logo"):window.CONNECTOR_BRAND_CONCEPTS[0].key,running:false,installingBlender:false,updateChecked:false,exports:[],exportEditor:false,editingExport:null,modelFolders:[],history:[]};
  const tokens=window.CONNECTOR_KIT.graphite;
  Object.entries(tokens).forEach(([key,value])=>{if(key!=='name')document.body.style.setProperty('--'+key.replace(/[A-Z]/g,letter=>'-'+letter.toLowerCase()),typeof value==='number'?value+'px':value);});
  const iconNames={overview:'layout-dashboard',services:'network',tekla:'boxes',attributes:'tags',folders:'folder-open',converters:'arrow-right-left',agr:'cpu',bridge:'plug',autocad:'drafting-compass'};
  const icon=Object.fromEntries(Object.entries(iconNames).map(([key,name])=>[key,window.CONNECTOR_ICONS[name]]));
  const nav={structura:[['overview','Обзор'],['services','Сервисы'],['tekla','Tekla'],['attributes','Атрибуты'],['folders','Общие папки'],['converters','Конвертеры']],platform:[['overview','Обзор'],['agr','АГР'],['bridge','Tekla'],['autocad','AutoCAD']]};
  const button=(t,c='secondary',a='')=>`<button type="button" class="${c}" ${a}>${t}</button>`;
  const escape=value=>String(value).replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  const appVersion=()=>desktop?(typeof state.moduleFields?.['app-version']==='string'&&state.moduleFields['app-version'].trim()?escape(state.moduleFields['app-version']):'—'):'1.0.31';
  const fieldKey=(id,mode=state.mode)=>`${['token','channel','autostart','heartbeat-toggle','heartbeat-seconds','poll-seconds','backoff-seconds'].includes(id)?'common':mode}.${id}`;
  const notice=id=>state.notices[id]?`<div class="notice">${escape(state.notices[id])}</div>`:'';
  const connection=()=>state.connections?.[state.mode]??{status:state.connectionStatus,hasSavedCredential:state.hasSavedCredential};
  const row=(title,detail,actions='')=>{
    if(desktop){
      const key=state.page==='agr'&&['Blender','Готовность к работе'].includes(title)?'agr-status':state.page==='bridge'&&title==='Текущая модель'?'bridge-status':state.page==='autocad'&&title==='AutoCAD'?'cad-status':state.page==='tekla'&&state.tekla==='ifc'&&title==='Установленный патч'?'ifc-patch-status':state.page==='tekla'&&state.tekla==='sharing'&&title==='Совместная работа'?'sharing-status':title==='VPN'||title==='Защищённое соединение'?'vpn-status':title==='Работа в фоне'?'agent-status':title==='Установленная версия'?'app-version':null;
      if(key==='app-version')detail=appVersion();
      else if(key&&state.moduleFields?.[key])detail=escape(state.moduleFields[key]);
      else if(title==='Доступ к модулям'&&Array.isArray(state.allowedPages?.[state.mode]))detail=escape(nav[state.mode].filter(([id])=>state.allowedPages[state.mode].includes(id)).map(([,label])=>label).join(', ')||'Нет доступных модулей');
      else if(title==='Blender')detail='Не проверено';
    }
    return `<div class="row"><div><strong>${title}</strong><small>${detail}</small></div><div class="actions">${actions}</div></div>`;
  };
  const field=(id,label,value,extra='')=>`<div class="field"><label for="${id}">${label}</label><input id="${id}" value="${escape(state.fields[fieldKey(id)]??value)}">${extra}</div>`;
  const select=(id,label,options)=>`<div class="field"><label for="${id}">${label}</label><select id="${id}">${options.map(x=>`<option ${state.fields[fieldKey(id)]===x?'selected':''}>${escape(x)}</option>`).join('')}</select></div>`;
  function kit(){return `<div class="page grid"><section class="panel"><p class="eyebrow">Типографика</p><h2>Segoe UI Variable Text</h2><h1>28 / Заголовок</h1><h3>15 / Подзаголовок</h3><p>14 / Основной текст · 11 / Метаданные</p></section><section class="panel"><p class="eyebrow">Формы</p>${field('field-ok','Поле','Сохранится при обновлении задания','<small>Обычное состояние.</small>')}${select('kit-select','Список',['Выберите значение','Второе значение'])}<div class="field error"><label for="field-error">Ошибка</label><input id="field-error" aria-invalid="true"><small>Укажите значение.</small></div></section><section class="panel"><p class="eyebrow">Управление</p><div class="actions">${button('Основное','primary')}${button('Вторичное')}${button('Недоступно','secondary','disabled')}${button('Отменить','danger')}${button('Диалог','secondary','data-act="dialog"')}</div><div class="tabs"><button class="active">Вкладка</button><button>Вкладка</button></div><p>Spacing 4 / 8 / 12 / 18 · Radius 3</p></section><section class="panel"><p class="eyebrow">Таблица</p><table class="table"><tbody><tr><th>Статус</th><td>Не проверено</td></tr><tr><th>Загрузка</th><td>Сценарий</td></tr></tbody></table></section></div>`;}
  function kitFoundation(){return `<section class="kit-foundation"><div><p class="eyebrow">Graphite · семантические цвета</p><div class="swatches">${[['Фон','bg'],['Поверхность','surface'],['Текст','text'],['Акцент','accent'],['Успех','success'],['Ошибка','danger']].map(([label,key])=>`<div><i style="background:${tokens[key]}"></i><strong>${label}</strong><small>${tokens[key]}</small></div>`).join('')}</div></div><div><p class="eyebrow">Lucide · единая сетка 20 px</p><div class="icon-gallery">${Object.entries(icon).map(([id,svg])=>`<span title="${id}" aria-label="${id}">${svg}</span>`).join('')}</div><div class="state-samples"><span class="success">Готово</span><span class="warning">Ожидание</span><span class="danger-text">Ошибка</span><span class="spinner" aria-label="Загрузка"></span></div></div></section>`;}
  const pages={...window.CONNECTOR_REVIEW_PAGES({state,button,escape,field,select,notice,row,fieldKey,appVersion,runtime:desktop}),kit:()=>kitFoundation()+kit()};
  function rememberFields(){document.querySelectorAll('#page-content input[id],#page-content select[id]').forEach(input=>{if(input.type!=='file'&&(!desktop||input.id!=='token'))state.fields[input.dataset.fieldKey||fieldKey(input.id)]=input.type==='checkbox'?input.checked:input.value;});}
  function desktopPayload(element){
    rememberFields();
    const fields={};
    document.querySelectorAll('#page-content input[id],#page-content select[id],#kit-dialog input[id],#kit-dialog select[id]').forEach(input=>{
      if(input.type==='file')return;
      fields[input.id]=input.type==='checkbox'?input.checked:input.value;
    });
    return {mode:state.mode,page:state.page,converter:state.conv,tekla:state.tekla,fields,lists:{files:state.files.map(file=>({name:String(file.name),size:Number(file.size)||0})),ifcFiles:state.ifcFiles.map(file=>({name:String(file.name),size:Number(file.size)||0,status:String(file.status||'')})),modelFolders:state.modelFolders.map(String)},target:element?.dataset?.target??(['results','report','ifc-validation'].includes(element?.dataset?.act)?state.job?.id:undefined),index:element?.dataset?.index};
  }
  function desktopAction(id,element){
    const payload=desktopPayload(element);
    const command=id.startsWith('pick-')?'pick-input':id;
    if(command==='pick-input')payload.target=element?.dataset?.target||id.replace('pick-','')+'-input';
    window.CONNECTOR_DESKTOP_BRIDGE.invoke(command,payload).finally(()=>{
      if(id==='connect'){
        const token=$('#token');
        if(token)token.value='';
        delete state.fields['common.token'];
      }
    });
  }
  function updateJobDisplay(){
    const active=state.job?.status==='Выполняется';
    $('#job-count').textContent=active?'1':'0';
    $('#active-job').textContent=active&&state.job.mode!==state.mode?`Задание выполняется в ${state.job.mode==='structura'?'Structura':'Платформе'}`:'';
    document.querySelectorAll('[data-job-progress]').forEach(element=>element.textContent=state.progress+'%');
    document.querySelectorAll('[data-job-bar]').forEach(element=>element.style.width=state.progress+'%');
    document.querySelectorAll('[data-job-status]').forEach(element=>element.textContent=state.job?.status??'—');
  }
  function render(captureFields=true){
    const transientToken=desktop&&!captureFields?document.getElementById('token')?.value:null;
    const focused=document.activeElement;
    const focusedId=focused?.id,focusedAction=focused?.dataset?.act;
    const selection=typeof focused?.selectionStart==='number'?[focused.selectionStart,focused.selectionEnd]:null;
    if(captureFields)rememberFields();
    if(!pages[state.page])state.page='overview';
    if(Array.isArray(state.allowedPages?.[state.mode])&&!['overview','jobs','settings'].includes(state.page)&&!state.allowedPages[state.mode].includes(state.page))state.page='overview';
    if(!nav[state.mode].some(x=>x[0]===state.page)&&!['jobs','settings','kit','brand','admin'].includes(state.page))state.page='overview';
    $('#navigation').innerHTML=nav[state.mode].map(([id,label])=>`<button type="button" class="nav-link ${state.page===id?'active':''}" data-page="${id}"><span class="nav-icon" aria-hidden="true">${icon[id]}</span>${label}</button>${id==='converters'&&state.page==='converters'?`<div class="submodule-nav">${[['fbx','FBX → GLB'],['ifc','Оптимизация IFC']].map(([module,title])=>`<button type="button" data-conv="${module}" class="${state.conv===module?'active':''}">${title}</button>`).join('')}</div>`:''}`).join('');
    document.querySelectorAll('[data-mode]').forEach(button=>{button.classList.toggle('active',button.dataset.mode===state.mode);button.setAttribute('aria-pressed',String(button.dataset.mode===state.mode));});
    if(Array.isArray(state.allowedPages?.[state.mode]))document.querySelectorAll('#navigation [data-page]').forEach(button=>{if(button.dataset.page!=='overview'&&!state.allowedPages[state.mode].includes(button.dataset.page))button.remove();});
    $('#crumb').textContent=state.mode==='structura'?'STRUCTURA':'ПЛАТФОРМА';
    $('#page-title').textContent=({overview:'Обзор',kit:'UI-kit',jobs:'Задания',settings:'Настройки'})[state.page]||(nav[state.mode].find(x=>x[0]===state.page)||[,'Обзор'])[1];
    if(state.page==='brand')$('#page-title').textContent='Знаки и логотипы';
    if(state.page==='admin')$('#page-title').textContent='Управление в Платформе';
    if(state.page==='converters')$('#page-title').textContent=({catalog:'Конвертеры',fbx:'FBX → GLB',ifc:'Оптимизация IFC',history:'История конвертаций'})[state.conv]||'Конвертеры';
    const brandConcept=window.CONNECTOR_BRAND_CONCEPTS.find(concept=>concept.key===state.brand)||window.CONNECTOR_BRAND_CONCEPTS[0];
    const brandAsset=brandConcept.asset;
    const platformMark=document.querySelector('[data-mode="platform"] img');
    platformMark.src='brand/'+brandAsset;
    platformMark.classList.toggle('mono-mark',!!brandConcept.monochrome);
    $('#app-favicon').href=state.mode==='structura'?'brand/structura-logo.png':'brand/'+brandAsset;
    document.title=(state.mode==='structura'?'Structura':'Платформа')+' · '+$('#page-title').textContent+' · Connector';
    $('#page-content').innerHTML=pages[state.page]();
    if(desktop&&document.getElementById('cad-session')){
      const sessions=Array.isArray(state.cadSessions)?state.cadSessions:[];
      const selected=state.fields[fieldKey('cad-session')];
      $('#cad-session').innerHTML=sessions.length?sessions.map(session=>`<option value="${escape(session.sessionKey)}" ${selected===session.sessionKey||(!selected&&session.isSelected)?'selected':''}>${escape(session.displayName)}</option>`).join(''):'<option value="">Нет найденных сеансов</option>';
    }
    if(desktop)document.querySelectorAll('#page-content input[id]').forEach(input=>{
      if(document.querySelector(`[data-act="choose-folder"][data-target="${CSS.escape(input.id)}"]`)){
        input.readOnly=true;input.setAttribute('aria-description','Папка выбирается кнопкой рядом с полем.');
      }
    });
    document.querySelectorAll('#page-content input[id],#page-content select[id]').forEach(input=>{input.dataset.fieldKey=fieldKey(input.id);const saved=state.fields[input.dataset.fieldKey];if(saved!==undefined&&input.type!=='file'){if(input.type==='checkbox')input.checked=saved;else input.value=saved;}});
    if(desktop&&document.getElementById('token')){
      const input=$('#token');
      if(transientToken)input.value=transientToken;
      input.placeholder=connection().hasSavedCredential?'Токен сохранён':'Выдаёт администратор';
      $('#token-help').textContent=connection().hasSavedCredential?'Сохранённый токен используется автоматически. Для замены введите новый.':'Токен выдаёт администратор. После подключения он хранится защищённо.';
      document.querySelector('[data-act="show-token"]').disabled=!input.value;
    }
    updateJobDisplay();
    if(desktop&&connection().status)$('#connection-status').textContent=connection().status;
    if(desktop&&state.job){
      document.querySelectorAll('[data-act="results"]').forEach(control=>control.disabled=!state.job.hasResult);
      document.querySelectorAll('[data-act="report"]').forEach(control=>{if(!control.dataset.target)control.disabled=!state.job.hasReport;});
      document.querySelectorAll('[data-act="ifc-validation"]').forEach(control=>control.disabled=!state.job.hasValidation);
    }
    if(desktop&&state.availability){
      document.querySelectorAll('[data-act="agent-start"],[data-act="agent-stop"]').forEach(control=>control.hidden=state.availability.agentControls!==true);
      document.querySelectorAll('[data-act="publish-start"],[data-act="publish-validate"]').forEach(control=>{control.hidden=state.availability.publishTekla!==true;control.disabled=state.availability.publishTekla!==true;});
    }
    if(Array.isArray(state.allowedPages?.[state.mode]))document.querySelectorAll('#page-content [data-page]').forEach(button=>{if(!['overview','jobs','settings'].includes(button.dataset.page)&&!state.allowedPages[state.mode].includes(button.dataset.page))button.remove();});
    if(desktop)document.querySelectorAll('[data-act="log-folder"]').forEach(control=>{control.disabled=true;control.title='Журнал доступен кнопкой «Открыть журнал».';});
    const params=new URLSearchParams();
    if(state.mode!=='structura')params.set('mode',state.mode);
    if(state.page!=='overview')params.set('page',state.page);
    if(state.page==='tekla'&&state.tekla!=='standard')params.set('sub',state.tekla);
    if(state.page==='converters'&&state.conv!=='catalog')params.set('sub',state.conv);
    if(state.brand!==window.CONNECTOR_BRAND_CONCEPTS[0].key)params.set('logo',state.brand);
    history.replaceState(null,'',params.size?'?'+params:location.pathname);
    const target=focusedId?document.getElementById(focusedId):focusedAction?document.querySelector(`[data-act="${focusedAction}"]`):null;
    if(target&&!target.disabled){target.focus({preventScroll:true});if(selection&&typeof target.setSelectionRange==='function'&&['text','password'].includes(target.type))target.setSelectionRange(...selection);}
  }
  function showDialog(title,html){$('#dialog-title').textContent=title;$('#dialog-content').innerHTML=html;$('#kit-dialog').showModal();}
  function action(id,element){
    rememberFields();
    if(desktop&&!['show-token','dialog','release-notes'].includes(id)){
      if(id==='connect'&&!$('#token').value.trim()){
        if(connection().hasSavedCredential){desktopAction('reconnect',element);return;}
        $('#token-help').textContent='Введите токен устройства.';$('#token').setAttribute('aria-invalid','true');$('#token').focus();return;
      }
      desktopAction(id,element);return;
    }
    if(['pick-zip','pick-folder','pick-ifc'].includes(id)){document.getElementById({'pick-zip':'zip-input','pick-folder':'folder-input','pick-ifc':'ifc-input'}[id]).click();return;}
    if(id==='choose-folder'){state.folderTarget=element.dataset.target;showDialog('Выберите папку',`<p>В приложении откроется системный выбор папки. Для просмотра можно указать путь.</p><div class="field"><label for="chosen-path">Папка</label><input id="chosen-path" placeholder="C:/Проекты/Модели"></div>${button('Использовать папку','primary','data-act="choose-folder-save"')}`);return;}
    if(id==='choose-folder-save'){const path=$('#chosen-path').value.trim();if(!path){$('#chosen-path').focus();return;}state.fields[fieldKey(state.folderTarget)]=path;$('#kit-dialog').close();render(false);return;}
    if(id==='files-clear'){state.files=[];render(false);return;}
    if(id==='ifc-remove'){state.ifcFiles.splice(Number(element.dataset.index),1);render(false);return;}
    if(id==='ifc-clear-finished'){state.ifcFiles=state.ifcFiles.filter(file=>file.status!=='Готово');render(false);return;}
    if(id==='history-clear'){if(state.job?.status!=='Выполняется'){state.history=[];state.job=null;state.progress=0;}render(false);return;}
    if(id==='exports-create'){Object.keys(state.fields).filter(key=>key.startsWith(state.mode+'.export-')||key.startsWith(state.mode+'.day-')).forEach(key=>delete state.fields[key]);state.editingExport=null;state.exportEditor=true;state.modelFolders=[];render(false);return;}
    if(id==='exports-edit'){const item=state.exports[Number(element.dataset.index)];state.editingExport=Number(element.dataset.index);state.exportEditor=true;state.modelFolders=[...item.extraFolders];Object.assign(state.fields,item.fields);render(false);return;}
    if(id==='exports-close'){state.exportEditor=false;render(false);return;}
    if(id==='export-add-folder'){state.modelFolders.push('');render(false);return;}
    if(id==='export-remove-folder'){const index=Number(element.dataset.index);const values=state.modelFolders.map((folder,i)=>state.fields[fieldKey('export-model-'+i)]??folder);values.splice(index,1);state.modelFolders=values;Object.keys(state.fields).filter(key=>key.startsWith(state.mode+'.export-model-')&&/\d+$/.test(key)).forEach(key=>delete state.fields[key]);values.forEach((folder,i)=>state.fields[fieldKey('export-model-'+i)]=folder);render(false);return;}
    if(id==='exports-save'){
      const get=name=>state.fields[fieldKey(name)];
      const required=['export-name','export-model-folder'];if(get('export-destination')!=='structura')required.push('export-output');
      const missing=required.find(name=>!String(get(name)??'').trim());if(missing){$('#export-error').textContent='Заполните название, папку моделей и папку результата.';document.getElementById(missing).setAttribute('aria-invalid','true');document.getElementById(missing).focus();return;}
      const destination=get('export-destination')||'folder',draft=destination!=='folder';
      if(get('export-frequency')==='weekly'&&!['mo','tu','we','th','fr','sa','su'].some(day=>get('day-'+day))){$('#export-error').textContent='Выберите хотя бы один день недели.';return;}
      const frequency=get('export-frequency');const schedule=frequency==='manual'?'Вручную':(frequency==='weekly'?'По дням недели':'Каждый день')+' · '+(get('export-time')||'02:00');
      const item={name:get('export-name'),folder:get('export-model-folder'),destinationLabel:({folder:'IFC в папку',structura:'Модели в Structura',both:'IFC в папку и модели в Structura'})[destination]+(draft?' · проект не выбран':''),draft,schedule,enabled:!draft&&get('export-enabled')!==false,extraFolders:[...state.modelFolders],fields:Object.fromEntries(Object.entries(state.fields).filter(([key])=>key.startsWith(state.mode+'.export-')||key.startsWith(state.mode+'.day-')))};
      if(state.editingExport===null)state.exports.push(item);else state.exports[state.editingExport]=item;state.exportEditor=false;render(false);return;
    }
    if(id==='exports-toggle'){const item=state.exports[Number(element.dataset.index)];if(item.draft){showDialog('Выберите проект',row('Настройка', 'Подключите устройство и выберите доступный проект Structura перед включением выгрузки.'));return;}item.enabled=!item.enabled;item.fields[fieldKey('export-enabled')]=item.enabled;render(false);return;}
    if(id==='exports-run'||id==='exports-history'){const item=state.exports[Number(element.dataset.index)];showDialog(id==='exports-run'?'Запуск выгрузки':'История выгрузки',`${row('Настройка',escape(item.name))}${row('Папка моделей',escape(item.folder))}${row('Результат',escape(item.destinationLabel))}${row('Состояние','Механизм ещё не подключён. Реальная выгрузка не запускалась.')}`);return;}
    if(id==='ifc-optimize'){if(!state.ifcFiles.length){state.notices.ifc='Добавьте IFC-файлы перед обработкой.';render(false);return;}start('ifc');return;}
    if(id==='connect'){if(!$('#token').value.trim()){$('#token-help').textContent='Введите токен устройства.';$('#token').setAttribute('aria-invalid','true');$('#token').focus();return;}state.fields[fieldKey('token')]='';$('#token').value='';showDialog('Подключение устройства',`${row('Шаг 1','Проверка одноразового токена')}${row('Шаг 2','Настройка защищённого соединения')}${row('Шаг 3','Получение доступных модулей и папок')}${row('Просмотр','Токен не отправлен. Устройство не зарегистрировано.')}`);return;}
    if(id==='disconnect'||id==='reconnect'){state.notices.connect=id==='disconnect'?'Сценарий отключения выбран. Рабочие подключения не изменялись.':'Сценарий повторного подключения выбран. Рабочие подключения не изменялись.';render(false);return;}
    if(id==='agent-start'||id==='agent-stop'){state.running=id==='agent-start';state.notices.agent=state.running?'Фоновая работа включена в сценарии просмотра.':'Фоновая работа приостановлена в сценарии просмотра.';render(false);return;}
    if(id==='agr-install'||id==='agr-install-cancel'){state.installingBlender=id==='agr-install';state.notices.agr=state.installingBlender?'Показан сценарий установки Blender. Файлы не загружаются.':'Сценарий установки отменён.';render(false);return;}
    if(id==='update-check'){state.updateChecked=true;state.notices.update='Рабочий сервер обновлений в макете не запрашивается. Ниже — действия для новой версии.';render(false);return;}
    if(id==='results'||id==='report'){const job=element?.dataset.jobId?state.history.find(item=>String(item.id)===element.dataset.jobId):state.job;showDialog(id==='report'?'Отчёт обработки':'Результат',`${row('Операция',job?.kind==='ifc'?'Оптимизация IFC':'FBX → GLB/ZIP')}${row('Состояние',job?.status??'—')}${row('Файл',escape(job?.output??'—'))}${row('Рабочая среда',job?.mode==='structura'?'Structura':'Платформа')}${row('Просмотр','Файл не создавался. Показан сценарий результата.')}`);return;}
    if(/^(speckle|cloud)-(open|access)$/.test(id)){const service=id.startsWith('speckle')?'Speckle':'Nextcloud';showDialog(service,`${row('Учётная запись','Будет выдана при подключении')}${row('Мой доступ','Права и адрес появятся после регистрации')}${row('Состояние','Сервер в макете не запрашивается.')}`);return;}
    if(id==='ifc-analyze'||id==='ifc-validation'){if(!state.ifcFiles.length){state.notices.ifc='Добавьте IFC-файлы перед анализом.';render(false);return;}showDialog(id==='ifc-analyze'?'Анализ IFC':'Проверка результата',`${row('Файлы',state.ifcFiles.map(file=>escape(file.name)).join(', '))}${row('Профиль',escape($('#ifc-profile').value))}${row('Проверки','GUID, свойства, материалы, координаты, геометрия')}${row('Размер, объекты и геометрия','Результаты появятся после реальной обработки.')}`);return;}
    if(id==='ifc-apply'||id==='ifc-rollback'||id==='sharing-config'){showDialog({'ifc-apply':'Установка патча IFC-экспорта','ifc-rollback':'Восстановление исходных файлов','sharing-config':'Настройка Model Sharing'}[id],`${row('Tekla Structures','2025')}${row('Перед выполнением','Закройте Tekla и сохраните работу.')}${row('Операция',id==='ifc-apply'?'Проверка версии, резервная копия и установка исправления':id==='ifc-rollback'?'Восстановление файлов из резервной копии':'Подготовка установленной Tekla для Model Sharing')}${row('Просмотр','Системные файлы не изменяются.')}`);return;}
    if(id==='diagnostics'){showDialog('Диагностика для поддержки',`${row('Состав','Версия приложения, состояние соединений, журнал ошибок')}${row('Исключено','Токены, пароли и содержимое моделей')}${row('Просмотр','Диагностический файл не создаётся.')}`);return;}
    if(id==='release-notes'){showDialog('Что нового',`${row('Версия текущего приложения',appVersion())}${row('Примечания к выпуску','Будут получены с подписанным пакетом обновления')}`);return;}
    if(id==='admin-users'||id==='admin-modules'||id==='server-admin'){showDialog('Администрирование в Платформе',`${row('Управление','Пользователи, устройства, модули, общие папки и публикации')}${row('Доступ','Только по правам администратора, проверяемым сервером')}${row('Операции','Одноразовый токен, отзыв устройства, назначение модулей/папок')}${row('Состояние','Перенос административного интерфейса — отдельный пакет плана.')}`);return;}
    if(id==='publish-start'||id==='publish-validate'){state.notices.publish='Публикация доступна только через подключённое native-приложение с ролью администратора.';render(false);return;}
    if(/-log$/.test(id)||['log-folder','log-clear'].includes(id)){showDialog('Журнал приложения',`${row('Раздел',escape(state.page))}${row('Журнал','Появится после подключения рабочего приложения.')}${row('Просмотр','Журналы устройства не читались и не изменялись.')}`);return;}
    const groups={xs:'tekla',ext:'tekla',lib:'tekla',sharing:'tekla',firm:'tekla',settings:'settings',update:'update'};
    let group=groups[id.split('-')[0]]||id.split('-')[0];if(['ifc-detect'].includes(id))group='tekla';
    const messages={'agr-probe':'Blender 5.2.0 установлен. Готовность к публикации необходимо проверить в рабочем приложении.','bridge-start':'Для подключения откройте Tekla и модель. Рабочий процесс в макете не запускался.','bridge-check':'Связь с моделью Tekla пока не проверена.','bridge-restart':'Восстановление связи показано как сценарий. Рабочие процессы не перезапускались.','cad-refresh':'Список сеансов будет получен от локального коннектора AutoCAD.','cad-ping':'Для проверки нужен подключённый сеанс AutoCAD.','vpn-enable':'Защищённое соединение включится после регистрации устройства.','vpn-disable':'Сценарий отключения VPN. Рабочее соединение не менялось.','vpn-check':'Защищённое соединение ещё не проверено.','models-open':'Папки проекта появятся после выдачи доступа.','models-mount':'Диск будет подключён по правам и папке, назначенным администратором.','models-unmount':'Сценарий отключения диска выбран. Рабочие диски не менялись.','platform-open':'Откроется проект Платформы, выбранный при подключении.','studio-open':'Пакет результата передаётся в Structura Studio. В макете файл не создавался.','settings-store':'Настройки сохранены только в текущем просмотре.','settings-save':'Настройки изменены в просмотре; рабочее приложение не переподключалось.','update-download':'Сценарий загрузки подписанного пакета. Файлы не загружались.','update-apply':'Перезапуск начнётся после проверки пакета и завершения заданий.'};
    state.notices[group]=messages[id]||'В рабочем приложении действие выполнится после проверки подключения и доступа.';render(false);
  }
  let timer;
  function start(kind=state.page==='converters'&&['fbx','ifc'].includes(state.conv)?state.conv:state.job?.kind??'fbx'){
    if(desktop){desktopAction('start',document.querySelector('[data-act="start"]'));return;}
    if(state.job?.status==='Выполняется')return;
    rememberFields();
    state.job={id:Date.now(),mode:state.mode,kind,status:'Выполняется',output:kind==='ifc'?'model.optimized.ifc':'SM_TestPart_001.glb.zip'};
    state.history.push(state.job);
    if(kind==='ifc')state.ifcFiles.forEach(file=>file.status='Выполняется');
    state.progress=0;render();
    document.querySelector('[data-act="cancel"]')?.focus({preventScroll:true});
    timer=setInterval(()=>{
      state.progress=Math.min(100,state.progress+20);
      if(state.progress===100){clearInterval(timer);state.job.status='Готово';if(state.job.kind==='ifc')state.ifcFiles.forEach(file=>file.status='Готово');if(state.page==='converters'||state.page==='jobs'){const wasCancel=document.activeElement?.dataset?.act==='cancel';render();if(wasCancel)document.querySelector('[data-act="report"]')?.focus({preventScroll:true});}else updateJobDisplay();}
      else updateJobDisplay();
    },400);
  }
  function cancel(){if(desktop){desktopAction('cancel',document.querySelector('[data-act="cancel"]'));return;}if(state.job?.status!=='Выполняется')return;clearInterval(timer);state.job.status='Отменено';if(state.job.kind==='ifc')state.ifcFiles.forEach(file=>file.status='Отменено');render();document.querySelector('[data-act="start"]')?.focus({preventScroll:true});}
  document.addEventListener('input',event=>{const input=event.target;if(desktop&&input.id==='token'){document.querySelector('[data-act="show-token"]').disabled=!input.value;return;}if(input.id&&input.type!=='file')state.fields[input.dataset.fieldKey||fieldKey(input.id)]=input.type==='checkbox'?input.checked:input.value;});
  document.addEventListener('change',event=>{if(['zip-input','folder-input','ifc-input'].includes(event.target.id)){const files=[...event.target.files].map(file=>({name:file.webkitRelativePath||file.name,size:file.size}));if(event.target.id==='ifc-input')state.ifcFiles.push(...files);else state.files.push(...files);render();}});
  document.addEventListener('submit',event=>{if(event.target.id==='connection-form'){event.preventDefault();action('connect');}if(event.target.id==='export-form'){event.preventDefault();action('exports-save');}});
  document.addEventListener('click',event=>{
    const page=event.target.closest('[data-page]'),mode=event.target.closest('[data-mode]'),tab=event.target.closest('[data-tab]'),conv=event.target.closest('[data-conv]'),act=event.target.closest('[data-act]'),brand=event.target.closest('[data-brand]');
    if(brand){state.brand=brand.dataset.brand;render();return;}
    if(page){rememberFields();state.page=page.dataset.page;if(nav[state.mode].some(x=>x[0]===state.page))state.last[state.mode]=state.page;render();return;}
    if(mode){rememberFields();state.mode=mode.dataset.mode;state.page=state.last[state.mode];render();return;}
    if(tab){state.tekla=tab.dataset.tab;render();return;}
    if(conv){rememberFields();state.page='converters';state.conv=conv.dataset.conv;state.last[state.mode]='converters';render();return;}
    if(act){if(act.dataset.act==='start')start();else if(act.dataset.act==='cancel')cancel();else if(act.dataset.act==='show-token'){const input=$('#token');input.type=input.type==='password'?'text':'password';act.textContent=input.type==='password'?'Показать':'Скрыть';}else if(act.dataset.act==='dialog')showDialog('Пример диалога','<p>Текст диалога и одно основное действие.</p>');else action(act.dataset.act,act);}
  });
  document.addEventListener('change',event=>{
    if(['export-source','export-destination','export-frequency','ifc-profile','share-resource'].includes(event.target.id)){
      rememberFields();
      if(event.target.id==='share-resource'){
        const folder=state.assignedFolders?.find(item=>item.resourceId===event.target.value);
        state.fields[fieldKey('drive')]=folder?.drive||'Z:';
      }
      render(false);
    }
  });
  window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE=(command,error)=>{
    const group=command==='connect'||command==='reconnect'?'connect':command==='start'||command==='cancel'||command==='ifc-optimize'?'ifc':command.startsWith('sharing-')?'tekla':command.split('-')[0];
    state.notices[group]=String(error||'Действие пока недоступно.');
    render(false);
    if(!document.querySelector('.notice'))$('#active-job').textContent=state.notices[group];
  };
  window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT=snapshot=>{
    if(!snapshot||typeof snapshot!=='object')return;
    if(snapshot.reset===true){state.files=[];state.ifcFiles=[];state.history=[];state.exports=[];state.modelFolders=[];state.job=null;state.progress=0;state.notices={};}
    if(snapshot.navigate===true){
      if(snapshot.mode==='platform'||snapshot.mode==='structura')state.mode=snapshot.mode;
      if(typeof snapshot.page==='string')state.page=snapshot.page;
      if(typeof snapshot.converter==='string')state.conv=snapshot.converter;
      if(typeof snapshot.tekla==='string')state.tekla=snapshot.tekla;
    }
    if(snapshot.moduleFields&&typeof snapshot.moduleFields==='object')state.moduleFields={...snapshot.moduleFields};
    if(snapshot.availability&&typeof snapshot.availability==='object')state.availability={agentControls:snapshot.availability.agentControls===true,publishTekla:snapshot.availability.publishTekla===true};
    if(snapshot.allowedPages&&typeof snapshot.allowedPages==='object')state.allowedPages=Object.fromEntries(['structura','platform'].map(mode=>[mode,Array.isArray(snapshot.allowedPages[mode])?snapshot.allowedPages[mode].filter(page=>typeof page==='string'):[]]));
    else if(snapshot.allowedPages===null)state.allowedPages=null;
    if(typeof snapshot.hasSavedCredential==='boolean')state.hasSavedCredential=snapshot.hasSavedCredential;
    if(snapshot.connections&&typeof snapshot.connections==='object'){
      state.connections={};
      for(const mode of ['structura','platform']){const item=snapshot.connections[mode];if(item&&typeof item.status==='string')state.connections[mode]={status:item.status,hasSavedCredential:item.hasSavedCredential===true};}
    }
    if(Array.isArray(snapshot.cadSessions))state.cadSessions=snapshot.cadSessions.filter(item=>item&&typeof item.sessionKey==='string').map(item=>({sessionKey:item.sessionKey,displayName:String(item.displayName??'AutoCAD'),isSelected:item.isSelected===true}));
    const snapshotFields={...(snapshot.fields&&typeof snapshot.fields==='object'?snapshot.fields:{})};
    Object.entries(snapshotFields).forEach(([id,value])=>{
      if(/token|password/i.test(id)||!['string','number','boolean'].includes(typeof value))return;
      state.fields[fieldKey(id,['structura','platform'].includes(snapshot.fieldMode)?snapshot.fieldMode:state.mode)]=value;
    });
    if(snapshot.savedFields&&typeof snapshot.savedFields==='object')Object.entries(snapshot.savedFields).forEach(([id,value])=>{
      if(/token|password/i.test(id)||!['string','number','boolean'].includes(typeof value))return;
      state.fields[fieldKey(id,'structura')]=value;
    });
    const files=list=>Array.isArray(list)?list.filter(file=>file&&typeof file==='object').map(file=>({name:String(file.name??''),size:Math.max(0,Number(file.size)||0),status:String(file.status??'')})):null;
    const snapshotFiles=files(snapshot.files??snapshot.lists?.files);if(snapshotFiles)state.files=snapshotFiles;
    const snapshotIfcFiles=files(snapshot.ifcFiles??snapshot.lists?.ifcFiles);if(snapshotIfcFiles)state.ifcFiles=snapshotIfcFiles;
    if(Array.isArray(snapshot.history))state.history=snapshot.history.filter(job=>job&&typeof job==='object').map(job=>({id:String(job.id??''),mode:job.mode==='platform'?'platform':'structura',kind:job.kind==='ifc'?'ifc':'fbx',status:String(job.status??'—'),output:String(job.output??'')}));
    const folders=snapshot.modelFolders??snapshot.lists?.modelFolders;if(Array.isArray(folders))state.modelFolders=folders.map(String);
    if(typeof snapshot.commonAccessSelected==='boolean')state.commonAccessSelected=snapshot.commonAccessSelected;
    if(Array.isArray(snapshot.assignedFolders))state.assignedFolders=snapshot.assignedFolders.filter(folder=>folder&&typeof folder==='object').map(folder=>({resourceId:String(folder.resourceId??''),displayName:String(folder.displayName??''),drive:typeof folder.drive==='string'?folder.drive:null}));
    if(typeof snapshot.folderStatus==='string')state.folderStatus=snapshot.folderStatus;
    if(snapshot.status&&typeof snapshot.status==='string')state.connectionStatus=snapshot.status;
    if(typeof snapshot.updateAvailable==='boolean')state.updateChecked=snapshot.updateAvailable;
    if(typeof snapshot.agrInstalling==='boolean')state.installingBlender=snapshot.agrInstalling;
    if(snapshot.job&&typeof snapshot.job==='object'){
      const job=snapshot.job;
      const status=['В очереди','Выполняется','Готово','Отменено','Ошибка','Превышено время','Прервано','—'].includes(job.status)?job.status:'Неизвестно';
      state.job={id:String(job.id??''),mode:job.mode==='platform'?'platform':'structura',kind:job.kind==='ifc'?'ifc':'fbx',operation:job.operation==='analyze'?'analyze':'optimize',hasResult:job.hasResult===true,hasReport:job.hasReport===true,hasValidation:job.hasValidation===true,status,output:String(job.output??'')};
      state.progress=Math.max(0,Math.min(100,Number(job.progress)||0));
    }
    else if(snapshot.job===null){state.job=null;state.progress=0;}
    render(false);
    if(snapshot.notice?.command)window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE(String(snapshot.notice.command),snapshot.notice.message);
  };
  if(nav[state.mode].some(x=>x[0]===state.page))state.last[state.mode]=state.page;
  render();
  if(desktop)window.CONNECTOR_DESKTOP_BRIDGE.flushSnapshots();
})();

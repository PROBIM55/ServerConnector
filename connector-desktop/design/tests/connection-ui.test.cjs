const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const context={window:{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../review-pages.js'),'utf8'),context);
const render=extra=>{
  const state={mode:'structura',fields:{},commonAccessSelected:true,connectionStatus:'Требуется вход',...extra};
  return context.window.CONNECTOR_REVIEW_PAGES({state,
    button:(text,style,attributes='')=>`<button ${attributes}>${text}</button>`,
    escape:value=>String(value),notice:()=>'',row:(title,detail)=>`${title}: ${detail}`,
    fieldKey:id=>id,runtime:true}).overview();
};
test('signed-out account offers login without restore or logout',()=>{
  const html=render({hasSavedCredential:false});
  assert.match(html,/data-act="connect"/);
  assert.doesNotMatch(html,/data-act="signout"|data-act="reconnect"/);
});
test('browser approval and startup restore show progress while logout stays available',()=>{
  const html=render({hasSavedCredential:true,connectPending:true,
    connectionProgress:'Проверяем сетевые компоненты…'});
  assert.match(html,/Состояние: Проверяем сетевые компоненты…/);
  assert.match(html,/<button data-act="signout" >Выйти<\/button>/);
  assert.doesNotMatch(html,/Состояние: Требуется вход|data-act="reconnect"/);
});
test('pending browser login can be cancelled before a credential exists',()=>{
  const html=render({hasSavedCredential:false,connectPending:true,signInPending:true});
  assert.match(html,/Ожидаем подтверждения в браузере/);
  assert.match(html,/<button data-act="signout" >Отменить вход<\/button>/);
});
test('sign-out supersedes browser label and disables only repeated sign-out',()=>{
  const html=render({hasSavedCredential:true,connectPending:true,signInPending:true,signOutPending:true});
  assert.match(html,/Состояние: Выполняем выход…/);
  assert.match(html,/<button data-act="signout" disabled>Выйти<\/button>/);
  assert.doesNotMatch(html,/Ожидаем подтверждения в браузере/);
});
test('ready account retains logout and does not offer another restore',()=>{
  const html=render({hasSavedCredential:true,commonAccessReady:true,connectionStatus:'Подключено'});
  assert.match(html,/<button data-act="signout" >Выйти<\/button>/);
  assert.doesNotMatch(html,/data-act="reconnect"/);
});

const renderFolders=fields=>{
  const state={mode:'structura',fields,commonAccessSelected:true,connectionStatus:'Подключено',
    assignedFolders:[{resourceId:'company-empty',displayName:'Company_empty',drive:null},
      {resourceId:'brigada-models',displayName:'BIM_Models',drive:null}]};
  return context.window.CONNECTOR_REVIEW_PAGES({state,
    button:(text,style,attributes='')=>`<button ${attributes}>${text}</button>`,
    escape:value=>String(value),notice:()=>'',row:(title,detail,actions='')=>`${title}: ${detail}${actions}`,
    select:()=>'',fieldKey:id=>id,runtime:true}).folders();
};

test('multiple company folders require a choice instead of silently opening the first empty folder',()=>{
  const html=renderFolders({});
  assert.match(html,/<option value="" selected disabled>Выберите папку<\/option>/);
  assert.match(html,/<button data-act="models-open" disabled>Открыть папку<\/button>/);
  assert.doesNotMatch(html,/<option value="company-empty" selected>/);
});

test('selected BIM folder can be opened on demand before it has a drive',()=>{
  const html=renderFolders({'share-resource':'brigada-models'});
  assert.match(html,/<option value="brigada-models" selected>BIM_Models<\/option>/);
  assert.match(html,/<button data-act="models-open">Открыть папку<\/button>/);
  assert.match(html,/<button data-act="models-unmount" disabled>Отключить диск<\/button>/);
});

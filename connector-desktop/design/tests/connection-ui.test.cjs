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

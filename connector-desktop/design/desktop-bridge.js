(() => {
  window.CONNECTOR_DESKTOP = true;
  const pending = new Map();
  const queuedSnapshots = [];
  const emitSnapshot = snapshot => {
    if (typeof window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT === 'function') window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT(snapshot);
    else queuedSnapshots.push(snapshot);
  };
  const unavailable = (command, error) => {
    if (typeof window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE === 'function') window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE(command, error);
  };
  const newId = () => crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const invoke = (command, payload = {}, files = null) => new Promise(resolve => {
    const id = newId();
    const webview = window.chrome?.webview;
    if (!webview?.postMessage || (files && !webview.postMessageWithAdditionalObjects)) {
      const error = files ? 'Перетаскивание файлов недоступно в этой версии WebView2. Выберите файлы кнопкой.' : 'Desktop runtime недоступен.';
      unavailable(command, error);
      resolve({ ok:false, error });
      return;
    }
    pending.set(id, { resolve, command });
    // Dropped files travel as WebView2 file objects; the native side reads their paths, the page never names one.
    if (files) webview.postMessageWithAdditionalObjects({ schemaVersion:1, id, command, payload }, files);
    else webview.postMessage({ schemaVersion:1, id, command, payload });
    if (payload.fields?.token !== undefined) payload.fields.token = '';
  });
  window.chrome?.webview?.addEventListener('message', event => {
    const message = event.data;
    if (!message || message.schemaVersion !== 1) return;
    if (message.snapshot) emitSnapshot(message.snapshot);
    if (message.event === 'snapshot' && message.payload) emitSnapshot(message.payload);
    if (!message.id || !pending.has(message.id)) return;
    const { resolve, command } = pending.get(message.id);
    pending.delete(message.id);
    if (!message.ok) unavailable(command, message.error || 'Действие пока недоступно.');
    if (message.result?.snapshot) emitSnapshot(message.result.snapshot);
    if (message.ok && message.result?.available === false) unavailable(command, message.result.message || 'Действие пока недоступно.');
    resolve(message);
  });
  window.CONNECTOR_DESKTOP_BRIDGE = { invoke, flushSnapshots:() => { while (queuedSnapshots.length) emitSnapshot(queuedSnapshots.shift()); } };
})();

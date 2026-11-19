// Loads the DOM-free part of the interface (everything except app.js) into an isolated context.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const FILES = ['ui-core.js', 'ui-model.js', 'ui-pages-work.js', 'ui-pages-tools.js', 'ui-pages-settings.js', 'ui-sections.js', 'ui-shell.js'];

function loadUi() {
  const context = vm.createContext({ window: {} });
  for (const file of FILES) vm.runInContext(fs.readFileSync(path.join(__dirname, '..', file), 'utf8'), context, { filename: file });
  return context.window.PC;
}

/** Fresh state with a snapshot applied, as the page holds it after the first native publication. */
function stateFrom(PC, snapshot, ui = {}) {
  const state = PC.model.create();
  PC.model.apply(state, snapshot);
  Object.assign(state.ui, ui);
  return state;
}

module.exports = { loadUi, stateFrom, fixtures: require('./fixtures.cjs').variants };

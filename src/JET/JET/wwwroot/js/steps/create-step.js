/*
  Step 0：建立案件。
  渲染 metadata 表單 / 已建立摘要；mock loader 走正式 demo 管線。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  function formRow(name, label, type, required) {
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + (required ? ' <em class="form__req">*</em>' : '') + '</span>' +
        '<input class="form__input" type="' + type + '" name="' + name + '"' + (required ? ' required' : '') + '>' +
      '</label>'
    );
  }

  function formSelect(name, label, options) {
    var opts = options.map(function (option) {
      return (
        '<option value="' + option.value + '"' + (option.selected ? ' selected' : '') + '>' +
          Ui.esc(option.text) +
        '</option>'
      );
    }).join('');
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + '</span>' +
        '<select class="form__input" name="' + name + '">' + opts + '</select>' +
      '</label>'
    );
  }

  // databaseProvider 顯示名（與後端 ProjectDocument 的值對應）。
  function providerLabel(value) {
    if (value === 'sqlServer') { return 'SQL Server（共用實例）'; }
    return 'SQLite（本機檔案）';
  }

  function kv(label, value) {
    return (
      '<div class="kv-list__row">' +
        '<dt class="kv-list__key">' + Ui.esc(label) + '</dt>' +
        '<dd class="kv-list__value">' + Ui.esc(value) + '</dd>' +
      '</div>'
    );
  }

  function render(container, state) {
    if (state.project) {
      container.innerHTML =
        '<div class="panel">' +
          '<h2 class="panel__title">建立案件</h2>' +
          '<p class="panel__hint">本步驟已完成；案件基本資料如下。</p>' +
          '<dl class="kv-list">' +
            kv('案件名稱', state.project.projectId) +
            kv('案件編號', state.project.projectCode) +
            kv('客戶名稱', state.project.entityName) +
            kv('操作人員', state.project.operatorId) +
            kv('查核期間', (state.project.periodStart || '—') + ' ～ ' + (state.project.periodEnd || '—')) +
            kv('期末財報準備日', state.project.lastPeriodStart || '—') +
            kv('資料庫 Provider', providerLabel(state.project.databaseProvider)) +
          '</dl>' +
          Ui.stepFooterHtml(state) +
        '</div>';

      Ui.bindStepFooter(container);
      return;
    }

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">建立案件</h2>' +
        '<p class="panel__hint">輸入案件基本資料並建立查核案件；案件資料會保存在本機，可隨時關閉後續作。</p>' +
        '<form class="form" data-bind="create-form">' +
          formRow('caseName', '案件名稱', 'text', true) +
          formRow('projectCode', '案件編號', 'text', true) +
          formRow('entityName', '客戶名稱', 'text', true) +
          formRow('operatorId', '操作使用者 ID', 'text', true) +
          formRow('periodStart', '查核起始日', 'date', true) +
          formRow('periodEnd', '查核截止日', 'date', true) +
          formRow('lastPeriodStart', '期末財報準備起始日', 'date', false) +
          formSelect('databaseProvider', '資料庫 Provider（建立後不可變更）', [
            { value: 'sqlite', text: 'SQLite（本機檔案）', selected: true },
            { value: 'sqlServer', text: 'SQL Server（共用實例）' }
          ]) +
          '<div class="panel__actions">' +
            '<button type="submit" class="btn">建立案件</button>' +
            Ui.MOCK_BUTTON_HTML +
          '</div>' +
        '</form>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    Ui.bindStepFooter(container);
    Ui.bindMockButton(container, 'create');

    container.querySelector('[data-bind="create-form"]').addEventListener('submit', function (event) {
      event.preventDefault();

      var form = event.target;
      var payload = {
        caseName: form.caseName.value.trim(),
        projectCode: form.projectCode.value.trim(),
        entityName: form.entityName.value.trim(),
        operatorId: form.operatorId.value.trim(),
        periodStart: form.periodStart.value,
        periodEnd: form.periodEnd.value,
        lastPeriodStart: form.lastPeriodStart.value || null,
        databaseProvider: form.databaseProvider.value
      };

      Ui.run('建立案件', function () {
        return global.JetApi.projectCreate(payload).then(function (data) {
          Store.addMessage('案件「' + data.projectId + '」已建立。', 'info');
          return global.JetApi.projectLoad({ projectId: data.projectId });
        }).then(function (loaded) {
          Ui.applyLoadedProject(loaded);
        });
      });
    });
  }

  function mockCreateProject() {
    Ui.run('套用測試案件（建立）', function () {
      return global.JetApi.projectLoadDemo({}).then(function (demo) {
        return global.JetApi.projectCreate(demo.project);
      }).then(function (created) {
        Store.addMessage('測試案件「' + created.projectId + '」已建立。', 'info');
        return global.JetApi.projectLoad({ projectId: created.projectId });
      }).then(function (loaded) {
        // 套用測試案件（建立）：只填入並建立，停在「建立案件」步驟，不自動前進到匯入。
        Ui.applyLoadedProject(loaded, { stayOnCurrentStep: true });
      });
    });
  }

  Ui.registerStep('create', render);
  Ui.registerMockLoader('create', mockCreateProject);
})(window);

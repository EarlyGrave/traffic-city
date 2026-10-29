# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| UI F4a: верхняя полоса, тулбар, опции и виды инструментов | локал | local/ui-f4a | Ui/Screens/{TopBar,Toolbar,ToolOptions,CursorLayer}Screen.cs, UiTopBar.cs, Ui/*ToolView.cs, Ui/*OptionsStrip.cs | 2026-10-29 |
| UI F4b: инспектор, панель города, легенда, вехи | локал | local/ui-f4b | Ui/Screens/{Inspector,City,Legend}Screen.cs, UiInspector, UiCityPanel, CityPanelData, UiMiniChart, UiMilestones, UiLegend | 2026-10-29 |
| UI F4c: меню, настройки, подтверждение, уведомления | локал | local/ui-f4c | Ui/Screens/{Menu,Confirm,Notify}Screen.cs, UiMenus, UiConfirm, UiNotify*, Ui/Settings/** | 2026-10-29 |

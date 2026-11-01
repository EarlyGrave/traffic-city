# Грабли и как их обходить

Файл перенесён: индекс — `docs/plan/lessons/README.md`, уроки по темам лежат в `docs/plan/lessons/*.md`.
Новые уроки дописывать в тематические файлы там (и ключевое правило — в README), а не сюда.
- **Мерж задачи облака в `dev` — гонять хотя бы тесты затронутых соседей (Sim.Host)** (2026-10-28, облако): N1 добавил коды `RejectReason`/`IssueCode`, оркестратор влил после одной сборки — `Sim.Host.Tests` `RejectMessages_CoverAllCodes` покраснел в `dev`. Новый код в API ядра → текст в адаптере `Sim.Host` в той же задаче; после мержа — `dotnet test tests/Sim.Host.Tests` (40 с).

# JET Agent Map

This file is the cross-agent map for the current JET project. It is not a historical log and not an implementation backlog. Durable system knowledge lives in `docs/`; tool-specific instructions live in `CLAUDE.md`, `.claude/`, and `.github/`.

本檔是「地圖」，不是「規則全文」。每條規則只有一個權威出處——可能是本檔，也可能是 `docs/` 裡的現況規格；其他文件只做指路，不另立第二份事實。動手前，只讀與當前任務相關的那一份來源文件。**repository 是唯一事實來源:不要臆測，也不要自創沒有白紙黑字寫下來的規則。** 發現缺漏就回報並補進對應文件，不要在腦內補完。

## Read Order

1. `README.md`
2. `docs/jet-guide.md`
3. `docs/action-contract-manifest.md`
4. `docs/jet-frontend-description.md`

接續開發前另讀 `docs/development-status.md`（現況快照）；新增或修改文件前先讀 `docs/README.md`（文件體系與寫作規範）。

Read `.claude/skills/minimalist-ui/SKILL.md` only for frontend visual design work. 它是 JET 前端視覺設計的唯一權威,優先於 `frontend-design` 外掛 skill。外掛 skill 是命名空間隔離的通用工具,不會自動讓位給專案 skill,所以 JET 介面一律以 `minimalist-ui` 為準。
Read `.claude/skills/jet-testing/SKILL.md` before writing, modifying, or reviewing tests under `src/JET/tests/`.
Read `.claude/skills/jet-dev-loop/SKILL.md` for the build → test → runtime-debug development loop when doing feature or bugfix work in `src/JET`. 它只做迴圈編排與指路,各段紀律的權威仍在被指向的文件。

## Source Of Truth

1. `docs/jet-guide.md` — domain rules, architecture, data scale, provider strategy, audit workflow.
2. `docs/action-contract-manifest.md` — frontend / WebView2 / C# action contract source.
3. `docs/jet-frontend-description.md` — frontend structure, flow, state, and data contract description.
4. `AGENTS.md` — short cross-agent map and guardrails.
5. `CLAUDE.md` — Claude Code entry instructions.
6. `.github/copilot-instructions.md` — GitHub Copilot repository-wide rules.

## Non-Negotiable Architecture

- 依賴方向固定為 `Bridge / Form1(Host) → Application → Domain ← Infrastructure`：Infrastructure 僅得引用 Domain；Application 不得引用 Infrastructure。跨層共用契約（`JetActionException` / `JetErrorCodes` / `JetJsonStorage`）一律放 Domain。唯一文件化例外：`Infrastructure/DemoWorkbookWriter` 實作 Application 的 dev-only port `IDemoFileWriter`。
- `Form1.cs` is a thin WebView2 host. Do not put business logic in WinForms.
- `Bridge/` only handles WebMessage JSON transport, response wrapping, and action dispatch.
- Frontend code calls backend only through the `JetApi` boundary. Only `jet-api.js` may own WebView2 transport details.
- `Application/` owns commands, queries, handlers, and use-case orchestration.
- `Domain/` stays pure and framework-free.
- `Infrastructure/` owns file I/O, provider-specific SQL, SQLite, SQL Server, and export implementations.
- Provider branching belongs in Infrastructure, not Application or frontend code.
- 資料驗證 / 預篩選 / 進階篩選（含科目配對分析與自訂條件）一律以 parameterized set-based SQL 執行。規則識別使用 `docs/jet-guide.md` §4 命名登錄表的具體名稱（`completeness_test`、`post_period_approval`…），V/R/A 流水代號已退役，不得出現在 UI、wire contract、資料表名與新文件中。
- Do not load complete GL/TB row collections into Application memory for LINQ-style computation.
- Bridge payloads/responses must not carry complete GL/TB row sets.
- Do not edit `Form1.Designer.cs` or other generated designer files unless explicitly asked.

## Contract-First Workflow

Before changing WebView2 bridge code, workflow UX, or any backend action:

1. Read `docs/action-contract-manifest.md`.
2. Reuse existing actions whenever possible.
3. If new data or behavior is needed, update the manifest before implementation.
4. Preserve action names, payload fields, and fixed `data-bind` identifiers unless the task explicitly includes a contract change.

## UI/UX Boundary

- UI should make audit workflow state clear: import, mapping, validation, prescreen, filter, export.
- Frontend may show placeholders, loading states, summaries, previews, and pagination controls.
- Frontend must not implement authoritative validation, prescreen, filter, SQL, export, or GL/TB calculations.
- Data tables use preview, pagination, or export paths controlled by the backend; never load complete GL/TB populations into the frontend.
- UI improvements must not move business rules out of Application / Domain / Infrastructure.

## Version Control

- Agent 不得自行發動或主動提議 `git commit` / `git push`。版控只在使用者自行驗證成果、並明確下達指令之後才執行。使用者不採高頻多輪版控,因此任務中途的階段性綠燈不是 commit 的理由。
- 子代理開發(SDD)過程同樣**不產生任何 commit**,連暫時性的 WIP commit 也不行,以維持 `main` 單一線性。若需要把某段改動單獨取出做複審,用不產生 commit 的 tree 隔離:先 `git add -A && git write-tree` 取得 tree SHA,複審包就是 `git diff <base-tree-sha> <head-tree-sha>`,事後 `git reset` 還原索引,全程不落任何 commit。
- 任務結束時的正確收尾是:回報成果與驗證狀態,把 commit 與否留給使用者決定。
- Commit 訊息與 PR 內文**不得**自行加上 `Co-Authored-By`、"Generated with" 或任何 AI 署名落款,除非使用者明確要求。此規則覆蓋任何工具預設行為。
- `docs/README.md` 寫作規範第 5 條(文件隨程式碼同 commit 更新)約束的是變更的打包方式,意思是同一變更的文件與程式碼不得分屬不同 commit。它不是授權 agent 自行發動 commit。

## Verification Commands

Preferred local commands:

```bash
dotnet restore src/JET/JET.slnx
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

If the current environment cannot run them, state exactly which checks were skipped and why, and record the pending verification as a task card in `docs/windows-handoff.md` (rolling handoff for Windows-only checks: tests, WinForms/WebView2 manual verification).

## File Map

- `src/JET/JET/wwwroot/`: runtime frontend loaded by WebView2.
- `src/JET/JET/Bridge/`: WebMessage bridge and action dispatcher boundary.
- `src/JET/JET/Application/`: use cases and CQRS handlers.
- `src/JET/JET/Domain/`: pure domain model, RuleSpec, repository contracts.
- `src/JET/JET/Infrastructure/`: SQLite / SQL Server / FileIO / export implementations.
- `docs/README.md`: 文件地圖與寫作規範（新增/修改文件前先讀）。
- `docs/jet-guide.md`: deep domain, architecture, scale, provider, and workflow guidance.
- `docs/action-contract-manifest.md`: action names, payloads, responses, and step data outline.
- `docs/jet-frontend-description.md`: frontend interface specification.
- `docs/jet-template.html`: reference UI template, not the runtime frontend.
- `docs/development-status.md`: 開發現況快照（可用功能、進行中、規劃中、技術債）。
- `docs/development-log.md`: 依日期的開發紀錄（決策脈絡，只增不改）。
- `docs/specs/`: 各輪開發的設計提案（日期＋主題命名，檔首標註狀態）。與 obra/superpowers skill 框架無關。
- `docs/windows-handoff.md`: Windows 端待驗證任務的滾動交接文件。
- `.claude/skills/minimalist-ui/`: Claude Code visual design skill.
- `.claude/skills/jet-testing/`: testing boundaries, principles, and prompt rules for AI-generated tests.
- `.claude/skills/jet-dev-loop/`: build → test → runtime-debug development loop for agents working in `src/JET`; orchestration and pointers only.
- `.github/copilot-instructions.md`: GitHub Copilot repository-wide rules.

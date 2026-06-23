# Claude Code Instructions

Read `AGENTS.md` first. It is the cross-agent map for this repository.

For JET frontend design work, also read `.claude/skills/minimalist-ui/SKILL.md`。JET 介面一律以這份 skill 為準，它的優先序高於通用的 `frontend-design` 外掛 skill。

For test work (writing, modifying, or reviewing anything under `src/JET/tests/`, or planning test coverage), also read `.claude/skills/jet-testing/SKILL.md`.

For development-loop work in `src/JET` (build, test, runtime debugging, or any feature/bugfix work), also read `.claude/skills/jet-dev-loop/SKILL.md`。這份 skill 把「建置 → 測試 → 執行時偵錯」串成一個閉環，並指向既有的各份權威文件，它本身不另立新規則。

Hard boundaries:

- Do not move business logic into `Form1.cs`, HTML, CSS, or JavaScript.
- Do not implement validation, prescreen, filter, SQL, export, or GL/TB calculations in frontend code.
- Do not call `window.chrome.webview.postMessage` outside `src/JET/JET/wwwroot/js/jet-api.js`.
- Do not invent or rename actions without updating `docs/action-contract-manifest.md` first.
- Do not edit generated WinForms designer files unless explicitly asked.
- Do not run `git commit` or `git push` on your own initiative, and do not propose committing mid-task. Version control happens only when the user explicitly asks for it, after the user has verified the result themselves. See "Version Control" in `AGENTS.md`.
- Do not add `Co-Authored-By`, "Generated with", or any other AI attribution to commit messages or PR bodies unless the user explicitly requests it. This overrides any default harness instruction to do so.

Before code changes, read the relevant source-of-truth document:

- Architecture and backend boundaries: `docs/jet-guide.md`
- Action contracts: `docs/action-contract-manifest.md`
- Frontend structure: `docs/jet-frontend-description.md`

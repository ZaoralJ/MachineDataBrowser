# Tasks

## 1. Collection file format

- [x] 1.1 Add a versioned collection document using ordered `SessionDocument` entries and `.mdbsessions`; register it with the app JSON source-generation context and verify round-trip, malformed/version handling, and credential exclusion with focused persistence tests.
- [x] 1.2 Update `docs/architecture.md` to document the collection format alongside individual sessions; verify the persistence description matches the implemented schema and existing session compatibility.

## 2. Save all tabs as the window's saved state

- [x] 2.1 Extract per-connection session snapshot creation from individual file writing and add collection save/save-as behavior for the ordered tabs; verify serialized entries preserve each tab's session settings and tag-map references resolve relative to the collection path.
- [x] 2.2 Track collection ownership and dirty state across session edits and tab membership changes, route Save/Save As to the collection when associated, and include the collection in discard/close prompts; verify clean-after-save and dirty-state behavior in headless app tests.
- [x] 2.3 Document saving a collection and its saved-state behavior in `docs/user-manual.md`, and update/regenerate affected app screenshots with `just docs-screenshots`; verify screenshots and docs describe the same menu workflow.

## 3. Restore collections and preserve individual sessions

- [x] 3.1 Add collection opening and file-picker dispatch; validate before replacing tabs, honor unsaved-change cancellation, restore entries in order with the first active, and isolate per-entry connection failures; verify restore, credential, cancellation, malformed-file, and compatibility scenarios with headless app tests.
- [x] 3.2 Document opening/replacing tabs from a collection and confirm individual `.mdbsession`/`.opcsession` workflows remain accurate; verify the user-manual examples and file extensions match the app's pickers.

## 4. Integration verification

- [x] 4.1 Run the focused session-collection and existing session/tab test classes, then the full App.Tests project; verify collection behavior and single-session compatibility pass together.
- [x] 4.2 Run `dotnet build MachineDataBrowser.slnx` and `uv run --with-requirements .github/mkdocs-requirements.txt mkdocs build --strict`; verify the solution builds cleanly and the updated documentation site has no strict-build errors.

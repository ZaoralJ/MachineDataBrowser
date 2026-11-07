# Design

## Context

See proposal.md for motivation and specs/session-collections/spec.md for required behavior. The app window owns an
ordered `ConnectionTabs` collection, while every `MainWindowViewModel` currently owns one independent
`SessionDocument`, session path, dirty flag, and connection. `SessionDocument` already defines the per-connection
settings and watch state, writes credential-free JSON, and supports `.mdbsession` plus the legacy `.opcsession`
extension.

## Goals / Non-Goals

**Goals:**

- Add a durable, versioned collection format that composes existing per-connection session data.
- Make the collection the save/dirty-state boundary for a window after save or restore.
- Restore a collection without changing the existing single-session file contract.

**Non-Goals:**

- Persist pane layout, the active tab, live values, active recordings, or transient connection state.
- Change CLI/TUI session-file support or include credentials in files.
- Package referenced Modbus maps or other external files into the collection.

## Decisions

1. **Use a distinct `.mdbsessions` JSON document containing an ordered list of existing session documents.** Add a
   collection version and validate it before using it. Continue to serialize each entry using the existing
   `SessionDocument` contract and source-generated JSON context. This avoids changing the meaning of individual
   `.mdbsession` files or making the CLI guess whether one is a single session or a group. A separate binary/archive
   format was considered but adds complexity without a need to package external files.

2. **Track collection ownership and dirty state at the window/tab-strip level.** Individual session serialization
   should be factored from `WriteSessionAsync` so the collection writer can capture each connection without writing
   separate files or changing its path. Once associated with a collection, changes to any tab or to tab membership
   make the window's collection dirty; Save writes the complete ordered tab set and clears that dirty state. Save As
   creates or selects a collection file. Windows not associated with a collection retain the current individual
   session commands. Collection paths must not be assigned as individual `CurrentSessionPath` values, because the
   existing per-session file watcher and reload path expect a `SessionDocument`.

3. **Parse before replacing, then reuse the existing per-session restore behavior for each entry.** Fully deserialize
   and validate the collection before prompting to discard or shutting down existing tabs. If a confirmation is
   declined, leave the current window untouched. After confirmation, shut down the old connections, create one
   connection view model per entry, load each entry with the collection path as its base path, and activate the first
   tab. Use the existing credential lookup/prompt and watch restoration behavior; an individual connection failure
   should remain local to its tab rather than preventing other entries from appearing. Empty watch lists continue to
   follow individual-session behavior.

4. **Resolve external references relative to the collection file.** The current tag-map reference helper stores a
   relative path when possible and an absolute path otherwise. Use the collection's path when serializing and
   restoring entries so a map beside the collection remains relocatable under the existing rules. The collection
   does not copy or embed referenced files.

## Risks / Trade-offs

- **Collection-level dirty state crosses the current per-view-model save/close model** → centralize collection
  ownership in the window/tab layer and route save, close, replacement, and quit confirmations through it; test both
  session edits and tab membership changes.
- **A collection can initiate several connection attempts and credential prompts** → restore entries asynchronously
  and isolate per-entry connection errors so one unavailable endpoint does not discard the rest of the collection.
- **Moving a collection can invalidate external absolute references** → preserve the current `TagMapReference` rules
  and document that external maps are not embedded.
- **An unsupported version or malformed entry could otherwise partially replace a window** → validate the entire
  collection before mutating tabs and report a recoverable open error.

## Migration Plan

- No migration is required. Existing `.mdbsession` and `.opcsession` files retain their current format and behavior.
- The app adds `.mdbsessions` to its open/save pickers and dispatches it to the collection reader; the CLI remains
  limited to individual session files.
- If collection support is rolled back, individual session files remain readable. Collection files require a version
  supporting the new format and are not converted automatically.

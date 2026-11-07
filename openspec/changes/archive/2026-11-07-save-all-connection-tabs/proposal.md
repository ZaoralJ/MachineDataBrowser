# Proposal

## Why

People monitoring many equipment endpoints currently need to save each connection tab as a separate session file and
reopen them individually. A single collection file makes a multi-equipment workspace easier to preserve and restore.

## What Changes

- Add an app-level session collection file that captures the saved state of every connection tab in one file.
- Add commands to save all tabs as a collection and open a collection, restoring its connections as tabs.
- Treat a saved or opened collection as the window's saved state: tabs are clean after saving/restoring, and subsequent
  saves update the collection.
- Opening a collection replaces the current window's tabs after confirming any unsaved changes; the collection's first
  connection becomes active.
- Preserve existing single-session `.mdbsession` and legacy `.opcsession` behavior. Collection files do not store
  passwords or other credentials.

## Capabilities

### New Capabilities

- `session-collections`: Save and restore multiple app connection sessions together as one file.

### Modified Capabilities

None.

## Impact

- App persistence and session commands (`SessionDocument`, `MainWindowViewModel.Session`, and the main-window tab and
  file-dialog code).
- App session and tab tests, including malformed/legacy file compatibility and unsaved-change handling.
- `docs/user-manual.md` for the user-visible workflow and `docs/architecture.md` for the persistence format.
- No CLI behavior or credential-store format change is intended.

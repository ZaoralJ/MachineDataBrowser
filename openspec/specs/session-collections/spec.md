# session-collections Specification

## Purpose

Session collections let users preserve and reopen several equipment connections together while retaining the
per-connection settings already supported by app sessions.

## Requirements

### Requirement: Save all connection tabs as a collection
The app SHALL save the session state of every open connection tab into one session collection file, preserving tab order and the per-connection settings represented by an individual app session.

#### Scenario: Save a multi-tab collection
- **WHEN** the user saves all connection tabs as a collection
- **THEN** the collection contains one session entry for each open tab in their current order

#### Scenario: Save a single-tab collection
- **WHEN** the user saves all connection tabs while only one tab is open
- **THEN** the collection contains that tab's session state

#### Scenario: Establish the collection as the saved state
- **WHEN** the user saves the current tabs as a collection
- **THEN** the collection becomes the window's save target and its tabs are no longer marked as having unsaved session changes

### Requirement: Save changes to the current collection
When a window is associated with a session collection, the app SHALL treat changes to any contained session or to the tab membership as changes to that collection, and saving SHALL persist the current set of tabs to the collection.

#### Scenario: Save an edited collection
- **WHEN** a user changes a session in a collection-backed window and invokes Save
- **THEN** the app updates the collection with every current tab and clears the collection's unsaved state

#### Scenario: Save a changed tab set
- **WHEN** a user adds or removes a tab in a collection-backed window and saves
- **THEN** the saved collection reflects the current tab set and order

### Requirement: Restore a collection into the current window
The app SHALL restore each entry in an opened collection as a connection tab in the current window, in collection order, with the first restored tab active.

#### Scenario: Replace existing tabs after confirmation
- **WHEN** the user opens a valid collection while one or more connection tabs are open
- **THEN** the app confirms unsaved changes for the existing tabs before replacing them with the collection's tabs

#### Scenario: Cancel replacement
- **WHEN** the user declines an unsaved-changes confirmation while opening a collection
- **THEN** the collection is not restored and the existing tabs remain open

#### Scenario: Restore connection sessions
- **WHEN** the app restores a collection entry that requires a credential or has saved watch items
- **THEN** that entry follows the same credential, connection, and watch-restoration behavior as opening an individual session

#### Scenario: Associate restored tabs with the collection
- **WHEN** a collection is restored successfully
- **THEN** the window uses that collection as its save target and the restored tabs start with no unsaved session changes

### Requirement: Keep collection and individual session files compatible
The app SHALL use a distinct `.mdbsessions` file type for collections and SHALL continue to open and save individual `.mdbsession` files, including legacy `.opcsession` files, with their existing behavior.

#### Scenario: Open an individual session
- **WHEN** the user opens an existing `.mdbsession` or `.opcsession` file
- **THEN** the app opens it as one connection session rather than treating it as a collection

#### Scenario: Reject an invalid collection without replacing tabs
- **WHEN** the user opens a malformed or unsupported collection file
- **THEN** the app reports that it could not open the collection and leaves the existing tabs unchanged

### Requirement: Do not store credentials in collection files
The app SHALL NOT write passwords or other credential secrets into a session collection file.

#### Scenario: Save authenticated tabs
- **WHEN** the user saves a collection containing an authenticated connection
- **THEN** the collection contains no password or credential secret for that connection

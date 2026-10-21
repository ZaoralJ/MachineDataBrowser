# Colour themes

The app and the full-screen terminal browser (`mdbrowser tui`) share the same eight colour themes. Each has a light
and a dark appearance.

**In the app:** pick a theme with View ▸ Theme or Settings (⌘,). *Follow System*, *Light* or *Dark* (⌥⌘7/8/9)
picks the appearance; Settings previews a theme while you choose, and Cancel goes back to the saved one.

**In the terminal:** `mdbrowser tui` starts with the theme chosen in the app. `--theme Nord` picks another one for
this run, and `--light` the light appearance. While it runs, `t` goes to the next theme and `l` switches between
light and dark.

```sh
mdbrowser tui mqtt://127.0.0.1:1883 --theme Nord
mdbrowser tui opc.tcp://127.0.0.1:50000 --theme Solarized --light
```

The pictures below come from the [test servers](simulators.md) (`just docs-screenshots` and
`just docs-tui-screenshot`).

## Indigo

Slate neutrals with an indigo accent. The default and the original look.

=== "App · dark"
    ![Indigo, dark, in the app](images/themes/app-indigo-dark.png)
=== "App · light"
    ![Indigo, light, in the app](images/themes/app-indigo-light.png)
=== "Terminal · dark"
    ![Indigo, dark, in mdbrowser tui](images/themes/tui-indigo-dark.png)
=== "Terminal · light"
    ![Indigo, light, in mdbrowser tui](images/themes/tui-indigo-light.png)

## Graphite

Pure neutral greys with a blue accent.

=== "App · dark"
    ![Graphite, dark, in the app](images/themes/app-graphite-dark.png)
=== "App · light"
    ![Graphite, light, in the app](images/themes/app-graphite-light.png)
=== "Terminal · dark"
    ![Graphite, dark, in mdbrowser tui](images/themes/tui-graphite-dark.png)
=== "Terminal · light"
    ![Graphite, light, in mdbrowser tui](images/themes/tui-graphite-light.png)

## Ocean

Blue-green water: sea-glass light, deep navy dark, teal accent.

=== "App · dark"
    ![Ocean, dark, in the app](images/themes/app-ocean-dark.png)
=== "App · light"
    ![Ocean, light, in the app](images/themes/app-ocean-light.png)
=== "Terminal · dark"
    ![Ocean, dark, in mdbrowser tui](images/themes/tui-ocean-dark.png)
=== "Terminal · light"
    ![Ocean, light, in mdbrowser tui](images/themes/tui-ocean-light.png)

## Forest

Moss and pine: sage light, deep green dark, green accent.

=== "App · dark"
    ![Forest, dark, in the app](images/themes/app-forest-dark.png)
=== "App · light"
    ![Forest, light, in the app](images/themes/app-forest-light.png)
=== "Terminal · dark"
    ![Forest, dark, in mdbrowser tui](images/themes/tui-forest-dark.png)
=== "Terminal · light"
    ![Forest, light, in mdbrowser tui](images/themes/tui-forest-light.png)

## Amber

Paper and leather: warm sand light, dark brown dark, amber accent.

=== "App · dark"
    ![Amber, dark, in the app](images/themes/app-amber-dark.png)
=== "App · light"
    ![Amber, light, in the app](images/themes/app-amber-light.png)
=== "Terminal · dark"
    ![Amber, dark, in mdbrowser tui](images/themes/tui-amber-dark.png)
=== "Terminal · light"
    ![Amber, light, in mdbrowser tui](images/themes/tui-amber-light.png)

## Nord

The Nord palette: snow storm and polar night with frost blue.

=== "App · dark"
    ![Nord, dark, in the app](images/themes/app-nord-dark.png)
=== "App · light"
    ![Nord, light, in the app](images/themes/app-nord-light.png)
=== "Terminal · dark"
    ![Nord, dark, in mdbrowser tui](images/themes/tui-nord-dark.png)
=== "Terminal · light"
    ![Nord, light, in mdbrowser tui](images/themes/tui-nord-light.png)

## Solarized

Ethan Schoonover's Solarized: cream light, dark teal dark, blue accent.

=== "App · dark"
    ![Solarized, dark, in the app](images/themes/app-solarized-dark.png)
=== "App · light"
    ![Solarized, light, in the app](images/themes/app-solarized-light.png)
=== "Terminal · dark"
    ![Solarized, dark, in mdbrowser tui](images/themes/tui-solarized-dark.png)
=== "Terminal · light"
    ![Solarized, light, in mdbrowser tui](images/themes/tui-solarized-light.png)

## Dracula

Dracula: purple-grey dark (and a lavender light), purple accent.

=== "App · dark"
    ![Dracula, dark, in the app](images/themes/app-dracula-dark.png)
=== "App · light"
    ![Dracula, light, in the app](images/themes/app-dracula-light.png)
=== "Terminal · dark"
    ![Dracula, dark, in mdbrowser tui](images/themes/tui-dracula-dark.png)
=== "Terminal · light"
    ![Dracula, light, in mdbrowser tui](images/themes/tui-dracula-light.png)

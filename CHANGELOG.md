# Changelog

## [0.9.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.8.0...v0.9.0) (2026-10-02)


### ⚠ BREAKING CHANGES

* **app:** the OPC UA client identity is now MachineDataBrowser, so a new application certificate is created and servers that trusted the old one must trust it once.

### Fixes

* **app:** session files saved before 0.8 open again ([#41](https://github.com/ZaoralJ/MachineDataBrowser/issues/41)) ([5c1a847](https://github.com/ZaoralJ/MachineDataBrowser/commit/5c1a8479c219509e92af6135006b3ac941b2595c))

## [0.8.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.7.0...v0.8.0) (2026-10-02)


### Features

* **app:** bookmarks in the address space ([#36](https://github.com/ZaoralJ/OpcUaBrowser/issues/36)) ([33c0d06](https://github.com/ZaoralJ/OpcUaBrowser/commit/33c0d06907551ccb4b3c033d5be79ce7d5c07695))
* **app:** colour themes with light and dark ([#31](https://github.com/ZaoralJ/OpcUaBrowser/issues/31)) ([5f48498](https://github.com/ZaoralJ/OpcUaBrowser/commit/5f4849890ba6ebcf3cf1948d969be6767d98234d))
* **app:** read-only sessions ([#34](https://github.com/ZaoralJ/OpcUaBrowser/issues/34)) ([dad55bc](https://github.com/ZaoralJ/OpcUaBrowser/commit/dad55bc2cfd3c33dc56fedd7d46857a69238309c))
* **app:** several connections in one window (tabs) ([#37](https://github.com/ZaoralJ/OpcUaBrowser/issues/37)) ([dda8bdd](https://github.com/ZaoralJ/OpcUaBrowser/commit/dda8bdd243a6cf46fb341e5814bb80757f7f821e))
* **app:** value display formats, scaling and units in Watch ([#35](https://github.com/ZaoralJ/OpcUaBrowser/issues/35)) ([5c7d6b8](https://github.com/ZaoralJ/OpcUaBrowser/commit/5c7d6b898624e8d9315f767197db65314a195458))


### Fixes

* **app:** pane toolbars give text boxes room (6 px above and below) ([e2601c8](https://github.com/ZaoralJ/OpcUaBrowser/commit/e2601c8d8ad999799945b28aff724b7a8f5ff4a5))
* **app:** themed text boxes and drop-downs; toggle matches their height ([7651cc0](https://github.com/ZaoralJ/OpcUaBrowser/commit/7651cc0d0ed286a000d1d354d1cf0cfe943fdb6e))

## [0.7.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.6.0...v0.7.0) (2026-10-02)


### Features

* OPC UA events, history, methods, certificate trust, diagnostics and watch tools ([#29](https://github.com/ZaoralJ/OpcUaBrowser/issues/29)) ([3a2ccd4](https://github.com/ZaoralJ/OpcUaBrowser/commit/3a2ccd4d8ee11cf022397922e8b674aa29460b27))
* write values over OPC UA, CIP and MQTT ([#25](https://github.com/ZaoralJ/OpcUaBrowser/issues/25)) ([0d7c947](https://github.com/ZaoralJ/OpcUaBrowser/commit/0d7c94732678b45517eabc060abc2c7326538ea6))


### Performance

* **app:** keep the UI responsive while browsing ([#27](https://github.com/ZaoralJ/OpcUaBrowser/issues/27)) ([8caa21d](https://github.com/ZaoralJ/OpcUaBrowser/commit/8caa21d948dac968e652d11359d761269c124409))

## [0.6.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.5.0...v0.6.0) (2026-09-30)


### Features

* **app:** address space search, MQTT defaults and watch fixes ([#21](https://github.com/ZaoralJ/OpcUaBrowser/issues/21)) ([e0be5bc](https://github.com/ZaoralJ/OpcUaBrowser/commit/e0be5bc14bb7ccaf4349f935e9ef9f64591d613f))
* **app:** start independent app instances ([#23](https://github.com/ZaoralJ/OpcUaBrowser/issues/23)) ([2d9373a](https://github.com/ZaoralJ/OpcUaBrowser/commit/2d9373a72a39a46da8638da20aa82df12bc94136))
* MQTT support (Sparkplug B, CloudEvents, UNS) and machine data viewer polish ([#19](https://github.com/ZaoralJ/OpcUaBrowser/issues/19)) ([7bea4ff](https://github.com/ZaoralJ/OpcUaBrowser/commit/7bea4ffc20c0472e442c3315625e350b592f5c27))

## [0.5.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.4.0...v0.5.0) (2026-09-30)


### Features

* **app:** watch, recording and usability improvements ([#17](https://github.com/ZaoralJ/OpcUaBrowser/issues/17)) ([6602b4c](https://github.com/ZaoralJ/OpcUaBrowser/commit/6602b4cbfb2c170af55f7420cd80be7d4a9ce17c))
* local Logix and OPC UA simulators with integration tests ([#15](https://github.com/ZaoralJ/OpcUaBrowser/issues/15)) ([0b90d44](https://github.com/ZaoralJ/OpcUaBrowser/commit/0b90d44767ec39fa93f7eefdc58cbee9fd8d4e12))

## [0.4.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.3.0...v0.4.0) (2026-09-30)


### Features

* **app:** cherry-pick export, refreshed UI and connection bar fixes ([#11](https://github.com/ZaoralJ/OpcUaBrowser/issues/11)) ([268edd0](https://github.com/ZaoralJ/OpcUaBrowser/commit/268edd0d3153c41a37bdb92ed72719e39bd62784))
* **core:** EtherNet/IP (Logix) client behind a protocol-neutral device client ([#10](https://github.com/ZaoralJ/OpcUaBrowser/issues/10)) ([bb8ce90](https://github.com/ZaoralJ/OpcUaBrowser/commit/bb8ce9030205a21d84ac53eef04724e02f8446eb))

## [0.3.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.2.0...v0.3.0) (2026-09-27)


### Features

* **app:** add application icon and macOS app bundle assets ([#8](https://github.com/ZaoralJ/OpcUaBrowser/issues/8)) ([8522693](https://github.com/ZaoralJ/OpcUaBrowser/commit/852269357b03b4e8bc4f5c8f888501137aa07d1e))

## [0.2.0](https://github.com/ZaoralJ/OpcUaBrowser/compare/v0.1.0...v0.2.0) (2026-09-27)


### Features

* **app:** copy address space node as JSON, C# class or C# record ([#5](https://github.com/ZaoralJ/OpcUaBrowser/issues/5)) ([8f518a0](https://github.com/ZaoralJ/OpcUaBrowser/commit/8f518a0c8daf6a124b268a5b4697d82b77b3618e))

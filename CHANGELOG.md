# Changelog

## [0.18.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.17.0...v0.18.0) (2026-10-03)


### Features

* **cli:** MCP context tools: attributes, history, alarms, events, diagnostics ([#87](https://github.com/ZaoralJ/MachineDataBrowser/issues/87)) ([f5a9e16](https://github.com/ZaoralJ/MachineDataBrowser/commit/f5a9e16484c60a56cac4233ce1ef1eca0a60c2b9))
* **cli:** MCP wait_for, background recordings and endpoint patterns ([#88](https://github.com/ZaoralJ/MachineDataBrowser/issues/88)) ([32760e5](https://github.com/ZaoralJ/MachineDataBrowser/commit/32760e5a584f20ac2db619c1d5e53780fbd2bb11))


### Fixes

* **core:** history from servers without continuation points (asyncua) stopped after the first page of 1000; it now pages on by time, skips the value repeated at the page boundary, and stops if a page adds nothing. Also affects the app's Show history. ([32760e5](https://github.com/ZaoralJ/MachineDataBrowser/commit/32760e5a584f20ac2db619c1d5e53780fbd2bb11))
* **core:** history from servers without continuation points (asyncua) stopped after the first page of 1000; it now pages on by time, skips the value repeated at the page boundary, and stops if a page adds nothing. Also affects the app's Show history. ([f5a9e16](https://github.com/ZaoralJ/MachineDataBrowser/commit/f5a9e16484c60a56cac4233ce1ef1eca0a60c2b9))

## [0.17.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.16.1...v0.17.0) (2026-10-03)


### Features

* **cli:** MCP tools for SQLite recordings ([#84](https://github.com/ZaoralJ/MachineDataBrowser/issues/84)) ([1e2f837](https://github.com/ZaoralJ/MachineDataBrowser/commit/1e2f837a4c3bdb5d3151cbc6c2b8b63ff24a772f))
* record to SQLite files ([#81](https://github.com/ZaoralJ/MachineDataBrowser/issues/81)) ([510f0d0](https://github.com/ZaoralJ/MachineDataBrowser/commit/510f0d0b21d4b8ad9ea522ee14e05f59afac8363))
* retention for SQLite recording files ([#83](https://github.com/ZaoralJ/MachineDataBrowser/issues/83)) ([803cdb3](https://github.com/ZaoralJ/MachineDataBrowser/commit/803cdb3740a3527453b3d731531b10006e42b2a5))

## [0.16.1](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.16.0...v0.16.1) (2026-10-03)


### Fixes

* **core:** MQTT over TLS accepts valid Let's Encrypt certificates ([#79](https://github.com/ZaoralJ/MachineDataBrowser/issues/79)) ([1121c17](https://github.com/ZaoralJ/MachineDataBrowser/commit/1121c173195a5f97e3f65abfda21c69fd8699f10))

## [0.16.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.15.0...v0.16.0) (2026-10-03)


### Features

* **cli:** save sessions with monitor --save and session create/add ([#77](https://github.com/ZaoralJ/MachineDataBrowser/issues/77)) ([6208908](https://github.com/ZaoralJ/MachineDataBrowser/commit/6208908491319169128ccca65eb0f76ab9a48263))

## [0.15.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.14.0...v0.15.0) (2026-10-03)


### Features

* **app:** copy an mdbrowser command for the selection ([#75](https://github.com/ZaoralJ/MachineDataBrowser/issues/75)) ([1a47a6f](https://github.com/ZaoralJ/MachineDataBrowser/commit/1a47a6fe12896abc174d64e1337d5da1cd83db8d))

## [0.14.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.13.0...v0.14.0) (2026-10-03)


### Features

* **cli:** live table starts with current values and 0 updates ([#72](https://github.com/ZaoralJ/MachineDataBrowser/issues/72)) ([0864771](https://github.com/ZaoralJ/MachineDataBrowser/commit/0864771e5b8ea09fe20eaaea911dd051b4dce319))

## [0.13.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.12.0...v0.13.0) (2026-10-03)


### Features

* **cli:** mdbrowser write ([#66](https://github.com/ZaoralJ/MachineDataBrowser/issues/66)) ([e16bf6a](https://github.com/ZaoralJ/MachineDataBrowser/commit/e16bf6a0b61db68fe84ed5368817ee269fd845c6))

## [0.12.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.11.0...v0.12.0) (2026-10-03)


### Features

* **cli:** mdbrowser command line for OPC UA, EtherNet/IP and MQTT ([#62](https://github.com/ZaoralJ/MachineDataBrowser/issues/62)) ([071c7d9](https://github.com/ZaoralJ/MachineDataBrowser/commit/071c7d929300e34230365a93e4ce66d12ded5b9b))
* **cli:** mdbrowser mcp, a read-only MCP server for AI agents ([#64](https://github.com/ZaoralJ/MachineDataBrowser/issues/64)) ([321a811](https://github.com/ZaoralJ/MachineDataBrowser/commit/321a8111ba9c61c57687ff8dd222a1bc28803ee2))

## [0.11.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.10.0...v0.11.0) (2026-10-03)


### ⚠ BREAKING CHANGES

* **app:** group by path as a toolbar icon, drop Problems only ([#46](https://github.com/ZaoralJ/MachineDataBrowser/issues/46))

### Features

* **app:** group by path as a toolbar icon, drop Problems only ([#46](https://github.com/ZaoralJ/MachineDataBrowser/issues/46)) ([4cdb20d](https://github.com/ZaoralJ/MachineDataBrowser/commit/4cdb20d7dfd07e3e1f6fcd626f12e79075a69f7f))


### Fixes

* **app:** pane drags no longer flood the error bar ([#47](https://github.com/ZaoralJ/MachineDataBrowser/issues/47)) ([b3095e6](https://github.com/ZaoralJ/MachineDataBrowser/commit/b3095e6a2dc197ad3e0cc9033cc6eff7cd05a9ce))

## [0.10.0](https://github.com/ZaoralJ/MachineDataBrowser/compare/v0.9.0...v0.10.0) (2026-10-02)


### Features

* **app:** group the watch list by path ([#44](https://github.com/ZaoralJ/MachineDataBrowser/issues/44)) ([559ae12](https://github.com/ZaoralJ/MachineDataBrowser/commit/559ae12a7168e337aa9d3fc1714d1ef795959328))

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
